using Relatude.DB.DataStores;
using Relatude.DB.Query;
using Relatude.DB.Utils;
using Relatude.DB.Nodes;
using static Tests.QueryTestHelpers;

namespace Tests;

[TestClass]
public class WhereTests {

    [TestMethod]
    public void QueryWhereExpressions() {
        var datamodel = Helper.GetDatamodel();
        var storeData = DataStoreLocal.Open(datamodel);
        var store = new NodeStore(storeData);
        var t = new Transaction(store);
        var articles = Helper.GenerateArticles(1000);
        t.Insert(articles);
        store.Execute(t);

        // var simpleArticles = store.Query<Article>().Execute().ToArray();

        //var n = 0;
        //var ba = store.Query<Article>().Execute();
        //var sw = Stopwatch.StartNew();
        //var result = store.Query<Article>().Where(x => x.Name != ("Test " + n)).Execute().ToList().Sum(t=>t.Num);
        //var names = store.Query<Article>().Where(x => x.Name != ("Test " + n)).Select(x => new { t = x.Name }).Execute().ToList();

        { // integer sums
            var sumUsingStore = store.Query<Article>().Execute().Sum(x => x.IntegerNum);
            var sumUsingLinQ = store.Query<Article>().Sum(x => x.IntegerNum);
            Assert.IsTrue(sumUsingLinQ == sumUsingStore);
            var sumUsingStore2 = store.Query<Article>().Execute().Sum(x => x.IntegerNum + 20);
            var sumUsingLinQ2 = store.Query<Article>().Sum(x => x.IntegerNum + 20);
            Assert.IsTrue(sumUsingLinQ2 == sumUsingStore2);
        }

        { // double sum
            var sumUsingStore = store.Query<Article>().Sum(x => x.DoubleNum);
            var sumUsingLinQ = store.Query<Article>().ToList().Sum(x => x.DoubleNum);
            Assert.IsTrue(sumUsingLinQ == sumUsingStore);
            var sumUsingStore2 = store.Query<Article>().Sum(x => x.DoubleNum + 20);
            var sumUsingLinQ2 = store.Query<Article>().ToList().Sum(x => x.DoubleNum + 20);
            Assert.IsTrue(sumUsingLinQ2 == sumUsingStore2);
        }

        { // minus prefix Expression
            var sumUsingStore = store.Query<Article>().Sum(x => -x.DoubleNum + -x.IntegerNum * 10 + (20 * 2 + 2));
            var sumUsingLinQ = store.Query<Article>().ToList().Sum(x => -x.DoubleNum + -x.IntegerNum * 10 + (20 * 2 + 2));
            Assert.IsTrue(sumUsingLinQ == sumUsingStore);
        }

        { // not prefix Expressions
            var sumUsingStore = store.Query<Article>().Where(c => !(c.DoubleNum > 10)).Count();
            var sumUsingLinQ = store.Query<Article>().Where(c => !(c.DoubleNum > 10)).Execute().Count();
            Assert.IsTrue(sumUsingLinQ == sumUsingStore);
        }
        //{ // Select expressions
        //    var sumUsingLinQ = store.Query<Article>().ToList().Select(c => new { c.Name, c.DoubleNum });
        //    var sumUsingStore = store.Query<Article>().Select(c => new { c.Name, b = c.Name + 1.2 }).Execute().ToArray();
        //Assert.IsTrue(sumUsingLinQ == sumUsingStore);
        //}

        { // computable constants
            var articleUsingStore = store.Query<Article>().Where(c => c.Id == DateTime.Now.Day).First();
            var articleUsingLinq = store.Query<Article>().Execute().First(c => c.Id == DateTime.Now.Day);
            Assert.AreEqual(articleUsingStore.Id, articleUsingLinq.Id);
        }

        { // methods
            var getDay = (string test) => DateTime.Now.Day;

            var articleUsingStore = store.Query<Article>().Where(c => c.Id == getDay("asd")).First();
            var articleUsingLinq = store.Query<Article>().Execute().First(c => c.Id == getDay("asd"));
            Assert.AreEqual(articleUsingStore.Id, articleUsingLinq.Id);
        }

        {  // Range query
            var fromStore = store.Query<Article>().Where(c => c.IntegerNum > 2 && c.IntegerNum < 8).Count();
            var fromLinq = store.Query<Article>().ToList().Count(c => c.IntegerNum > 2 && c.IntegerNum < 8);
            Assert.AreEqual(fromLinq, fromStore);

            var fromStore2 = store.Query<Article>().Where(c => c.IntegerNum >= 3 && c.IntegerNum <= 7).Count();
            var fromLinq2 = store.Query<Article>().ToList().Count(c => c.IntegerNum >= 3 && c.IntegerNum <= 7);
            Assert.AreEqual(fromLinq2, fromStore2);
        }
    }

