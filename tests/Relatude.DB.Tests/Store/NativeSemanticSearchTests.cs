using Relatude.DB.AI;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.IO;
using Relatude.DB.AI.ISV;
using Relatude.DB.Nodes;
using Relatude.Utils;

namespace Relatude.Store;

/// <summary>Semantic search through the full store, served by the disk-based native vector index
/// engine instead of the in-memory semantic index.</summary>
[TestClass]
public class NativeSemanticSearchTests {
    [TestMethod]
    public void SearchAndRestartWithNativeVectorEngine() {
        var folder = Path.Combine(Path.GetTempPath(), "RelatudeDB_NativeSemanticSearchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try {
            var settings = new SettingsLocal() {
                EnableSemanticIndexByDefault = true,
                // the dummy AI settings pick no index type, so this default routes to the engine:
                UsePersistedSemanticIndexesByDefault = true,
            };
            var io = new IOProviderDisk(Path.Combine(folder, "db"));
            var engineFolder = Path.Combine(folder, "indexes");
            Func<IndexEngines> engines = () => new IndexEngines(semantic: new NativeVectorIndexEngine(engineFolder));

            var storeData = DataStoreLocal.Open(Helper.GetDatamodel(), settings, io, null, null, null, AIEngine.CreateDummy(), engines);
            var store = new NodeStore(storeData);
            var articles = Helper.GenerateArticles(100);
            store.Insert(articles);
            while (store.Datastore.IsTaskQueueBusy()) Thread.Sleep(100); // embedding tasks

            // using the semantic extract to ensure 100% similarity with at least one of the articles:
            var nodeData = store.Mapper.CreateNodeDataFromObject(articles[0], null, null);
            var search = UtilsText.GetSemanticExtract((DataStoreLocal)store.Datastore, nodeData);
            Assert.AreEqual(1, store.Query<Article>().Search(search, 1, 1, false, 200, null).Execute().Count());
            Assert.AreEqual(articles.Count, store.Query<Article>().Search(search, 1, -1, false, 200, null).Execute().Count());
            var some = store.Query<Article>().Search(search, 1, 0, false, 200, null).Execute().Count();
            Assert.IsTrue(some >= 1 && some <= articles.Count);

            store.Dispose(); // the final WAL flush makes the vector index durable; no state save needed

            // the vectors now live in the engine's folder, not in the store's state files
            var engineFiles = Directory.GetFiles(Path.Combine(engineFolder, "vectorindex"), "*", SearchOption.AllDirectories);
            Assert.IsTrue(engineFiles.Length > 0, "the native vector engine should have persisted index files");

            // reopen: the index loads from its own manifest and searches work again
            var storeData2 = DataStoreLocal.Open(Helper.GetDatamodel(), settings, io, null, null, null, AIEngine.CreateDummy(), engines);
            var store2 = new NodeStore(storeData2);
            Assert.AreEqual(1, store2.Query<Article>().Search(search, 1, 1, false, 200, null).Execute().Count());
            Assert.AreEqual(articles.Count, store2.Query<Article>().Search(search, 1, -1, false, 200, null).Execute().Count());
            store2.Dispose();
        } finally {
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    [TestMethod]
    public void PersistedOffFallsBackToMemoryIndex() {
        var folder = Path.Combine(Path.GetTempPath(), "RelatudeDB_NativeSemanticSearchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try {
            var settings = new SettingsLocal() {
                EnableSemanticIndexByDefault = true,
                UsePersistedSemanticIndexesByDefault = false, // memory index, even with an engine configured
            };
            var engineFolder = Path.Combine(folder, "indexes");
            Func<IndexEngines> engines = () => new IndexEngines(semantic: new NativeVectorIndexEngine(engineFolder));
            var storeData = DataStoreLocal.Open(Helper.GetDatamodel(), settings, new IOProviderDisk(Path.Combine(folder, "db")), null, null, null, AIEngine.CreateDummy(), engines);
            var store = new NodeStore(storeData);
            var articles = Helper.GenerateArticles(10);
            store.Insert(articles);
            while (store.Datastore.IsTaskQueueBusy()) Thread.Sleep(100);
            var nodeData = store.Mapper.CreateNodeDataFromObject(articles[0], null, null);
            var search = UtilsText.GetSemanticExtract((DataStoreLocal)store.Datastore, nodeData);
            Assert.AreEqual(1, store.Query<Article>().Search(search, 1, 1, false, 200, null).Execute().Count());
            store.Dispose();
            // the engine was never asked to open an index: no per-index folder, no data files
            var vectorFolder = Path.Combine(engineFolder, "vectorindex");
            if (Directory.Exists(vectorFolder)) {
                Assert.AreEqual(0, Directory.GetFileSystemEntries(vectorFolder).Length, "the disk vector engine should be unused when UsePersistedSemanticIndexesByDefault is off");
            }
        } finally {
            try { Directory.Delete(folder, true); } catch { }
        }
    }
}
