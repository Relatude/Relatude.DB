using Relatude.DB.DataStores;
using Relatude.DB.Query;
using Relatude.DB.Utils;
using Relatude.DB.Nodes;

namespace Tests;

[TestClass]
public class RelationQueryTests {

    [TestMethod]
    public void TestingRelationQueries() {
        var datamodel = Helper.GetDatamodel();
        var storeData = DataStoreLocal.Open(datamodel);
        var store = new NodeStore(storeData);
        var t = new Transaction(store);
        var articles = Helper.GenerateArticles(1000);
        t.Insert(articles);
        store.Execute(t);
        var result = store.Query<Article>().Where(a => a.Author == null).Select(c => new { name = c.Name, num = c.DoubleNum }).Execute().ToArray();
        var g = Guid.NewGuid();
        store.Query<Article>().Where(a => a.Children.Has(g)).Select(c => new { name = c.Name, num = c.DoubleNum }).Execute().ToArray();
        store.Dispose();
    }

    [TestMethod]
    public void TestWhereRelates() {
        var datamodel = Helper.GetDatamodel();
        var storeData = DataStoreLocal.Open(datamodel);
        var store = new NodeStore(storeData);

        var parent = new Article { Id = 1, Name = "Parent" };
        var child1 = new Article { Id = 2, Name = "Child 1" };
        var child2 = new Article { Id = 3, Name = "Child 2" };
        var unrelated = new Article { Id = 4, Name = "Unrelated" };

        store.Insert(parent);
        store.Insert(child1);
        store.Insert(child2);
        store.Insert(unrelated);
        store.AddRelation(child1, a => a.Parent, parent);
        store.AddRelation(child2, a => a.Parent, parent);

        var parentNode = store.Query<Article>().Where(a => a.Id == 1).Execute().First();

        { // WhereRelates: find children of parent
            var children = store.Query<Article>().WhereRelates(a => a.Parent, parentNode.PId).Execute().ToList();
            Assert.AreEqual(2, children.Count);
            Assert.IsTrue(children.All(c => c.Name.StartsWith("Child")));
        }

        { // WhereNotRelates: find articles NOT related to parent
            var notChildren = store.Query<Article>().WhereNotRelates(a => a.Parent, parentNode.PId).Execute().ToList();
            Assert.IsTrue(notChildren.All(a => a.Name != "Child 1" && a.Name != "Child 2"));
        }

        store.Dispose();
    }

    [TestMethod]
    public void TestWhereRelatesAny() {
        var datamodel = Helper.GetDatamodel();
        var storeData = DataStoreLocal.Open(datamodel);
        var store = new NodeStore(storeData);

        var parent1 = new Article { Id = 1, Name = "Parent 1" };
        var parent2 = new Article { Id = 2, Name = "Parent 2" };
        var child1 = new Article { Id = 3, Name = "Child 1" };
        var child2 = new Article { Id = 4, Name = "Child 2" };

        store.Insert(parent1);
        store.Insert(parent2);
        store.Insert(child1);
        store.Insert(child2);
        store.AddRelation(child1, a => a.Parent, parent1);
        store.AddRelation(child2, a => a.Parent, parent2);

        var p1 = store.Query<Article>().Where(a => a.Id == 1).Execute().First();
        var p2 = store.Query<Article>().Where(a => a.Id == 2).Execute().First();

        var children = store.Query<Article>().WhereRelatesAny(a => a.Parent, [p1.PId, p2.PId]).Execute().ToList();
        Assert.AreEqual(2, children.Count);

        store.Dispose();
    }

    [TestMethod]
    public void TestIncludeRelations() {
        var datamodel = Helper.GetDatamodel();
        var storeData = DataStoreLocal.Open(datamodel);
        var store = new NodeStore(storeData);

        var parent = new Article { Id = 1, Name = "Parent" };
        var child = new Article { Id = 2, Name = "Child" };

        store.Insert(parent);
        store.Insert(child);
        store.AddRelation(child, a => a.Parent, parent);

        { // Include parent
            var children = store.Query<Article>().Where(c => c.Name == "Child").Include(a => a.Parent).Execute().ToList();
            Assert.AreEqual(1, children.Count);
            Assert.IsNotNull(children[0].Parent);
            Assert.AreEqual("Parent", children[0].Parent!.Name);
        }

        { // Include children collection
            var parents = store.Query<Article>().Where(c => c.Name == "Parent").Include(a => a.Children).Execute().ToList();
            Assert.AreEqual(1, parents.Count);
            Assert.AreEqual(1, parents[0].Children.Count());
        }

        store.Dispose();
    }

    [TestMethod]
    public void TestingDelete() {
        var datamodel = Helper.GetDatamodel();
        var storeData = DataStoreLocal.Open(datamodel);
        var store = new NodeStore(storeData);

        var article1 = new Article { Id = 10, Name = "Test 1", DoubleNum = 1.1 };
        var article2 = new Article { Id = 100, Name = "Test 2", DoubleNum = 1.1 };
        store.Insert(article1);
        store.Insert(article2);
        store.AddRelation(article1, a => a.Parent, article2);
        var articles = store.Query<Article>().Include(a => a.Parent).Include(a => a.Children).Execute().ToArray();
        Assert.IsTrue(articles[1].Children.Count() == 1);
        store.Delete(article2.Id);
        var articles2 = store.Query<Article>().Include(a => a.Parent).Include(a => a.Children).Execute().ToArray();
        Assert.IsTrue(articles2[0].Children.Count() == 0);
        store.Dispose();


    }
}
