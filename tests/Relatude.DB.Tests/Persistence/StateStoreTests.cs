using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.NodeServer;
using Relatude.DB.Query;
using Relatude.Store;
using Relatude.Utils;
using NodeStore = Relatude.DB.Nodes.NodeStore;

namespace Relatude.Persistence;

/// <summary>
/// The state stores behind the guid map, node segments, addresses and relations. The memory store
/// and the native KV store must behave the same, survive a reopen (the KV store from its own file,
/// with the log replayed from its own position) and rebuild from the log when the engine is lost or
/// the kind of store changes.
/// </summary>
[TestClass]
public class StateStoreTests {

    static string newDir() {
        var dir = Path.Combine(Path.GetTempPath(), "RelatudeDB_State_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
    static Datamodel datamodel() {
        var dm = Helper.GetDatamodel();
        dm.Add<UrlPage>(); // carries an address property
        dm.Add<UrlPageTree>();
        return dm;
    }
    static NodeStore open(string dir, StateStoreEngine engine, string? valueEngine = null) {
        var settings = TestEngines.Settings(value: valueEngine);
        settings.StateStore = engine;
        settings.StateStoreMaxMemoryUsageInMb = 16;
        var indexPath = Path.Combine(dir, FileKeyUtility.IndexStoreFolderKey);
        var createStateStore = NodeStoreContainer.CreateStateStoreFactory(settings, indexPath, []);
        var data = DataStoreLocal.Open(datamodel(), settings, new IOProviderDisk(dir), createIndexEngines: TestEngines.Factory(dir, settings),
            throwOnBadStateFile: false, throwOnBadLogFile: true, createStateStore: createStateStore);
        return new NodeStore(data);
    }
    static string stateFolder(string dir) => NodeStoreContainer.StateStoreFolderPath(Path.Combine(dir, FileKeyUtility.IndexStoreFolderKey));

    // 300 articles (ids 1..300), 2..40 children of 1 with 10 moved first, 200..250 deleted, 5 renamed,
    // one author, 50 pages with addresses of which 5 are deleted
    static void seed(NodeStore store) {
        var articles = Helper.GenerateArticles(300);
        foreach (var chunk in articles.Chunk(50)) store.Insert(chunk);
        for (var id = 2; id <= 40; id++) store.AddRelation<Article>(id, a => a.Parent!, 1);
        store.MoveRelationToTop(store.Get<Article>(1), a => a.Children, store.Get<Article>(10));
        store.Delete(Enumerable.Range(200, 51));
        var renamed = store.Get<Article>(5);
        renamed.Name = "renamed";
        store.Update(renamed);
        store.Insert(new User { Username = "ann" }, out var authorId);
        store.AddRelation<Article>(store.Datastore.GetGuid(7), a => a.Author!, authorId);
        for (var i = 1; i <= 50; i++) store.Insert(new UrlPage { Title = "Page " + i, Slug = "page-" + i }, out _);
        for (var i = 1; i <= 5; i++) {
            Assert.IsTrue(store.Datastore.TryGetNodeIdFromAddress("page-" + i, out Guid pageId));
            store.Delete(pageId);
        }
    }
    static void verify(NodeStore store, string when, bool withLateArticle = false, bool expectVersions = true, int extraArticles = 0) {
        Assert.AreEqual(249 + extraArticles + (withLateArticle ? 1 : 0), store.Query<Article>().Count(), when + ": articles");
        Assert.AreEqual("renamed", store.Get<Article>(5).Name, when + ": update");
        Assert.IsFalse(store.Datastore.TryGetAddress(200, out _), when + ": deleted node gone");
        var root = store.Query<Article>().Where(a => a.Id == 1).Include(a => a.Children).Execute().First();
        var children = root.Children.Select(c => c.Id).ToArray();
        int[] expectedChildren = withLateArticle ? [.. Enumerable.Range(2, 39), 400] : [.. Enumerable.Range(2, 39)];
        Assert.AreEqual(expectedChildren.Length, children.Length, when + ": children");
        Assert.AreEqual(10, children[0], when + ": reordered child first");
        if (withLateArticle) Assert.AreEqual(400, children[^1], when + ": late child last");
        CollectionAssert.AreEquivalent(expectedChildren, children, when + ": children set");
        var withAuthor = store.Query<Article>().Where(a => a.Id == 7).Include(a => a.Author).Execute().First();
        Assert.AreEqual("ann", withAuthor.Author?.Username, when + ": author");
        Assert.AreEqual(45, store.Query<UrlPage>().Count(), when + ": pages");
        Assert.IsTrue(store.Datastore.TryGetNodeIdFromAddress("page-7", out Guid pageId), when + ": address lookup");
        Assert.AreEqual("page-7", store.Get<UrlPage>(pageId).Slug);
        Assert.IsFalse(store.Datastore.TryGetNodeIdFromAddress("page-3", out Guid _), when + ": deleted address gone");
        if (expectVersions) Assert.AreEqual(1, store.FindOlderVersions(store.Datastore.GetGuid(5)).Length, when + ": version chain");
    }

    [TestMethod]
    [DataRow(StateStoreEngine.Memory, null)]
    [DataRow(StateStoreEngine.Native, null)]
    [DataRow(StateStoreEngine.Native, "Native")]
    public void NodesRelationsAndAddresses_RoundTrip_AndSurviveReopen(StateStoreEngine engine, string? valueEngine) {
        var dir = newDir();
        try {
            using (var store = open(dir, engine, valueEngine)) {
                seed(store);
                verify(store, "before reopen");
            }
            using (var store = open(dir, engine, valueEngine)) {
                verify(store, "after reopen");
                // a state snapshot older than the engine: the file gated stores replay the tail, the engine skips it
                store.Maintenance(MaintenanceAction.SaveIndexStates);
                store.Insert(new Article { Id = 400, Name = "late" });
                store.AddRelation<Article>(400, a => a.Parent!, 1);
            }
            using (var store = open(dir, engine, valueEngine)) verify(store, "after second reopen", withLateArticle: true);
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void NativeStateLost_RebuildsFromTheLog() {
        var dir = newDir();
        try {
            using (var store = open(dir, StateStoreEngine.Native)) seed(store);
            Directory.Delete(stateFolder(dir), true);
            using (var store = open(dir, StateStoreEngine.Native)) verify(store, "after losing the engine");
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void SwitchingTheKindOfStore_RebuildsFromTheLog() {
        var dir = newDir();
        try {
            using (var store = open(dir, StateStoreEngine.Memory)) {
                seed(store);
                store.Maintenance(MaintenanceAction.SaveIndexStates); // a memory state file the native store must reject
            }
            using (var store = open(dir, StateStoreEngine.Native)) {
                verify(store, "memory to native");
                store.Maintenance(MaintenanceAction.SaveIndexStates); // and the other way around
            }
            using (var store = open(dir, StateStoreEngine.Memory)) verify(store, "native to memory");
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    [DataRow(StateStoreEngine.Memory)]
    [DataRow(StateStoreEngine.Native)]
    public void RewriteWithHotSwap_KeepsEveryNodeReadable(StateStoreEngine engine) {
        var dir = newDir();
        try {
            using (var store = open(dir, engine)) {
                seed(store);
                var data = (DataStoreLocal)store.Datastore;
                data.RewriteStore(true, FileKeyUtility.WAL_NextFileKey(data.IO));
                store.Maintenance(MaintenanceAction.ClearCache); // every read below comes from the new file
                verify(store, "after rewrite", expectVersions: false); // a rewrite keeps one version per node
                store.Insert(new Article { Id = 401, Name = "after rewrite" });
                var updated = store.Get<Article>(401);
                updated.Name = "after rewrite, updated";
                store.Update(updated); // chains to the version written to the new file
                Assert.AreEqual(1, store.FindOlderVersions(store.Datastore.GetGuid(401)).Length);
            }
            using (var store = open(dir, engine)) {
                verify(store, "after rewrite and reopen", expectVersions: false, extraArticles: 1);
                Assert.AreEqual("after rewrite, updated", store.Get<Article>(401).Name);
                Assert.AreEqual(1, store.FindOlderVersions(store.Datastore.GetGuid(401)).Length);
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    [DataRow(StateStoreEngine.Memory)]
    [DataRow(StateStoreEngine.Native)]
    public void DeleteTransactionsAfter_RevertsTheStateStore(StateStoreEngine engine) {
        var dir = newDir();
        try {
            long timestamp;
            using (var store = open(dir, engine)) {
                seed(store);
                timestamp = store.Timestamp;
                store.Insert(new Article { Id = 500, Name = "to be reverted" });
                store.AddRelation<Article>(500, a => a.Parent!, 1);
                store.Delete(6);
            }
            using (var store = open(dir, engine)) {
                Assert.AreEqual(249, store.Query<Article>().Count());
                var result = store.DeleteTransactionsAfter(timestamp);
                Assert.AreEqual(3, result.TransactionsDeleted);
                if (engine == StateStoreEngine.Native) Assert.IsTrue(result.EnginesReset.Length >= 1, "the state engine was durable past the revert point");
                verify(store, "after revert");
            }
            using (var store = open(dir, engine)) verify(store, "after revert and reopen");
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    [DataRow(StateStoreEngine.Memory)]
    [DataRow(StateStoreEngine.Native)]
    public void VersionChains_FollowUpdatesWrittenBeforeAndAfterOpen(StateStoreEngine engine) {
        var dir = newDir();
        try {
            Guid id;
            using (var store = open(dir, engine)) {
                var article = new Article { Id = 1, Name = "v0" };
                store.Insert(article);
                id = store.Datastore.GetGuid(1);
                for (var v = 1; v <= 3; v++) {
                    article.Name = "v" + v;
                    store.Update(article);
                }
                Assert.AreEqual(3, store.FindOlderVersions(id).Length);
            }
            using (var store = open(dir, engine)) {
                Assert.AreEqual(3, store.FindOlderVersions(id).Length, "chains read from the file after reopen");
                var article = store.Get<Article>(1);
                article.Name = "v4"; // the removed version was written before this session: its segment comes from the action
                store.Update(article);
                Assert.AreEqual(4, store.FindOlderVersions(id).Length);
                store.Delete(1);
                Assert.AreEqual(0, store.FindOlderVersions(id).Length, "a deleted node has no reachable chain");
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }
}
