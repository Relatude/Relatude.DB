using Relatude.DB.AI;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.Nodes;
using Relatude.Utils;

namespace Relatude.Store;

[TestClass]
public class SemanticSearchTests {
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

    [TestMethod]
    public void DefaultsFromStoreSettingsTest() {
        // a search leaving the settings open must pick them up from the store settings:
        Assert.AreEqual(1, countHitsUsingDefaults(1, 1, out var total)); // only the 100% match
        Assert.AreEqual(total, countHitsUsingDefaults(1, -1, out _)); // no similarity limit, all match

        // ... and the provider level setting still wins over the store setting when it is given:
        Assert.AreEqual(1, countHitsUsingDefaults(1, -1, out _, providerSimilarityLimit: 1));
    }

    /// <summary>
    /// Searches with the semantic weight and similarity limit left to the defaults, and returns the
    /// number of hits. The search text is a full semantic extract of one of the articles, so at a
    /// similarity limit of 1 exactly one article can match.
    /// </summary>
    static int countHitsUsingDefaults(double weight, double similarityLimit, out int totalArticles, double? providerSimilarityLimit = null) {
        var settings = new SettingsLocal() {
            EnableSemanticIndexByDefault = true,
            DefaultSemanticIndexWeight = weight,
            DefaultSemanticSimilarityLimit = similarityLimit,
        };
        var ai = AIEngine.CreateDummy();
        ai.Settings.DefaultMinimumSimilarity = providerSimilarityLimit;
        using var store = new NodeStore(DataStoreLocal.Open(Helper.GetDatamodel(), settings, null, null, null, null, ai));
        var articles = Helper.GenerateArticles(100);
        totalArticles = articles.Count;
        store.Insert(articles);
        while (store.Datastore.IsTaskQueueBusy()) Thread.Sleep(100);
        var nodeData = store.Mapper.CreateNodeDataFromObject(articles[0], null, null);
        var search = UtilsText.GetSemanticExtract((DataStoreLocal)store.Datastore, nodeData);
        return store.Query<Article>().Search(search, null, null, false, 200, null).Execute().Count();
    }
}