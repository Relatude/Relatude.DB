using Relatude.DB.DataStores;
using Relatude.DB.Query;
using Relatude.DB.Utils;
using Relatude.DB.Nodes;
using static Tests.QueryTestHelpers;

namespace Tests;

[TestClass]
public class OrderingAndPagingTests {

    [TestMethod]
    public void TestOrderBy() {
        var datamodel = Helper.GetDatamodel();
        var storeData = DataStoreLocal.Open(datamodel);
        var store = new NodeStore(storeData);
        var articles = Helper.GenerateArticles(50);
        store.Insert(articles);

        { // ascending by IntegerNum
            var fromStore = store.Query<Article>().OrderBy(c => c.IntegerNum).Execute().Select(c => c.IntegerNum).ToList();
            for (int i = 1; i < fromStore.Count; i++)
                Assert.IsTrue(fromStore[i] >= fromStore[i - 1]);
        }

        { // descending by IntegerNum
            var fromStore = store.Query<Article>().OrderByDescending(c => c.IntegerNum).Execute().Select(c => c.IntegerNum).ToList();
            for (int i = 1; i < fromStore.Count; i++)
                Assert.IsTrue(fromStore[i] <= fromStore[i - 1]);
        }

        { // ascending by Name
            var fromStore = store.Query<Article>().OrderBy(c => c.Name).Execute().Select(c => c.Name).ToList();
            var sorted = fromStore.OrderBy(n => n).ToList();
            CollectionAssert.AreEqual(sorted, fromStore);
        }

        store.Dispose();
    }

    [TestMethod]
    public void TestSkipAndTake() {
        var datamodel = Helper.GetDatamodel();
        var storeData = DataStoreLocal.Open(datamodel);
        var store = new NodeStore(storeData);
        var articles = Helper.GenerateArticles(50);
        store.Insert(articles);

        var total = store.Query<Article>().Count();
        Assert.AreEqual(50, total);

        { // Take
            var taken = store.Query<Article>().Take(10).Execute().ToList();
            Assert.AreEqual(10, taken.Count);
        }

        { // Skip
            var skipped = store.Query<Article>().Skip(40).Execute().ToList();
            Assert.AreEqual(10, skipped.Count);
        }

        { // Page
            var page = store.Query<Article>().Page(0, 10).Execute().ToList();
            Assert.AreEqual(10, page.Count);

            var page2 = store.Query<Article>().Page(1, 10).Execute().ToList();
            Assert.AreEqual(10, page2.Count);

            // pages should not overlap
            Assert.IsFalse(page.Any(a => page2.Any(b => b.Id == a.Id)));
        }

        store.Dispose();
    }

    [TestMethod]
    public void TestOrderByComparedToLinq() {
        var store = OpenStoreWithArticles(200);
        var all = store.Query<Article>().ToList();

        { // ascending by int, compare full key sequence (tie insensitive)
            var fromStore = store.Query<Article>().OrderBy(c => c.IntegerNum).Execute().Select(c => c.IntegerNum).ToList();
            var fromLinq = all.OrderBy(c => c.IntegerNum).Select(c => c.IntegerNum).ToList();
            CollectionAssert.AreEqual(fromLinq, fromStore);
        }

        { // descending by int
            var fromStore = store.Query<Article>().OrderByDescending(c => c.IntegerNum).Execute().Select(c => c.IntegerNum).ToList();
            var fromLinq = all.OrderByDescending(c => c.IntegerNum).Select(c => c.IntegerNum).ToList();
            CollectionAssert.AreEqual(fromLinq, fromStore);
        }

        { // ascending by double
            var fromStore = store.Query<Article>().OrderBy(c => c.DoubleNum).Execute().Select(c => c.DoubleNum).ToList();
            var fromLinq = all.OrderBy(c => c.DoubleNum).Select(c => c.DoubleNum).ToList();
            CollectionAssert.AreEqual(fromLinq, fromStore);
        }

        { // ordering by unique key gives fully deterministic sequence
            var fromStore = store.Query<Article>().OrderBy(c => c.Id2).Execute().Select(c => c.Id).ToList();
            var fromLinq = all.OrderBy(c => c.Id2).Select(c => c.Id).ToList();
            CollectionAssert.AreEqual(fromLinq, fromStore);
        }

        { // Where + OrderBy combined
            var fromStore = store.Query<Article>().Where(c => c.IntegerNum > 4).OrderBy(c => c.Id2).Execute().Select(c => c.Id).ToList();
            var fromLinq = all.Where(c => c.IntegerNum > 4).OrderBy(c => c.Id2).Select(c => c.Id).ToList();
            CollectionAssert.AreEqual(fromLinq, fromStore);
        }

        store.Dispose();
    }

    [TestMethod]
    public void TestOrderBySkipTakeComparedToLinq() {
        var store = OpenStoreWithArticles(100);
        var all = store.Query<Article>().ToList();

        { // Skip + Take on ordered query
            var fromStore = store.Query<Article>().OrderBy(c => c.Id2).Skip(10).Take(5).Execute().Select(c => c.Id).ToList();
            var fromLinq = all.OrderBy(c => c.Id2).Skip(10).Take(5).Select(c => c.Id).ToList();
            CollectionAssert.AreEqual(fromLinq, fromStore);
        }

        { // Page on ordered query matches LINQ Skip/Take
            var fromStore = store.Query<Article>().OrderBy(c => c.Id2).Page(2, 10).Execute().Select(c => c.Id).ToList();
            var fromLinq = all.OrderBy(c => c.Id2).Skip(20).Take(10).Select(c => c.Id).ToList();
            CollectionAssert.AreEqual(fromLinq, fromStore);
        }

        { // concatenated pages equal full ordered list
            var pages = new List<int>();
            for (var p = 0; p < 10; p++)
                pages.AddRange(store.Query<Article>().OrderBy(c => c.Id2).Page(p, 10).Execute().Select(c => c.Id));
            var fromLinq = all.OrderBy(c => c.Id2).Select(c => c.Id).ToList();
            CollectionAssert.AreEqual(fromLinq, pages);
        }

        { // skip beyond count returns empty
            var fromStore = store.Query<Article>().Skip(1000).Execute().ToList();
            Assert.AreEqual(0, fromStore.Count);
        }

        { // take more than count returns all
            var fromStore = store.Query<Article>().Take(1000).Execute().ToList();
            Assert.AreEqual(all.Count, fromStore.Count);
        }

        store.Dispose();
    }

    [TestMethod]
    public void TestExecuteTotalCountComparedToLinq() {
        var store = OpenStoreWithArticles(200);
        var all = store.Query<Article>().ToList();

        // TotalCount reports the full match count even when a page is requested
        var page = ((QueryOfNodes<Article, Article>)store.Query<Article>().Where(c => c.IntegerNum > 3).OrderBy(c => c.Id2).Page(0, 10)).Execute(out var totalCount).ToList();
        var fromLinq = all.Count(c => c.IntegerNum > 3);

        Assert.AreEqual(10, page.Count);
        Assert.AreEqual(fromLinq, totalCount);

        store.Dispose();
    }
}
