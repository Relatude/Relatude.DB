using Relatude.DB.AI;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.Nodes;
using Relatude.DB.Utils;

namespace Tests;

[TestClass]
public class SemanticSearch {
    [TestMethod]
    public void SearchTest() {
        var datamodel = Helper.GetDatamodel();
        var settings = new SettingsLocal() {
            EnableSemanticIndexByDefault = true,
        };
        var ai = AIEngine.CreateDummy();
        var storeData = DataStoreLocal.Open(datamodel, settings, null, null, null, null, ai);
        var store = new NodeStore(storeData);
        var articles = Helper.GenerateArticles(100);
        store.Insert(articles);

        while (store.Datastore.IsTaskQueueBusy()) {
            Thread.Sleep(100);
        }

        // using the semantic extract to ensure 100% similarity with at least one of the articles:
        var oneOfTheArticles = articles[0];
        var nodeData = store.Mapper.CreateNodeDataFromObject(oneOfTheArticles, null, null);
        var search = UtilsText.GetSemanticExtract((DataStoreLocal)store.Datastore, nodeData);
        // 1 cosine similarity - should be 1 100% match:
        var result = store.Query<Article>().Search(search, 1, 1, false, 200, null).Execute().ToList();
        Assert.AreEqual(1, result.Count);

        // -1 cosine similarity - should match all
        result = store.Query<Article>().Search(search, 1, -1, false, 200, null).Execute().ToList();
        Assert.AreEqual(result.Count, articles.Count);

        // 0 cosine similarity - should match some
        result = store.Query<Article>().Search(search, 1, 0, false, 200, null).Execute().ToList();
        Assert.IsTrue(result.Count >= 1 && result.Count <= articles.Count);

        store.Dispose();
    }
}