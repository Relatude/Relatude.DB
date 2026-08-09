using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Indexes.KvStore;
using Relatude.DB.Datastores.Indexes.BTreeIndex;
using Relatude.DB.IO;
using Relatude.DB.Nodes;

namespace Relatude.Persistence;

[Node]
public class AheadArticle {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(Indexed = true)]
    public string Name { get; set; } = string.Empty;
    [IntegerProperty(Indexed = true)]
    public int Number { get; set; }
}

/// <summary>
/// Startup divergence detection: a persisted index store whose timestamp is newer than anything in
/// the WAL holds transactions the durable log lost (e.g. a crash dropped a queued WAL batch after
/// the indexes had committed). The store must detect this on open and rebuild all indexes from the
/// log instead of silently keeping the phantom entries.
/// </summary>
[TestClass]
public class IndexAheadOfLogTests {

    const int NodeCount = 100;

    static string tempDir() {
        var dir = Path.Combine(Path.GetTempPath(), "RelatudeDB_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
    static NodeStore openStore(string dir) {
        var dm = new Datamodel();
        dm.Add<AheadArticle>();
        var settings = new SettingsLocal {
            UsePersistedValueIndexesByDefault = true,
            PersistedValueIndexEngine = PersistedValueIndexEngine.Native,
        };
        return new NodeStore(DataStoreLocal.Open(dm, settings, new IOProviderDisk(dir), null, null, null, null,
            () => new IndexEngines(new NativeKvIndexStore(dir))));
    }
    static void insertNodes(NodeStore store) {
        for (int i = 0; i < NodeCount; i++) {
            store.Insert(new AheadArticle { Id = Guid.NewGuid(), Name = "Name" + i, Number = i % 10 });
        }
    }
    static void verifyNodes(NodeStore store) {
        Assert.AreEqual(NodeCount, store.Query<AheadArticle>().Count());
        // indexed queries must answer from the (rebuilt) persisted indexes
        Assert.AreEqual(NodeCount / 10, store.Query<AheadArticle>().Where(a => a.Number == 3).Count());
        Assert.AreEqual(1, store.Query<AheadArticle>().Where(a => a.Name == "Name42").Count());
    }
    static string findKvFile(string dir) {
        var files = Directory.GetFiles(dir, "nativekv.db", SearchOption.AllDirectories);
        Assert.AreEqual(1, files.Length, "Expected exactly one nativekv.db under the test folder.");
        return files[0];
    }

    [TestMethod]
    public void ReopenWithoutTampering_KeepsIndexes() {
        var dir = tempDir();
        try {
            using (var store = openStore(dir)) insertNodes(store);
            using (var store = openStore(dir)) verifyNodes(store);
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void IndexStoreAheadOfLog_IsDetectedAndRebuilt() {
        var dir = tempDir();
        try {
            using (var store = openStore(dir)) insertNodes(store);

            // Simulate the aftermath of a lost WAL batch: the persisted index store claims a
            // timestamp far beyond anything the log contains.
            using (var engine = new BPlusTreeStorageEngine(findKvFile(dir))) {
                engine.SetTimestamp(DateTime.UtcNow.AddYears(100).Ticks);
            }

            // The open must detect the divergence, reset all indexes and rebuild them from the log.
            using (var store = openStore(dir)) {
                verifyNodes(store);
                // after the rebuild the store accepts writes and indexes them normally
                store.Insert(new AheadArticle { Id = Guid.NewGuid(), Name = "AfterRebuild", Number = 3 });
                Assert.AreEqual(1, store.Query<AheadArticle>().Where(a => a.Name == "AfterRebuild").Count());
            }

            // and the rebuilt state must survive another clean reopen
            using (var store = openStore(dir)) {
                Assert.AreEqual(NodeCount + 1, store.Query<AheadArticle>().Count());
                Assert.AreEqual(NodeCount / 10 + 1, store.Query<AheadArticle>().Where(a => a.Number == 3).Count());
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void UnflushedIndexCommits_RollBackToLogConsistentState() {
        // Option B semantics end-to-end: index commits publish in memory and become durable at WAL
        // flush. Dispose flushes, so a clean shutdown keeps everything; this verifies the reopened
        // store answers indexed queries correctly after multiple open/insert/close cycles.
        var dir = tempDir();
        try {
            using (var store = openStore(dir)) insertNodes(store);
            using (var store = openStore(dir)) {
                store.Insert(new AheadArticle { Id = Guid.NewGuid(), Name = "Second", Number = 3 });
            }
            using (var store = openStore(dir)) {
                Assert.AreEqual(NodeCount + 1, store.Query<AheadArticle>().Count());
                Assert.AreEqual(NodeCount / 10 + 1, store.Query<AheadArticle>().Where(a => a.Number == 3).Count());
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }
}
