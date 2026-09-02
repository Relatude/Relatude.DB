using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Indexes.KvStore;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.Utils;

namespace Relatude.Persistence;

#region datamodel
// value indexed and word indexed properties on one type, so a rollback is verified against both
// index kinds. InstantTextIndexing keeps the word indexes inside the transaction, so text searches
// answer right after Insert without waiting for the background queue.
[Node(TextIndex = BoolValue.True, InstantTextIndexing = BoolValue.True)]
public class RevArticle {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(IndexedByWords = true)]
    public string Body { get; set; } = "";
    [StringProperty(Indexed = true)]
    public string Category { get; set; } = "";
    [IntegerProperty(Indexed = true)]
    public int Number { get; set; }
}
#endregion

/// <summary>
/// Reverting: BeginRevertWindow / RollbackRevertWindow / CommitRevertWindow and the general
/// DeleteTransactionsAfter. A rollback must restore the exact pre-window state — node data, value
/// indexes, word indexes — leave the store writable, and the reverted state must survive a
/// restart. The engine matrix matters: the deferring engines (native KV, Lucene) must come back
/// without a reset (the cheap path), while the per-transaction-durable SQLite engine is reset and
/// rebuilt from the truncated log.
/// </summary>
[TestClass]
public class RevertTests {

    const int NodeCount = 40;

