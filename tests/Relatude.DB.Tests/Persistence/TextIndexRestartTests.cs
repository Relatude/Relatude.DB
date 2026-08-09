using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.Query;

namespace Relatude.Persistence;

#region datamodel
// InstantTextIndexing so the word indexes are written as part of the transaction (the combined
// text index is otherwise filled by a background queue and could still be empty right after Insert)
[Node(TextIndex = BoolValue.True, InstantTextIndexing = BoolValue.True)]
public class DiskTextArticle {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(IndexedByWords = true)]
    public string Title { get; set; } = "";
    [StringProperty(IndexedByWords = true)]
    public string Body { get; set; } = "";
}
#endregion

/// <summary>
/// The built-in disk text index engine (Relatude.DB.TextIndex) running under a full data store:
/// text search must work through the normal query API, survive restarts, rebuild itself from the
/// WAL when its files are deleted, and behave like the in-memory trie index on the same data —
/// including the BM25 ranking order.
/// </summary>
[TestClass]
public class TextIndexRestartTests {

    const int NodeCount = 50;

    static string tempDir() {
        var dir = Path.Combine(Path.GetTempPath(), "RelatudeDB_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
    static NodeStore openStore(string dir, bool persistedText = true) {
        var dm = new Datamodel();
        dm.Add<DiskTextArticle>();
        var settings = new SettingsLocal {
            UsePersistedTextIndexesByDefault = persistedText,
            PersistedTextIndexEngine = persistedText ? PersistedTextIndexEngine.Native : PersistedTextIndexEngine.Memory,
        };
        return new NodeStore(DataStoreLocal.Open(dm, settings, new IOProviderDisk(dir), null, null, null, null,
            persistedText ? () => new IndexEngines(null, new TextIndexEngine(dir)) : null));
    }
    static void insertNodes(NodeStore store) {
        for (int i = 0; i < NodeCount; i++) {
            store.Insert(new DiskTextArticle {
                Id = Guid.NewGuid(),
                Title = "Fjellrev backpack " + i,
                Body = i % 2 == 0 ? "waterproof and sturdy" : "lightweight canvas",
            });
        }
    }
    static void verifySearch(NodeStore store) {
        Assert.AreEqual(NodeCount, store.Query<DiskTextArticle>().WhereSearch("fjellrev").Count(), "combined text index (WhereSearch)");
        Assert.AreEqual(NodeCount / 2, store.Query<DiskTextArticle>().WhereSearch("waterproof").Count(), "combined text index (WhereSearch)");
        Assert.AreEqual(NodeCount, store.Query<DiskTextArticle>().Where(x => x.Title.MatchesSearch("backpack")).Count(), "per property word index (MatchesSearch)");
        Assert.AreEqual(0, store.Query<DiskTextArticle>().WhereSearch("nosuchword").Count());
    }
    static string textIndexFolder(string dir) {
        var path = Path.Combine(dir, "textindex");
        Assert.IsTrue(Directory.Exists(path), "Expected the textindex folder under the test folder.");
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
            // dispose flushed the WAL and the segment manifests with their position, so the
            // reopened store answers text searches without any rebuild
            using (var store = openStore(dir)) verifySearch(store);
            // and again, to prove the reopened state re-persists correctly too
            using (var store = openStore(dir)) verifySearch(store);
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void MissingIndexFolder_IsRebuiltFromLog() {
        var dir = tempDir();
        try {
            using (var store = openStore(dir)) insertNodes(store);
            // Simulate lost index files: the word indexes are gone while the log is current. A
            // fresh index reports timestamp 0, which must make the startup loader replay the WAL.
            Directory.Delete(textIndexFolder(dir), true);
            using (var store = openStore(dir)) {
                verifySearch(store);
                store.Insert(new DiskTextArticle { Id = Guid.NewGuid(), Title = "Fjellrev backpack extra", Body = "waterproof" });
                Assert.AreEqual(NodeCount + 1, store.Query<DiskTextArticle>().WhereSearch("fjellrev").Count());
            }
            using (var store = openStore(dir)) {
                Assert.AreEqual(NodeCount + 1, store.Query<DiskTextArticle>().WhereSearch("fjellrev").Count());
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void UpdatedNodes_AreReflectedInSearchAfterReopen() {
        var dir = tempDir();
        try {
            using (var store = openStore(dir)) {
                insertNodes(store);
                var article = store.Query<DiskTextArticle>().WhereSearch("waterproof").Execute().First();
                article.Body = "repainted in glorious teal";
                store.Update(article);
                // the updated words must be searchable immediately (write buffer overlay)...
                Assert.AreEqual(1, store.Query<DiskTextArticle>().WhereSearch("teal").Count());
                Assert.AreEqual(NodeCount / 2 - 1, store.Query<DiskTextArticle>().WhereSearch("waterproof").Count());
            }
            // ...and after a restart
            using (var store = openStore(dir)) {
                Assert.AreEqual(1, store.Query<DiskTextArticle>().WhereSearch("teal").Count());
                Assert.AreEqual(NodeCount / 2 - 1, store.Query<DiskTextArticle>().WhereSearch("waterproof").Count());
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void ParityWithMemoryTrie_SetsAndRanking() {
        var dirDisk = tempDir();
        var dirMem = tempDir();
        try {
            using var diskStore = openStore(dirDisk, persistedText: true);
            using var memStore = openStore(dirMem, persistedText: false);
            // identical corpus in both stores; strictly increasing doc lengths give every doc a
            // distinct BM25 score, so the ranked order is fully determined and must match exactly
            var ids = new Guid[12];
            for (var i = 0; i < ids.Length; i++) ids[i] = Guid.NewGuid();
            foreach (var store in new[] { diskStore, memStore }) {
                for (var i = 0; i < ids.Length; i++) {
                    var padding = string.Join(' ', Enumerable.Range(0, i * 2).Select(p => "filler" + p));
                    store.Insert(new DiskTextArticle {
                        Id = ids[i],
                        Title = "gadget " + i,
                        Body = ("waterproof kettle " + padding).Trim(),
                    });
                }
            }
            string[] queries = ["waterproof", "waterproof kettle", "water*", "kettle waterproof gadget", "watrproof~", "filler3"];
            foreach (var q in queries) {
                var disk = diskStore.Query<DiskTextArticle>().WhereSearch(q).Execute().Select(a => a.Id).ToArray();
                var mem = memStore.Query<DiskTextArticle>().WhereSearch(q).Execute().Select(a => a.Id).ToArray();
                CollectionAssert.AreEquivalent(mem, disk, "result set differs from the memory trie for: " + q);
            }
            // ranking parity: same BM25 formula and statistics → same order
            var diskRanked = diskStore.Query<DiskTextArticle>().WhereSearch("waterproof").Execute().Select(a => a.Id).ToArray();
            var memRanked = memStore.Query<DiskTextArticle>().WhereSearch("waterproof").Execute().Select(a => a.Id).ToArray();
            CollectionAssert.AreEqual(memRanked, diskRanked, "BM25 ranking order differs from the memory trie");
            Assert.AreEqual(ids[0], diskRanked[0], "the shortest doc scores highest for equal term counts");
        } finally {
            Directory.Delete(dirDisk, true);
            Directory.Delete(dirMem, true);
        }
    }
}
