using Relatude.DB.DataStores;
using Relatude.DB.Query;
using Relatude.DB.Utils;
using Relatude.DB.Nodes;
using static Tests.QueryTestHelpers;

namespace Tests;

[TestClass]
public class SelectionTests {

    [TestMethod]
    public void TestSelectId() {
        var datamodel = Helper.GetDatamodel();
        var storeData = DataStoreLocal.Open(datamodel);
        var store = new NodeStore(storeData);
        var articles = Helper.GenerateArticles(20);
        store.Insert(articles);

        var ids = store.Query<Article>().SelectId().Execute().ToList();
        Assert.AreEqual(20, ids.Count);
        Assert.IsTrue(ids.All(id => id != Guid.Empty));

        store.Dispose();
    }

    [TestMethod]
    public void TestSelectComparedToLinq() {
        var store = OpenStoreWithArticles(100);
        var all = store.Query<Article>().ToList();

        { // anonymous projection of two properties
            var fromStore = store.Query<Article>().OrderBy(c => c.Id2).Select(c => new { c.Name, c.IntegerNum }).Execute().ToList();
            var fromLinq = all.OrderBy(c => c.Id2).Select(c => new { c.Name, c.IntegerNum }).ToList();
            Assert.AreEqual(fromLinq.Count, fromStore.Count);
            for (var i = 0; i < fromLinq.Count; i++) {
                Assert.AreEqual(fromLinq[i].Name, fromStore[i].Name);
                Assert.AreEqual(fromLinq[i].IntegerNum, fromStore[i].IntegerNum);
            }
        }

        { // projection with renamed members
            var fromStore = store.Query<Article>().OrderBy(c => c.Id2).Select(c => new { name = c.Name, num = c.DoubleNum }).Execute().ToList();
            var fromLinq = all.OrderBy(c => c.Id2).Select(c => new { name = c.Name, num = c.DoubleNum }).ToList();
            Assert.AreEqual(fromLinq.Count, fromStore.Count);
            for (var i = 0; i < fromLinq.Count; i++) {
                Assert.AreEqual(fromLinq[i].name, fromStore[i].name);
                Assert.AreEqual(fromLinq[i].num, fromStore[i].num, 1e-9);
            }
        }

        { // projection combined with Where
            var fromStore = store.Query<Article>().Where(c => c.IntegerNum > 5).OrderBy(c => c.Id2).Select(c => new { c.Id2 }).Execute().Select(x => x.Id2).ToList();
            var fromLinq = all.Where(c => c.IntegerNum > 5).OrderBy(c => c.Id2).Select(c => c.Id2).ToList();
            CollectionAssert.AreEqual(fromLinq, fromStore);
        }

        store.Dispose();
    }
}
