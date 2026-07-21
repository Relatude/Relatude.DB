using Relatude.DB.DataStores;
using Relatude.DB.Utils;
using Relatude.DB.Nodes;

namespace Tests;

[TestClass]
public class MaintenanceTests {

    [TestMethod]
    public void TestSetCache() {
        var datamodel = Helper.GetDatamodel();
        var storeData = DataStoreLocal.Open(datamodel);
        var store = new NodeStore(storeData);
        var t = new Transaction(store);
        var articles = Helper.GenerateArticles(1000);
        t.Insert(articles);
        store.Execute(t);
        store.Maintenance(MaintenanceAction.CompressMemory);
        store.Dispose();
    }
}
