using Relatude.DB.DataStores;
using Relatude.DB.Query;
using Relatude.DB.Utils;
using Relatude.DB.Nodes;
using static Tests.QueryTestHelpers;

namespace Tests;

[TestClass]
public class AggregateTests {

    [TestMethod]
    public void TestCountAsync() {
        var datamodel = Helper.GetDatamodel();
        var storeData = DataStoreLocal.Open(datamodel);
        var store = new NodeStore(storeData);
        var articles = Helper.GenerateArticles(30);
        store.Insert(articles);

        var syncCount = store.Query<Article>().Count();
        var asyncCount = store.Query<Article>().CountAsync().GetAwaiter().GetResult();

        Assert.AreEqual(syncCount, asyncCount);
        store.Dispose();
    }

    [TestMethod]
    public void TestSumComparedToLinq() {
        var store = OpenStoreWithArticles(300);
        var all = store.Query<Article>().ToList();

        { // int sum with multiplication
            var fromStore = store.Query<Article>().Sum(x => x.IntegerNum * 2);
            var fromLinq = all.Sum(x => x.IntegerNum * 2);
            Assert.AreEqual(fromLinq, fromStore);
        }

        { // int sum with subtraction
            var fromStore = store.Query<Article>().Sum(x => x.IntegerNum - 3);
            var fromLinq = all.Sum(x => x.IntegerNum - 3);
            Assert.AreEqual(fromLinq, fromStore);
        }

        { // double sum with division
            var fromStore = store.Query<Article>().Sum(x => x.DoubleNum / 2.0);
            var fromLinq = all.Sum(x => x.DoubleNum / 2.0);
            Assert.AreEqual(fromLinq, fromStore, 1e-6);
        }

        { // sum mixing two properties
            var fromStore = store.Query<Article>().Sum(x => x.DoubleNum * x.IntegerNum);
            var fromLinq = all.Sum(x => x.DoubleNum * x.IntegerNum);
            Assert.AreEqual(fromLinq, fromStore, 1e-6);
        }

        { // sum over filtered query
            var fromStore = store.Query<Article>().Where(x => x.IntegerNum > 5).Sum(x => x.IntegerNum);
            var fromLinq = all.Where(x => x.IntegerNum > 5).Sum(x => x.IntegerNum);
            Assert.AreEqual(fromLinq, fromStore);
        }

        { // sum over empty result
            var fromStore = store.Query<Article>().Where(x => x.IntegerNum > 1000).Sum(x => x.IntegerNum);
            Assert.AreEqual(0, fromStore);
        }

        store.Dispose();
    }

    [TestMethod]
    public void TestCountComparedToLinq() {
        var store = OpenStoreWithArticles(200);
        var all = store.Query<Article>().ToList();

        { // Count matches materialized count
            Assert.AreEqual(all.Count, store.Query<Article>().Count());
        }

        { // Count equals Execute().Count()
            var q1 = store.Query<Article>().Where(c => c.IntegerNum > 3).Count();
            var q2 = store.Query<Article>().Where(c => c.IntegerNum > 3).Execute().Count();
            Assert.AreEqual(q2, q1);
        }

        { // Count with complex predicate
            var fromStore = store.Query<Article>().Where(c => (c.IntegerNum > 7 || c.DoubleNum < 2) && c.Size != Sizes.Medium).Count();
            var fromLinq = all.Count(c => (c.IntegerNum > 7 || c.DoubleNum < 2) && c.Size != Sizes.Medium);
            Assert.AreEqual(fromLinq, fromStore);
        }

        { // Count on empty result
            var fromStore = store.Query<Article>().Where(c => c.Name == "does not exist").Count();
            Assert.AreEqual(0, fromStore);
        }

        store.Dispose();
    }

    [TestMethod]
    public void TestFirstOrDefault() {
        var datamodel = Helper.GetDatamodel();
        var storeData = DataStoreLocal.Open(datamodel);
        var store = new NodeStore(storeData);
        var articles = Helper.GenerateArticles(10);
        store.Insert(articles);

        { // returns item when found
            var item = store.Query<Article>().Where(c => c.Id == 5).FirstOrDefault();
            Assert.IsNotNull(item);
            Assert.AreEqual(5, item.Id);
        }

        { // returns null when not found
            var item = store.Query<Article>().Where(c => c.Id == 9999).FirstOrDefault();
            Assert.IsNull(item);
        }

        store.Dispose();
    }

    [TestMethod]
    public void TestFirstAndSingleComparedToLinq() {
        var store = OpenStoreWithArticles(50);
        var all = store.Query<Article>().ToList();

        { // First on ordered query
            var fromStore = store.Query<Article>().OrderBy(c => c.Id2).First();
            var fromLinq = all.OrderBy(c => c.Id2).First();
            Assert.AreEqual(fromLinq.Id, fromStore.Id);
        }

        { // First on ordered descending query
            var fromStore = store.Query<Article>().OrderByDescending(c => c.Id2).First();
            var fromLinq = all.OrderByDescending(c => c.Id2).First();
            Assert.AreEqual(fromLinq.Id, fromStore.Id);
        }

        { // Single via unique filter
            var fromStore = store.Query<Article>().Where(c => c.Name == "Test 25").Single();
            var fromLinq = all.Single(c => c.Name == "Test 25");
            Assert.AreEqual(fromLinq.Id, fromStore.Id);
        }

        { // TryGet found
            Assert.IsTrue(store.Query<Article>().Where(c => c.Name == "Test 25").TryGet(out var item));
            Assert.AreEqual(25, item!.Id);
        }

        { // TryGet not found
            Assert.IsFalse(store.Query<Article>().Where(c => c.Name == "no such").TryGet(out _));
        }

        store.Dispose();
    }
}