    [TestMethod]
    public void TestWhereStringComparisons() {
        var datamodel = Helper.GetDatamodel();
        var storeData = DataStoreLocal.Open(datamodel);
        var store = new NodeStore(storeData);
        var articles = Helper.GenerateArticles(100);
        store.Insert(articles);

        { // exact string equality
            var fromStore = store.Query<Article>().Where(c => c.Name == "Test 1").Count();
            var fromLinq = store.Query<Article>().ToList().Count(c => c.Name == "Test 1");
            Assert.AreEqual(fromLinq, fromStore);
        }

        { // string inequality
            var fromStore = store.Query<Article>().Where(c => c.Name != "Test 1").Count();
            var fromLinq = store.Query<Article>().ToList().Count(c => c.Name != "Test 1");
            Assert.AreEqual(fromLinq, fromStore);
        }

        store.Dispose();
    }

    [TestMethod]
    public void TestWhereNumericComparisons() {
        var datamodel = Helper.GetDatamodel();
        var storeData = DataStoreLocal.Open(datamodel);
        var store = new NodeStore(storeData);
        var articles = Helper.GenerateArticles(100);
        store.Insert(articles);

        { // greater than
            var fromStore = store.Query<Article>().Where(c => c.IntegerNum > 5).Count();
            var fromLinq = store.Query<Article>().ToList().Count(c => c.IntegerNum > 5);
            Assert.AreEqual(fromLinq, fromStore);
        }

        { // less than
            var fromStore = store.Query<Article>().Where(c => c.IntegerNum < 3).Count();
            var fromLinq = store.Query<Article>().ToList().Count(c => c.IntegerNum < 3);
            Assert.AreEqual(fromLinq, fromStore);
        }

        { // greater than or equal
            var fromStore = store.Query<Article>().Where(c => c.IntegerNum >= 7).Count();
            var fromLinq = store.Query<Article>().ToList().Count(c => c.IntegerNum >= 7);
            Assert.AreEqual(fromLinq, fromStore);
        }

        { // less than or equal
            var fromStore = store.Query<Article>().Where(c => c.IntegerNum <= 2).Count();
            var fromLinq = store.Query<Article>().ToList().Count(c => c.IntegerNum <= 2);
            Assert.AreEqual(fromLinq, fromStore);
        }

        { // double greater than
            var fromStore = store.Query<Article>().Where(c => c.DoubleNum > 5.0).Count();
            var fromLinq = store.Query<Article>().ToList().Count(c => c.DoubleNum > 5.0);
            Assert.AreEqual(fromLinq, fromStore);
        }

        store.Dispose();
    }

    [TestMethod]
    public void TestWhereAndOrCombinations() {
        var datamodel = Helper.GetDatamodel();
        var storeData = DataStoreLocal.Open(datamodel);
        var store = new NodeStore(storeData);
        var articles = Helper.GenerateArticles(100);
        store.Insert(articles);

        { // AND condition
            var fromStore = store.Query<Article>().Where(c => c.IntegerNum > 2 && c.IntegerNum < 8).Count();
            var fromLinq = store.Query<Article>().ToList().Count(c => c.IntegerNum > 2 && c.IntegerNum < 8);
            Assert.AreEqual(fromLinq, fromStore);
        }

        { // OR condition
            var fromStore = store.Query<Article>().Where(c => c.IntegerNum == 1 || c.IntegerNum == 9).Count();
            var fromLinq = store.Query<Article>().ToList().Count(c => c.IntegerNum == 1 || c.IntegerNum == 9);
            Assert.AreEqual(fromLinq, fromStore);
        }

        { // AND + OR combined
            var fromStore = store.Query<Article>().Where(c => (c.IntegerNum < 3 || c.IntegerNum > 7) && c.DoubleNum < 5.0).Count();
            var fromLinq = store.Query<Article>().ToList().Count(c => (c.IntegerNum < 3 || c.IntegerNum > 7) && c.DoubleNum < 5.0);
            Assert.AreEqual(fromLinq, fromStore);
        }

        store.Dispose();
    }

    [TestMethod]
    public void TestWhereById() {
        var datamodel = Helper.GetDatamodel();
        var storeData = DataStoreLocal.Open(datamodel);
        var store = new NodeStore(storeData);
        var articles = Helper.GenerateArticles(20);
        store.Insert(articles);

        { // single item by id via lambda
            var result = store.Query<Article>().Where(c => c.Id == 5).Execute().ToList();
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(5, result[0].Id);
        }

        { // no match returns empty
            var result = store.Query<Article>().Where(c => c.Id == 9999).Execute().ToList();
            Assert.AreEqual(0, result.Count);
        }

        store.Dispose();
    }

