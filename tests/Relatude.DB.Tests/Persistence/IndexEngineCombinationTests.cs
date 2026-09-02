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
// value indexed and word indexed properties on one type, so a single query pass exercises both
// engine slots. InstantTextIndexing keeps the word indexes inside the transaction (they are
// otherwise filled by a background queue and would still be empty right after Insert).
[Node(TextIndex = BoolValue.True, InstantTextIndexing = BoolValue.True)]
public class CombiDoc {
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
/// Every value-engine / text-engine combination, since the engines are chosen independently:
/// value indexes and word indexes each pick their own backend, and one backend (SQLite) can serve
/// both roles from a single instance. Each combination must answer value filters and text searches,
/// and still answer them after a restart — which is where a mis-wired engine shows up, either by
/// losing its data or by claiming a position that skips the WAL replay that would rebuild it.
/// </summary>
[TestClass]
public class IndexEngineCombinationTests {

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
        dm.Add<CombiDoc>();
        var settings = TestEngines.Settings(value: v, text: t);
        return new NodeStore(DataStoreLocal.Open(dm, settings, new IOProviderDisk(dir), null, null, null, null,
            engineFactory(v, t, dir)));
    }

    static void insertNodes(NodeStore store) {
        for (var i = 0; i < NodeCount; i++) {
            store.Insert(new CombiDoc {
                Id = Guid.NewGuid(),
                Body = i % 2 == 0 ? "waterproof canvas backpack" : "lightweight leather satchel",
                Category = i % 4 == 0 ? "outdoor" : "travel",
                Number = i % 10,
            });
        }
    }

    static void verify(NodeStore store, string because) {
        Assert.AreEqual(NodeCount, store.Query<CombiDoc>().Count(), "node count " + because);
        // value indexes
        Assert.AreEqual(NodeCount / 4, store.Query<CombiDoc>().Where(x => x.Category == "outdoor").Count(), "value filter " + because);
        Assert.AreEqual(NodeCount / 10, store.Query<CombiDoc>().Where(x => x.Number == 3).Count(), "integer filter " + because);
        // word indexes: the node's combined text index, and one property's own word index
        Assert.AreEqual(NodeCount / 2, store.Query<CombiDoc>().WhereSearch("waterproof").Count(), "text search " + because);
        Assert.AreEqual(NodeCount / 2, store.Query<CombiDoc>().Where(x => x.Body.MatchesSearch("satchel")).Count(), "per property search " + because);
        Assert.AreEqual(0, store.Query<CombiDoc>().WhereSearch("nosuchword").Count(), "empty search " + because);
    }

    [DataTestMethod]
    [DataRow("Memory", "Memory")]
    [DataRow("Memory", "Lucene")]
    [DataRow("Memory", "Sqlite")]
    [DataRow("Native", "Memory")]
    [DataRow("Native", "Lucene")]
    [DataRow("Native", "Sqlite")]
    [DataRow("Sqlite", "Memory")]
    [DataRow("Sqlite", "Lucene")]
    [DataRow("Sqlite", "Sqlite")]
    public void EngineCombination_AnswersQueriesAndSurvivesRestart(string v, string t) {
        var dir = tempDir();
        var combination = "(values=" + v + ", text=" + t + ")";
        try {
            using (var store = openStore(dir, v, t)) {
                insertNodes(store);
                verify(store, "before restart " + combination);
            }
            using (var store = openStore(dir, v, t)) {
                verify(store, "after restart " + combination);
                // the reopened store must also accept and index new writes
                store.Insert(new CombiDoc { Id = Guid.NewGuid(), Body = "waterproof extra", Category = "outdoor", Number = 3 });
                Assert.AreEqual(NodeCount / 2 + 1, store.Query<CombiDoc>().WhereSearch("waterproof").Count(), "text search after write " + combination);
                Assert.AreEqual(NodeCount / 4 + 1, store.Query<CombiDoc>().Where(x => x.Category == "outdoor").Count(), "value filter after write " + combination);
            }
        } finally {
            try { Directory.Delete(dir, true); } catch { } // sqlite/lucene files can linger briefly on windows
        }
    }
}
