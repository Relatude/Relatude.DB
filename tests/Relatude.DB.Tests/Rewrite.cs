using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Query;
using Relatude.DB.Nodes;
using Relatude.Utils;

namespace Relatude;
[TestClass]
public class Rewrite {
    void testData(NodeStore store, List<Article> orgArticles) {
        for (int i = 1; i < orgArticles.Count + 1; i++) {
            var artFromStore = store.Get<Article>(i);
            Assert.AreEqual(orgArticles[i - 1].Name, artFromStore.Name);
            Assert.AreEqual(orgArticles[i - 1].Body, artFromStore.Body);
        }
    }
    [TestMethod]
    public void StateFileRoundTrip() {
        // Forces a state.bin write (nodes, guids, relations, ...) then reopens, so the state file
        // is read back through BufferReader (the in-place parsing path).
        var io = new IOProviderMemory();
        var datamodel = Helper.GetDatamodel();
        var storeData = DataStoreLocal.Open(datamodel, null, io);
        var store = new NodeStore(storeData);

        var articles = Helper.GenerateArticles(200);
        var t = new Transaction(store);
        t.Insert(articles);
        store.Execute(t);
        store.AddRelation(articles[1], a => a.Parent, articles[0]);
        store.AddRelation(articles[2], a => a.Parent, articles[0]);

        store.Maintenance(MaintenanceAction.SaveIndexStates); // writes state.bin
        store.Dispose();

        storeData = DataStoreLocal.Open(datamodel, null, io); // reads state.bin via BufferReader
        store = new NodeStore(storeData);
        testData(store, articles);
        var parentNode = store.Query<Article>().Where(a => a.Id == articles[0].Id).Execute().First();
        var children = store.Query<Article>().WhereRelates(a => a.Parent, parentNode.PId).Execute().ToList();
        Assert.AreEqual(2, children.Count);
        store.Dispose();
    }

    [TestMethod]
    public void SimpleRewrite() {
        var io = new IOProviderMemory();
        var datamodel = Helper.GetDatamodel();
        var storeData = DataStoreLocal.Open(datamodel, null, io);
        var store = new NodeStore(storeData);
        var t = new Transaction(store);

        var articles = Helper.GenerateArticles(2000);
        t.Insert(articles);
        store.Execute(t);

        // just testing data stored ( and cached )
        testData(store, articles);

        // clearing cache and forcing loading of segments from original store
        store.Maintenance(MaintenanceAction.ClearCache);
        testData(store, articles);

        // optimizing store and forcing rewrite of segments and update of segments in node store
        store.Maintenance(MaintenanceAction.TruncateLog);
        testData(store, articles);

        store.Dispose();

        // opening store again and testing that result is still the same ( causes new indexes to generated )
        storeData = DataStoreLocal.Open(datamodel, null, io);
        store = new NodeStore(storeData);

        testData(store, articles);

        store.Dispose();

    }



}
