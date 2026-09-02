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
            var settings = TestEngines.Settings(vector: IndexEngineTypes.IVS);
            settings.EnableSemanticIndexByDefault = true;
            var io = new IOProviderDisk(Path.Combine(folder, "db"));
            var engineFolder = Path.Combine(folder, "indexes");
            Func<IndexEngines> engines = () => IndexEngines.Single(vectorId: TestEngines.VectorId, vector: new ISVEngine(engineFolder));

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
    public void NoEngineFallsBackToMemoryIndex() {
        var folder = Path.Combine(Path.GetTempPath(), "RelatudeDB_NativeSemanticSearchTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try {
            // no vector engine configured: DefaultVectorIndex stays Guid.Empty, the memory index
            var settings = new SettingsLocal { EnableSemanticIndexByDefault = true };
            var storeData = DataStoreLocal.Open(Helper.GetDatamodel(), settings, new IOProviderDisk(Path.Combine(folder, "db")), null, null, null, AIEngine.CreateDummy(), null);
            var store = new NodeStore(storeData);
            var articles = Helper.GenerateArticles(10);
            store.Insert(articles);
            while (store.Datastore.IsTaskQueueBusy()) Thread.Sleep(100);
            var nodeData = store.Mapper.CreateNodeDataFromObject(articles[0], null, null);
            var search = UtilsText.GetSemanticExtract((DataStoreLocal)store.Datastore, nodeData);
            Assert.AreEqual(1, store.Query<Article>().Search(search, 1, 1, false, 200, null).Execute().Count());
            store.Dispose();
        } finally {
            try { Directory.Delete(folder, true); } catch { }
        }
    }
}