    static string tempDir() {
        var dir = Path.Combine(Path.GetTempPath(), "RelatudeDB_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Runs the factory the server host uses, so the slot assignment and the SQLite dual-role rule
    // (one instance serving value and text indexes when both are Sqlite) are the production ones.
    // "Memory" stands for no engine: the default id stays Guid.Empty.
    static Func<IndexEngines>? engineFactory(string v, string t, string dir) {
        return TestEngines.Factory(dir, TestEngines.Settings(value: v, text: t));
    }

    static NodeStore openStore(string dir, string v, string t) {
        var dm = new Datamodel();
        dm.Add<RevArticle>();
        var settings = TestEngines.Settings(value: v, text: t);
        return new NodeStore(DataStoreLocal.Open(dm, settings, new IOProviderDisk(dir), null, null, null, null,
            engineFactory(v, t, dir)));
    }

    static NodeStore openMemoryStore() {
        var dm = new Datamodel();
        dm.Add<RevArticle>();
        return new NodeStore(DataStoreLocal.Open(dm, new SettingsLocal(), null));
    }

    static Guid insertBaseNodes(NodeStore store) {
        Guid firstId = Guid.Empty;
        for (var i = 0; i < NodeCount; i++) {
            var id = Guid.NewGuid();
            if (i == 0) firstId = id;
            store.Insert(new RevArticle {
                Id = id,
                Body = i % 2 == 0 ? "waterproof canvas backpack" : "lightweight leather satchel",
                Category = i % 4 == 0 ? "outdoor" : "travel",
                Number = i % 10,
            });
        }
        return firstId;
    }

    /// <summary>Inserts, updates and deletes inside the window; returns the number of transactions made.</summary>
    static int mutate(NodeStore store, Guid updateId) {
        var transactions = 0;
        for (var i = 0; i < 10; i++) {
            store.Insert(new RevArticle { Id = Guid.NewGuid(), Body = "temporary experiment", Category = "extra", Number = 3 });
            transactions++;
        }
        var loaded = store.Get<RevArticle>(updateId);
        loaded.Category = "changed";
        loaded.Body = "rewritten prose";
        store.Update(loaded);
        transactions++;
        var doomed = store.Query<RevArticle>().Where(a => a.Category == "travel").Execute().First();
        store.Delete(doomed.Id);
        transactions++;
        return transactions;
    }

    static void verifyBaseState(NodeStore store, Guid updateId, string because) {
        Assert.AreEqual(NodeCount, store.Query<RevArticle>().Count(), "node count " + because);
        // value indexes
        Assert.AreEqual(NodeCount / 4, store.Query<RevArticle>().Where(a => a.Category == "outdoor").Count(), "value filter " + because);
        Assert.AreEqual(0, store.Query<RevArticle>().Where(a => a.Category == "extra").Count(), "inserted rows gone " + because);
        Assert.AreEqual(NodeCount / 10, store.Query<RevArticle>().Where(a => a.Number == 3).Count(), "integer filter " + because);
        // word indexes
        Assert.AreEqual(NodeCount / 2, store.Query<RevArticle>().WhereSearch("waterproof").Count(), "text search " + because);
        Assert.AreEqual(0, store.Query<RevArticle>().WhereSearch("temporary").Count(), "inserted text gone " + because);
        Assert.AreEqual(0, store.Query<RevArticle>().WhereSearch("rewritten").Count(), "updated text reverted " + because);
        // the update is undone and the deleted node is back
        Assert.AreEqual("outdoor", store.Get<RevArticle>(updateId).Category, "update reverted " + because);
    }

    static void verifyMutatedState(NodeStore store) {
        Assert.AreEqual(NodeCount + 10 - 1, store.Query<RevArticle>().Count(), "mutations were applied");
        Assert.AreEqual(10, store.Query<RevArticle>().Where(a => a.Category == "extra").Count());
        Assert.AreEqual(10, store.Query<RevArticle>().WhereSearch("temporary").Count());
    }

    [TestMethod]
    public void RevertWindow_Rollback_MemoryStore() {
        using var store = openMemoryStore();
        var updateId = insertBaseNodes(store);
        var windowTimestamp = store.BeginRevertWindow();
        Assert.IsNotNull(store.RevertWindow);
        Assert.AreEqual(windowTimestamp, store.RevertWindow!.Timestamp);

        var transactions = mutate(store, updateId);
        verifyMutatedState(store);

        var result = store.RollbackRevertWindow();
        Assert.IsNull(store.RevertWindow);
        Assert.AreEqual(transactions, result.TransactionsDeleted);
        Assert.AreEqual(windowTimestamp, result.LastTimestamp);
        Assert.IsFalse(result.StateAndIndexesReset);
        verifyBaseState(store, updateId, "after rollback");

        // the store stays writable and indexes new data normally
        store.Insert(new RevArticle { Id = Guid.NewGuid(), Body = "postrollback", Category = "outdoor", Number = 3 });
        Assert.AreEqual(1, store.Query<RevArticle>().WhereSearch("postrollback").Count());
        Assert.AreEqual(NodeCount / 4 + 1, store.Query<RevArticle>().Where(a => a.Category == "outdoor").Count());
    }

    [DataTestMethod]
    [DataRow("Memory", "Memory")]
    [DataRow("Native", "Memory")]
    [DataRow("Native", "Lucene")]
    [DataRow("Sqlite", "Sqlite")]
    public void RevertWindow_Rollback_EngineCombinations(string v, string t) {
        var dir = tempDir();
        try {
            Guid updateId;
            using (var store = openStore(dir, v, t)) {
                updateId = insertBaseNodes(store);
                store.BeginRevertWindow();
                mutate(store, updateId);
                verifyMutatedState(store);
                var result = store.RollbackRevertWindow();
                verifyBaseState(store, updateId, "after rollback (" + v + "/" + t + ")");
                if (v == "Sqlite") {
                    // SQLite is durable per transaction, so it must have been reset and rebuilt
                    Assert.AreEqual(1, result.EnginesReset.Length, "sqlite engine reset");
                } else {
                    // the deferring engines reopen at the window start: the cheap path, no resets
                    Assert.AreEqual(0, result.EnginesReset.Length, "no engine reset for " + v + "/" + t);
                }
                store.Insert(new RevArticle { Id = Guid.NewGuid(), Body = "postrollback", Category = "outdoor", Number = 3 });
            }
            // the reverted state (plus the write after it) must survive a restart
            using (var store = openStore(dir, v, t)) {
                Assert.AreEqual(NodeCount + 1, store.Query<RevArticle>().Count(), "count after reopen");
                Assert.AreEqual(0, store.Query<RevArticle>().WhereSearch("temporary").Count(), "rolled back text stays gone after reopen");
                Assert.AreEqual(1, store.Query<RevArticle>().WhereSearch("postrollback").Count(), "write after rollback survives reopen");
                Assert.AreEqual("outdoor", store.Get<RevArticle>(updateId).Category, "reverted update stays reverted after reopen");
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void RevertWindow_Commit_KeepsChanges() {
        var dir = tempDir();
        try {
            Guid updateId;
            using (var store = openStore(dir, "Native", "Memory")) {
                updateId = insertBaseNodes(store);
                store.BeginRevertWindow();
                mutate(store, updateId);
                store.CommitRevertWindow();
                Assert.IsNull(store.RevertWindow);
                verifyMutatedState(store);
            }
            using (var store = openStore(dir, "Native", "Memory")) {
                verifyMutatedState(store);
                Assert.AreEqual("changed", store.Get<RevArticle>(updateId).Category, "update kept after reopen");
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void DeleteTransactionsAfter_WithoutWindow_AcrossReopen() {
        var dir = tempDir();
        try {
            Guid updateId;
            long timestamp;
            using (var store = openStore(dir, "Native", "Memory")) {
                updateId = insertBaseNodes(store);
                timestamp = store.Timestamp; // remembered "before the changes", as the workflow prescribes
                mutate(store, updateId);
            } // dispose makes everything durable, including the engines at the new head
            using (var store = openStore(dir, "Native", "Memory")) {
                verifyMutatedState(store);
                var result = store.DeleteTransactionsAfter(timestamp);
                Assert.AreEqual(12, result.TransactionsDeleted); // 10 inserts + 1 update + 1 delete
                // the engine was durable past the timestamp, so the general form had to reset it
                Assert.AreEqual(1, result.EnginesReset.Length, "kv engine reset");
                verifyBaseState(store, updateId, "after DeleteTransactionsAfter");
            }
            using (var store = openStore(dir, "Native", "Memory")) {
                verifyBaseState(store, updateId, "after reopen");
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void DeleteTransactionsAfter_DryRun_ChangesNothing() {
        using var store = openMemoryStore();
        var updateId = insertBaseNodes(store);
        var timestamp = store.Timestamp;
        var transactions = mutate(store, updateId);

        var preview = store.DeleteTransactionsAfter(timestamp, dryRun: true);
        Assert.IsTrue(preview.DryRun);
        Assert.AreEqual(transactions, preview.TransactionsDeleted);
        Assert.IsTrue(preview.BytesTruncated > 0);
        Assert.AreEqual(timestamp, preview.LastTimestamp);
        verifyMutatedState(store); // nothing happened

        // and a dry run against the head reports nothing to delete
        var empty = store.DeleteTransactionsAfter(store.Timestamp, dryRun: true);
        Assert.AreEqual(0, empty.TransactionsDeleted);
    }

    [TestMethod]
    public void Guards() {
        using var store = openMemoryStore();
        insertBaseNodes(store);

        // rollback and commit need an active window
        Assert.ThrowsException<Exception>(() => store.RollbackRevertWindow());
        Assert.ThrowsException<Exception>(() => store.CommitRevertWindow());

        // reverting to before the first transaction (deleting everything) is refused
        Assert.ThrowsException<Exception>(() => store.DeleteTransactionsAfter(1));

        store.BeginRevertWindow();
        // no nested windows
        Assert.ThrowsException<Exception>(() => store.BeginRevertWindow());
        // the general form defers to the window while one is active
        store.Insert(new RevArticle { Id = Guid.NewGuid(), Body = "x", Category = "extra", Number = 1 });
        Assert.ThrowsException<Exception>(() => store.DeleteTransactionsAfter(store.Timestamp - 1));
        store.RollbackRevertWindow(); // discards the insert above
        Assert.IsNull(store.RevertWindow);
        Assert.AreEqual(NodeCount, store.Query<RevArticle>().Count());

        // a window with nothing written inside it can also be rolled back: the no-op still ends it
        store.BeginRevertWindow();
        var noop = store.RollbackRevertWindow();
        Assert.AreEqual(0, noop.TransactionsDeleted);
        Assert.IsNull(store.RevertWindow);
    }
}