    [TestMethod]
    public void TestMultipleWhereChaining() {
        var datamodel = Helper.GetDatamodel();
        var storeData = DataStoreLocal.Open(datamodel);
        var store = new NodeStore(storeData);
        var articles = Helper.GenerateArticles(200);
        store.Insert(articles);

        // Chain multiple Where calls — should AND them together
        var fromStore = store.Query<Article>()
            .Where(c => c.IntegerNum > 2)
            .Where(c => c.IntegerNum < 8)
            .Count();

        var fromLinq = store.Query<Article>().ToList()
            .Count(c => c.IntegerNum > 2 && c.IntegerNum < 8);

        Assert.AreEqual(fromLinq, fromStore);
        store.Dispose();
    }

    [TestMethod]
    public void TestWhereSubtype() {
        var datamodel = Helper.GetDatamodel();
        var storeData = DataStoreLocal.Open(datamodel);
        var store = new NodeStore(storeData);

        store.Insert(new Article { Id = 1, Name = "Base" });
        store.Insert(new Article2 { Id = 2, Name = "Derived", Name2 = "Extra" });

        // Query base type includes both
        var all = store.Query<Article>().Count();
        Assert.AreEqual(2, all);

        // Query derived type returns only Article2
        var derived = store.Query<Article2>().Count();
        Assert.AreEqual(1, derived);

        store.Dispose();
    }

    [TestMethod]
    public void TestWherePropertyToPropertyComparedToLinq() {
        var store = OpenStoreWithArticles(200);
        var all = store.Query<Article>().ToList();

        { // double vs int property
            var fromStore = store.Query<Article>().Where(c => c.DoubleNum > c.IntegerNum).Count();
            var fromLinq = all.Count(c => c.DoubleNum > c.IntegerNum);
            Assert.AreEqual(fromLinq, fromStore);
        }

        { // sum of two properties against constant
            var fromStore = store.Query<Article>().Where(c => c.IntegerNum + c.DoubleNum < 10).Count();
            var fromLinq = all.Count(c => c.IntegerNum + c.DoubleNum < 10);
            Assert.AreEqual(fromLinq, fromStore);
        }

        store.Dispose();
    }

    [TestMethod]
    public void TestWhereWithClosureVariablesComparedToLinq() {
        var store = OpenStoreWithArticles(200);
        var all = store.Query<Article>().ToList();

        { // captured int
            var min = 3;
            var fromStore = store.Query<Article>().Where(c => c.IntegerNum > min).Count();
            var fromLinq = all.Count(c => c.IntegerNum > min);
            Assert.AreEqual(fromLinq, fromStore);
        }

        { // captured int with arithmetic on the constant side
            var min = 3;
            var fromStore = store.Query<Article>().Where(c => c.IntegerNum > min + 2).Count();
            var fromLinq = all.Count(c => c.IntegerNum > min + 2);
            Assert.AreEqual(fromLinq, fromStore);
        }

        { // captured string
            var name = "Test 42";
            var fromStore = store.Query<Article>().Where(c => c.Name == name).Count();
            var fromLinq = all.Count(c => c.Name == name);
            Assert.AreEqual(1, fromStore);
            Assert.AreEqual(fromLinq, fromStore);
        }

        { // captured string built by concatenation
            var n = 42;
            var fromStore = store.Query<Article>().Where(c => c.Name == "Test " + n).Count();
            var fromLinq = all.Count(c => c.Name == "Test " + n);
            Assert.AreEqual(fromLinq, fromStore);
        }

        { // captured enum
            var size = Sizes.Large;
            var fromStore = store.Query<Article>().Where(c => c.Size == size).Count();
            var fromLinq = all.Count(c => c.Size == size);
            Assert.AreEqual(fromLinq, fromStore);
        }

        store.Dispose();
    }

    [TestMethod]
    public void TestWhereStringLambdaComparedToLinq() {
        var store = OpenStoreWithArticles(200);
        var all = store.Query<Article>().ToList();

        { // predicate given as query language string
            var fromStore = store.Query<Article>().Where("c => c.IntegerNum > 5").Count();
            var fromLinq = all.Count(c => c.IntegerNum > 5);
            Assert.AreEqual(fromLinq, fromStore);
        }

        { // string lambda with && and string equality
            var fromStore = store.Query<Article>().Where("c => c.IntegerNum > 2 && c.Name != \"Test 10\"").Count();
            var fromLinq = all.Count(c => c.IntegerNum > 2 && c.Name != "Test 10");
            Assert.AreEqual(fromLinq, fromStore);
        }

        store.Dispose();
    }
}
