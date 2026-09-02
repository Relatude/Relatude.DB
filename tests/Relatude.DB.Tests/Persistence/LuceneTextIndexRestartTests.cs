using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Indexes.KvStore;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.Query;
using Relatude.Utils;

namespace Relatude.Persistence;

#region datamodel
// InstantTextIndexing so the word indexes are written as part of the transaction (the combined
// text index is otherwise filled by a background queue and could still be empty right after Insert)
[Node(TextIndex = BoolValue.True, InstantTextIndexing = BoolValue.True)]
public class LuceneArticle {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(IndexedByWords = true)]
    public string Title { get; set; } = "";
    [StringProperty(IndexedByWords = true)]
    public string Body { get; set; } = "";
}
#endregion

/// <summary>
/// Restart behavior of the Lucene text index engine. The engine defers Lucene commits to the
/// WAL-flush checkpoint and stores each index's position (timestamp + WAL file id) in the Lucene
/// commit user data, so after any restart the index either has its data or reports a position that
/// makes the startup loader replay the missing WAL — the text index can never come back silently
/// empty while claiming to be current (the original Website.Simple bug: seed 15k products, kill the
/// site, reopen, text search returns nothing while facets still work).
/// </summary>
[TestClass]
public class LuceneTextIndexRestartTests {

    const int NodeCount = 50;

    static string tempDir() {
        var dir = Path.Combine(Path.GetTempPath(), "RelatudeDB_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
    static NodeStore openStore(string dir) {
        var dm = new Datamodel();
        dm.Add<LuceneArticle>();
        var settings = new SettingsLocal {
            ValueIndexes = [TestEngines.NativeValue], DefaultValueIndex = TestEngines.ValueId,
            TextIndexes = [TestEngines.LuceneText], DefaultTextIndex = TestEngines.TextId,
        };
        return new NodeStore(DataStoreLocal.Open(dm, settings, new IOProviderDisk(dir), null, null, null, null,
            () => IndexEngines.Single(TestEngines.ValueId, new NativeKvIndexStore(dir), TestEngines.TextId, new LuceneTextIndexEngine(dir))));
    }
    static void insertNodes(NodeStore store) {
        for (int i = 0; i < NodeCount; i++) {
            store.Insert(new LuceneArticle {
                Id = Guid.NewGuid(),
                Title = "Fjellrev backpack " + i,
                Body = i % 2 == 0 ? "waterproof and sturdy" : "lightweight canvas",
            });
        }
    }
    static void verifySearch(NodeStore store) {
        Assert.AreEqual(NodeCount, store.Query<LuceneArticle>().WhereSearch("fjellrev").Count(), "combined text index (WhereSearch)");
        Assert.AreEqual(NodeCount / 2, store.Query<LuceneArticle>().WhereSearch("waterproof").Count(), "combined text index (WhereSearch)");
        Assert.AreEqual(NodeCount, store.Query<LuceneArticle>().Where(x => x.Title.MatchesSearch("backpack")).Count(), "per property word index (MatchesSearch)");
        Assert.AreEqual(0, store.Query<LuceneArticle>().WhereSearch("nosuchword").Count());
    }
    static string luceneFolder(string dir) {
        var path = Path.Combine(dir, "lucene");
        Assert.IsTrue(Directory.Exists(path), "Expected the lucene index folder under the test folder.");
        return path;
    }

    [TestMethod]
    public void CleanReopen_KeepsTextSearch() {
        var dir = tempDir();
        try {
            using (var store = openStore(dir)) {
                insertNodes(store);
                verifySearch(store);
            }
            // dispose flushed the WAL and committed the Lucene indexes with their position, so the
            // reopened store answers text searches without any rebuild
            using (var store = openStore(dir)) verifySearch(store);
            // and again, to prove the reopened state re-persists correctly too
            using (var store = openStore(dir)) verifySearch(store);
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void MissingLuceneIndex_IsRebuiltFromLog() {
        var dir = tempDir();
        try {
            using (var store = openStore(dir)) insertNodes(store);
            // Simulate the aftermath of lost Lucene commits (or a deleted index folder): the word
            // indexes are gone while the value indexes are current. A fresh index reports
            // timestamp 0, which must make the startup loader replay the whole WAL into it.
            Directory.Delete(luceneFolder(dir), true);
            using (var store = openStore(dir)) {
                verifySearch(store);
                // after the rebuild the store accepts writes and indexes them normally
                store.Insert(new LuceneArticle { Id = Guid.NewGuid(), Title = "Fjellrev backpack extra", Body = "waterproof" });
                Assert.AreEqual(NodeCount + 1, store.Query<LuceneArticle>().WhereSearch("fjellrev").Count());
            }
            // and the rebuilt state must survive another clean reopen
            using (var store = openStore(dir)) {
                Assert.AreEqual(NodeCount + 1, store.Query<LuceneArticle>().WhereSearch("fjellrev").Count());
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void UpdatedNodes_AreReflectedInSearchAfterReopen() {
        var dir = tempDir();
        try {
            Guid id;
            using (var store = openStore(dir)) {
                insertNodes(store);
                var article = store.Query<LuceneArticle>().WhereSearch("waterproof").Execute().First();
                id = article.Id;
                article.Body = "repainted in glorious teal";
                store.Update(article);
                // the updated words must be searchable immediately (near-real-time reader)...
                Assert.AreEqual(1, store.Query<LuceneArticle>().WhereSearch("teal").Count());
                Assert.AreEqual(NodeCount / 2 - 1, store.Query<LuceneArticle>().WhereSearch("waterproof").Count());
            }
            // ...and after a restart
            using (var store = openStore(dir)) {
                Assert.AreEqual(1, store.Query<LuceneArticle>().WhereSearch("teal").Count());
                Assert.AreEqual(NodeCount / 2 - 1, store.Query<LuceneArticle>().WhereSearch("waterproof").Count());
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }
}
