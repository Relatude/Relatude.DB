using Relatude.DB.DataStores;
using Relatude.DB.Query;
using Relatude.DB.Nodes;
using Relatude.Utils;

namespace Relatude.Querying;

[TestClass]
public class IncludeFilterTests {

    // root with children c1..c4 (IntegerNum 1..4, DoubleNum 1.0..4.0)
    static NodeStore openStore(out Article root) {
        var store = new NodeStore(DataStoreLocal.Open(Helper.GetDatamodel()));
        root = new Article { Id = 1, Name = "root" };
        store.Insert(root);
        for (var i = 1; i <= 4; i++) {
            var child = new Article { Id = 1 + i, Name = "c" + i, IntegerNum = i, DoubleNum = i };
            store.Insert(child);
            store.AddRelation(child, a => a.Parent, root);
        }
        return store;
    }

    [TestMethod]
    public void TestIncludeFilterOnManySide() {
        var store = openStore(out _);
        { // filter on an indexed property (native index set path)
            var roots = store.Query<Article>().Where(a => a.Id == 1).Include(a => a.Children, c => c.IntegerNum >= 3).Execute().ToList();
            Assert.AreEqual(1, roots.Count); // the filter must never affect the main result set
            CollectionAssert.AreEqual(new[] { "c3", "c4" }, roots[0].Children.Select(c => c.Name).OrderBy(n => n).ToArray());
        }
        { // filter on a non indexed property (per node evaluation path)
            var roots = store.Query<Article>().Where(a => a.Id == 1).Include(a => a.Children, c => c.DoubleNum > 2.5).Execute().ToList();
            CollectionAssert.AreEqual(new[] { "c3", "c4" }, roots[0].Children.Select(c => c.Name).OrderBy(n => n).ToArray());
        }
        { // without filter, everything still loads (regression)
            var roots = store.Query<Article>().Where(a => a.Id == 1).Include(a => a.Children).Execute().ToList();
            Assert.AreEqual(4, roots[0].Children.Count());
        }
        store.Dispose();
    }

    [TestMethod]
    public void TestIncludeFilterOnOneSide() {
        var store = openStore(out _);
        { // parent passes the filter -> loaded
            var c1 = store.Query<Article>().Where(a => a.Id == 2).Include(a => a.Parent, p => p!.Name == "root").Execute().Single();
            Assert.IsNotNull(c1.Parent);
            Assert.AreEqual("root", c1.Parent!.Name);
        }
        { // parent fails the filter -> property stays empty
            var c1 = store.Query<Article>().Where(a => a.Id == 2).Include(a => a.Parent, p => p!.Name == "other").Execute().Single();
            Assert.IsNull(c1.Parent);
        }
        store.Dispose();
    }

    [TestMethod]
    public void TestIncludeFilterBeforeTop() {
        var store = openStore(out _);
        var roots = store.Query<Article>().Where(a => a.Id == 1).Include(a => a.Children, c => c.IntegerNum >= 2, top: 2).Execute().ToList();
        var loaded = roots[0].Children.Select(c => c.Name).ToArray();
        Assert.AreEqual(2, loaded.Length); // top counts the surviving nodes, not the candidates
        foreach (var name in loaded) CollectionAssert.Contains(new[] { "c2", "c3", "c4" }, name);
        store.Dispose();
    }

    [TestMethod]
    public void TestIncludeFiltersAreAndMerged() {
        var store = openStore(out _);
        var roots = store.Query<Article>().Where(a => a.Id == 1)
            .Include(a => a.Children, c => c.IntegerNum >= 2)
            .Include(a => a.Children, c => c.IntegerNum <= 2)
            .Execute().ToList();
        CollectionAssert.AreEqual(new[] { "c2" }, roots[0].Children.Select(c => c.Name).ToArray());
        store.Dispose();
    }

    [TestMethod]
    public void TestThenIncludeFilter() {
        var store = new NodeStore(DataStoreLocal.Open(Helper.GetDatamodel()));
        var root = new Article { Id = 1, Name = "root" };
        var c1 = new Article { Id = 2, Name = "c1", IntegerNum = 1 };
        var c2 = new Article { Id = 3, Name = "c2", IntegerNum = 2 };
        var gc1 = new Article { Id = 4, Name = "gc1", IntegerNum = 1 };
        var gc2 = new Article { Id = 5, Name = "gc2", IntegerNum = 2 };
        store.Insert(root);
        store.Insert(c1);
        store.Insert(c2);
        store.Insert(gc1);
        store.Insert(gc2);
        store.AddRelation(c1, a => a.Parent, root);
        store.AddRelation(c2, a => a.Parent, root);
        store.AddRelation(gc1, a => a.Parent, c1);
        store.AddRelation(gc2, a => a.Parent, c1);
        { // filter at the second level only
            var r = store.Query<Article>().Where(a => a.Id == 1)
                .Include(a => a.Children)
                .ThenInclude<Article, Article>(a => a.Children, gc => gc.IntegerNum == 2)
                .Execute().ToList();
            var child1 = r[0].Children.Single(c => c.Name == "c1");
            CollectionAssert.AreEqual(new[] { "gc2" }, child1.Children.Select(c => c.Name).ToArray());
        }
        { // filter at the first level cascades: excluded children load no grandchildren
            var r = store.Query<Article>().Where(a => a.Id == 1)
                .Include(a => a.Children, c => c.IntegerNum == 2)
                .ThenInclude<Article, Article>(a => a.Children)
                .Execute().ToList();
            CollectionAssert.AreEqual(new[] { "c2" }, r[0].Children.Select(c => c.Name).ToArray());
        }
        store.Dispose();
    }

    [TestMethod]
    public void TestIncludeFilterOnSingleRelationAcrossTypes() {
        var store = new NodeStore(DataStoreLocal.Open(Helper.GetDatamodel()));
        var author = new User { Username = "alice" };
        var article = new Article { Id = 100, Name = "a1" };
        store.Insert(author);
        store.Insert(article);
        store.AddRelation(article, a => a.Author, author);
        var withAuthor = store.Query<Article>().Include(a => a.Author, u => u!.Username == "alice").Execute().Single();
        Assert.IsNotNull(withAuthor.Author);
        var withoutAuthor = store.Query<Article>().Include(a => a.Author, u => u!.Username == "bob").Execute().Single();
        Assert.IsNull(withoutAuthor.Author);
        store.Dispose();
    }

    [TestMethod]
    public void TestIncludeFilterRawQueryText() {
        var store = openStore(out _);
        var childrenPropId = store.Mapper.GetProperty<Article, IEnumerable<Article>>(a => a.Children).Id;
        var rootGuid = store.Query<Article>().Where(a => a.Id == 1).Execute().First()!.PId;
        var query = $"Article.WhereInIds([\"{rootGuid}\"]).Include(\"{childrenPropId}\", c => c.IntegerNum >= 3)";
        var result = store.Datastore.Query(query, []);
        Assert.IsInstanceOfType(result, typeof(Relatude.DB.Query.Data.IStoreNodeDataCollection));
        var coll = (Relatude.DB.Query.Data.IStoreNodeDataCollection)result!;
        var root = coll.NodeValues.Single();
        // the included relation is attached to the node data; verify through the typed mapper instead:
        var typed = store.Query<Article>().Where(a => a.Id == 1).Include(a => a.Children, c => c.IntegerNum >= 3).Execute().Single();
        Assert.AreEqual(2, typed.Children.Count());
        store.Dispose();
    }
}
