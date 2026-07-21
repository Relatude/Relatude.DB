using Relatude.DB.DataStores;
using Relatude.DB.Query;
using Relatude.DB.Utils;
using Relatude.DB.Nodes;
using static Tests.QueryTestHelpers;

namespace Tests;

[TestClass]
public class EnumQueryTests {

    [TestMethod]
    public void TestEnums() {
        var datamodel = Helper.GetDatamodel();
        var storeData = DataStoreLocal.Open(datamodel);
        var store = new NodeStore(storeData);
        var t = new Transaction(store);
        var articles = Helper.GenerateArticles(1000);
        t.Insert(articles);
        store.Execute(t);
        store.Flush();
        // Enum query
        {
            var sumUsingStore = store.Query<Article>().Where(c => c.Size == Sizes.Small).Count();
            var sumUsingLinQ = store.Query<Article>().ToList().Where(c => c.Size == Sizes.Small).Count();
            Assert.IsTrue(sumUsingLinQ == sumUsingStore);
        }
        // Enum query 2
        {
            var sizes = new Sizes[] { Sizes.Small, Sizes.Medium };
            var siss = new[] { 1, 2 };

            var sumUsingStore = store.Query<Article>().WhereIn(c => c.Size, sizes).Count();
            var sumUsingLinQ = store.Query<Article>().ToList().Where(c => sizes.Contains(c.Size)).Count();
            Assert.IsTrue(sumUsingLinQ == sumUsingStore);
        }
    }

    [TestMethod]
    public void TestEnumQueriesComparedToLinq() {
        var store = OpenStoreWithArticles(300);
        var all = store.Query<Article>().ToList();

        { // enum inequality
            var fromStore = store.Query<Article>().Where(c => c.Size != Sizes.Small).Count();
            var fromLinq = all.Count(c => c.Size != Sizes.Small);
            Assert.AreEqual(fromLinq, fromStore);
        }

        { // enum equality ORed
            var fromStore = store.Query<Article>().Where(c => c.Size == Sizes.Small || c.Size == Sizes.Large).Count();
            var fromLinq = all.Count(c => c.Size == Sizes.Small || c.Size == Sizes.Large);
            Assert.AreEqual(fromLinq, fromStore);
        }

        { // enum combined with numeric predicate
            var fromStore = store.Query<Article>().Where(c => c.Size == Sizes.Medium && c.IntegerNum > 4).Count();
            var fromLinq = all.Count(c => c.Size == Sizes.Medium && c.IntegerNum > 4);
            Assert.AreEqual(fromLinq, fromStore);
        }

        { // all sizes via WhereIn equals total
            var sizes = new[] { Sizes.Small, Sizes.Medium, Sizes.Large };
            var fromStore = store.Query<Article>().WhereIn(c => c.Size, sizes).Count();
            Assert.AreEqual(all.Count, fromStore);
        }

        store.Dispose();
    }
}
