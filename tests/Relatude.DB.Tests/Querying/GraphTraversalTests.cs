using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Query;
using Relatude.DB.Nodes;
using Relatude.Utils;

namespace Relatude.Querying;

[TestClass]
public class GraphTraversalTests {

    // Builds:  root -> c1, c2;  c1 -> gc1, gc2;  gc1 -> ggc1
    static NodeStore openTreeStore(out Article root, out Article c1, out Article c2, out Article gc1, out Article gc2, out Article ggc1) {
        var store = new NodeStore(DataStoreLocal.Open(Helper.GetDatamodel()));
        root = new Article { Id = 1, Name = "root" };
        c1 = new Article { Id = 2, Name = "c1" };
        c2 = new Article { Id = 3, Name = "c2" };
        gc1 = new Article { Id = 4, Name = "gc1" };
        gc2 = new Article { Id = 5, Name = "gc2" };
        ggc1 = new Article { Id = 6, Name = "ggc1" };
        var unrelated = new Article { Id = 7, Name = "unrelated" };
        store.Insert(root);
        store.Insert(c1);
        store.Insert(c2);
        store.Insert(gc1);
        store.Insert(gc2);
        store.Insert(ggc1);
        store.Insert(unrelated);
        store.AddRelation(c1, a => a.Parent, root);
        store.AddRelation(c2, a => a.Parent, root);
        store.AddRelation(gc1, a => a.Parent, c1);
        store.AddRelation(gc2, a => a.Parent, c1);
        store.AddRelation(ggc1, a => a.Parent, gc1);
        return store;
    }
    static string[] names(IEnumerable<Article> articles) => articles.Select(a => a.Name).OrderBy(n => n).ToArray();

    [TestMethod]
    public void TestTraverseLevels() {
        var store = openTreeStore(out var root, out _, out _, out _, out _, out _);

        { // one level down == direct children
            var r = store.Query<Article>().Where(a => a.Id == 1).Traverse(a => a.Children, maxLevel: 1).Execute();
            CollectionAssert.AreEqual(new[] { "c1", "c2" }, names(r));
        }
        { // two levels down, cumulative
            var r = store.Query<Article>().Where(a => a.Id == 1).Traverse(a => a.Children, maxLevel: 2).Execute();
            CollectionAssert.AreEqual(new[] { "c1", "c2", "gc1", "gc2" }, names(r));
        }
        { // window: exactly level 2
            var r = store.Query<Article>().Where(a => a.Id == 1).Traverse(a => a.Children, maxLevel: 2, minLevel: 2).Execute();
            CollectionAssert.AreEqual(new[] { "gc1", "gc2" }, names(r));
        }
        { // minLevel 0 includes the seed
            var r = store.Query<Article>().Where(a => a.Id == 1).Traverse(a => a.Children, maxLevel: 1, minLevel: 0).Execute();
            CollectionAssert.AreEqual(new[] { "c1", "c2", "root" }, names(r));
        }
        { // whole subtree
            var r = store.Query<Article>().Where(a => a.Id == 1).Traverse(a => a.Children, maxLevel: 10).Execute();
            CollectionAssert.AreEqual(new[] { "c1", "c2", "gc1", "gc2", "ggc1" }, names(r));
        }
        { // count never materializes nodes and matches
            var count = store.Query<Article>().Where(a => a.Id == 1).Traverse(a => a.Children, maxLevel: 10).Count();
            Assert.AreEqual(5, count);
        }
        { // traversal over the single (Parent) side: ancestors of ggc1
            var r = store.Query<Article>().Where(a => a.Id == 6).Traverse(a => a.Parent, maxLevel: 10).Execute();
            CollectionAssert.AreEqual(new[] { "c1", "gc1", "root" }, names(r));
        }
        { // Reverse over Children == Parent direction
            var r = store.Query<Article>().Where(a => a.Id == 6).Traverse(a => a.Children, maxLevel: 10, minLevel: 1, GraphDirection.Reverse).Execute();
            CollectionAssert.AreEqual(new[] { "c1", "gc1", "root" }, names(r));
        }
        { // Both direction from c1: parent + children in one hop
            var r = store.Query<Article>().Where(a => a.Id == 2).Traverse(a => a.Children, maxLevel: 1, minLevel: 1, GraphDirection.Both).Execute();
            CollectionAssert.AreEqual(new[] { "gc1", "gc2", "root" }, names(r));
        }
        { // empty seed set -> empty result
            var r = store.Query<Article>().Where(a => a.Id == 999).Traverse(a => a.Children, maxLevel: 3).Execute();
            Assert.AreEqual(0, r.Count);
        }
        { // multi seed: level is min distance over ALL seeds; c1 is a seed (level 0), so it is not reported at level 1
            var r = store.Query<Article>().Where(a => a.Id == 1 || a.Id == 2).Traverse(a => a.Children, maxLevel: 1).Execute();
            CollectionAssert.AreEqual(new[] { "c2", "gc1", "gc2" }, names(r));
        }
        store.Dispose();
    }

    [TestMethod]
    public void TestTraverseComposability() {
        var store = openTreeStore(out _, out _, out _, out _, out _, out _);
        { // Where after Traverse
            var r = store.Query<Article>().Where(a => a.Id == 1).Traverse(a => a.Children, maxLevel: 10).Where(a => a.Name == "gc1" || a.Name == "gc2").Execute();
            CollectionAssert.AreEqual(new[] { "gc1", "gc2" }, names(r));
        }
        { // OrderBy + Page after Traverse
            var r = store.Query<Article>().Where(a => a.Id == 1).Traverse(a => a.Children, maxLevel: 10).OrderBy(a => a.Name).Page(0, 2).Execute(out var totalCount);
            CollectionAssert.AreEqual(new[] { "c1", "c2" }, r.Select(a => a.Name).ToArray());
            Assert.AreEqual(5, totalCount);
        }
        { // Include after Traverse
            var r = store.Query<Article>().Where(a => a.Id == 1).Traverse(a => a.Children, maxLevel: 1).Include(a => a.Children).Execute().ToList();
            var c1 = r.Single(a => a.Name == "c1");
            CollectionAssert.AreEqual(new[] { "gc1", "gc2" }, names(c1.Children));
        }
        store.Dispose();
    }

    [TestMethod]
    public void TestTraverseCycleTermination() {
        var store = new NodeStore(DataStoreLocal.Open(Helper.GetDatamodel()));
        var a = new Article { Id = 1, Name = "a" };
        var b = new Article { Id = 2, Name = "b" };
        var c = new Article { Id = 3, Name = "c" };
        store.Insert(a);
        store.Insert(b);
        store.Insert(c);
        store.AddRelation(a, x => x.Parent, b); // a -> b -> c -> a  (parent cycle)
        store.AddRelation(b, x => x.Parent, c);
        store.AddRelation(c, x => x.Parent, a);
        var r = store.Query<Article>().Where(x => x.Id == 1).Traverse(x => x.Parent, maxLevel: 100).Execute();
        CollectionAssert.AreEqual(new[] { "b", "c" }, names(r)); // terminates; the seed itself stays at level 0 even when reached again
        var all = store.Query<Article>().Execute().ToDictionary(n => n!.Name, n => n!.PId);
        var path = store.Query<Article>().ShortestPath(x => x.Parent, all["a"], all["c"]).Execute();
        Assert.IsTrue(path.Found);
        Assert.AreEqual(2, path.Length); // a -> b -> c
        store.Dispose();
    }

    [TestMethod]
    public void TestTraverseChainingAndReTyping() {
        var store = new NodeStore(DataStoreLocal.Open(Helper.GetDatamodel()));
        var g1 = new Group { Groupname = "g1" };
        var g2 = new Group { Groupname = "g2" };
        var u1 = new User { Username = "u1" };
        var u2 = new User { Username = "u2" };
        var u3 = new User { Username = "u3" };
        var u4 = new User { Username = "u4" };
        store.Insert(g1);
        store.Insert(g2);
        store.Insert(u1);
        store.Insert(u2);
        store.Insert(u3);
        store.Insert(u4);
        store.AddRelation(u1, u => u.Group, g1);
        store.AddRelation(u2, u => u.Group, g1);
        store.AddRelation(u3, u => u.Group, g1);
        store.AddRelation(u4, u => u.Group, g2);
        // co-members of u1 via chaining, re-typed User -> Group -> User:
        var coMembers = store.Query<User>().Where(u => u.Username == "u1")
            .Traverse(u => u.Group, maxLevel: 1)
            .Traverse(g => g.Members, maxLevel: 1)
            .Execute();
        CollectionAssert.AreEqual(new[] { "u1", "u2", "u3" }, coMembers.Select(u => u.Username).OrderBy(n => n).ToArray());
        store.Dispose();
    }

    [TestMethod]
    public void TestTraverseCacheInvalidation() {
        var store = openTreeStore(out var root, out _, out _, out _, out _, out _);
        int countBefore = store.Query<Article>().Where(a => a.Id == 1).Traverse(a => a.Children, maxLevel: 10).Count();
        Assert.AreEqual(5, countBefore);
        var extra = new Article { Id = 8, Name = "extra" };
        store.Insert(extra);
        store.AddRelation(extra, a => a.Parent, root); // mutates the relation -> new GeneralStateId -> cache invalid
        int countAfter = store.Query<Article>().Where(a => a.Id == 1).Traverse(a => a.Children, maxLevel: 10).Count();
        Assert.AreEqual(6, countAfter);
        store.Dispose();
    }

    [TestMethod]
    public void TestTraverseBudget() {
        var store = openTreeStore(out _, out _, out _, out _, out _, out _);
        Assert.ThrowsException<Exception>(() => {
            store.Query<Article>().Where(a => a.Id == 1).Traverse(a => a.Children, maxLevel: 10, minLevel: 1, GraphDirection.Default, maxVisited: 2).Count();
        });
        store.Dispose();
    }

    [TestMethod]
    public void TestShortestPath() {
        var store = openTreeStore(out var root, out var c1, out var c2, out var gc1, out var gc2, out var ggc1);
        var all = store.Query<Article>().Execute().ToDictionary(a => a.Name, a => a.PId);

        { // root -> ggc1 over Children: root, c1, gc1, ggc1
            var path = store.Query<Article>().ShortestPath(a => a.Children, all["root"], all["ggc1"]).Execute();
            Assert.IsTrue(path.Found);
            Assert.AreEqual(3, path.Length);
            CollectionAssert.AreEqual(new[] { "root", "c1", "gc1", "ggc1" }, path.Nodes.Select(n => n.Name).ToArray());
            CollectionAssert.AreEqual(new[] { all["root"], all["c1"], all["gc1"], all["ggc1"] }, path.NodeIds);
        }
        { // reverse: ggc1 -> root over Children reversed
            var path = store.Query<Article>().ShortestPath(a => a.Children, all["ggc1"], all["root"], direction: GraphDirection.Reverse).Execute();
            Assert.IsTrue(path.Found);
            CollectionAssert.AreEqual(new[] { "ggc1", "gc1", "c1", "root" }, path.Nodes.Select(n => n.Name).ToArray());
        }
        { // no path in the followed direction (sibling to sibling downwards)
            var path = store.Query<Article>().ShortestPath(a => a.Children, all["c1"], all["c2"]).Execute();
            Assert.IsFalse(path.Found);
            Assert.AreEqual(0, path.NodeIds.Length);
        }
        { // sibling to sibling with Both: c1 -> root -> c2
            var path = store.Query<Article>().ShortestPath(a => a.Children, all["c1"], all["c2"], direction: GraphDirection.Both).Execute();
            Assert.IsTrue(path.Found);
            Assert.AreEqual(2, path.Length);
            CollectionAssert.AreEqual(new[] { "c1", "root", "c2" }, path.Nodes.Select(n => n.Name).ToArray());
        }
        { // blocked by maxLevel
            var path = store.Query<Article>().ShortestPath(a => a.Children, all["root"], all["ggc1"], maxLevel: 2).Execute();
            Assert.IsFalse(path.Found);
        }
        { // from == to
            var path = store.Query<Article>().ShortestPath(a => a.Children, all["root"], all["root"]).Execute();
            Assert.IsTrue(path.Found);
            Assert.AreEqual(0, path.Length);
            CollectionAssert.AreEqual(new[] { all["root"] }, path.NodeIds);
        }
        { // unknown ids -> not found, no throw
            var path = store.Query<Article>().ShortestPath(a => a.Children, Guid.NewGuid(), all["root"]).Execute();
            Assert.IsFalse(path.Found);
        }
        { // store level convenience wrapper
            var path = store.ShortestPath<Article, IEnumerable<Article>>(all["root"], a => a.Children, all["gc2"]);
            Assert.IsTrue(path.Found);
            Assert.AreEqual(2, path.Length);
        }
        store.Dispose();
    }

    [TestMethod]
    public void TestShortestPathDeterminism() {
        // diamond: a -> b1 -> c, a -> b2 -> c : two equally short paths, result must be stable
        var store = new NodeStore(DataStoreLocal.Open(Helper.GetDatamodel()));
        var a = new Article { Id = 1, Name = "a" };
        var b1 = new Article { Id = 2, Name = "b1" };
        var b2 = new Article { Id = 3, Name = "b2" };
        var c = new Article { Id = 4, Name = "c" };
        store.Insert(a);
        store.Insert(b1);
        store.Insert(b2);
        store.Insert(c);
        store.AddRelation(b1, x => x.Parent, a);
        store.AddRelation(b2, x => x.Parent, a);
        store.AddRelation(c, x => x.Parent, b1);
        var cId = store.Query<Article>().Where(x => x.Id == 4).Execute().First().PId;
        var aId = store.Query<Article>().Where(x => x.Id == 1).Execute().First().PId;
        var first = store.Query<Article>().ShortestPath(x => x.Parent, cId, aId).Execute();
        var second = store.Query<Article>().ShortestPath(x => x.Parent, cId, aId).Execute();
        Assert.IsTrue(first.Found);
        Assert.AreEqual(2, first.Length);
        CollectionAssert.AreEqual(first.NodeIds, second.NodeIds);
        store.Dispose();
    }

    [TestMethod]
    public void TestTraverseRawQueryText() {
        var store = openTreeStore(out _, out _, out _, out _, out _, out _);
        var childrenPropId = store.Mapper.GetProperty<Article, IEnumerable<Article>>(a => a.Children).Id;
        var rootGuid = store.Query<Article>().Where(a => a.Id == 1).Execute().First().PId;
        { // count over raw query text (the wire format used by HTTP clients and the admin UI)
            var query = $"Article.WhereInIds([\"{rootGuid}\"]).Traverse(\"{childrenPropId}\", 1, 2).Count()";
            var result = store.Datastore.Query(query, []);
            Assert.AreEqual(4, (int)result!);
        }
        { // shortest path over raw query text
            var ggc1Guid = store.Query<Article>().Where(a => a.Id == 6).Execute().First().PId;
            var query = $"Article.ShortestPath(\"{childrenPropId}\", \"{rootGuid}\", \"{ggc1Guid}\", 10)";
            var result = store.Datastore.Query(query, []);
            Assert.IsInstanceOfType(result, typeof(Relatude.DB.Query.Data.IGraphPathResultData));
            var path = (Relatude.DB.Query.Data.IGraphPathResultData)result!;
            Assert.IsTrue(path.Found);
            Assert.AreEqual(3, path.Length);
            Assert.AreEqual(4, path.NodeIds.Count);
        }
        store.Dispose();
    }

    [TestMethod]
    public void TestTraverseJsonEvaluation() {
        var store = openTreeStore(out _, out _, out _, out _, out _, out _);
        { // traverse result through the JSON evaluation path (server endpoint shape)
            var json = store.Query<Article>().Where(a => a.Id == 1).Traverse(a => a.Children, maxLevel: 2).EvaluateForJson();
            Assert.IsNotNull(json);
        }
        { // shortest path through the JSON evaluation path
            var all = store.Query<Article>().Execute().ToDictionary(a => a.Name, a => a.PId);
            var json = store.Query<Article>().ShortestPath(a => a.Children, all["root"], all["gc1"]).EvaluateForJson();
            Assert.IsInstanceOfType(json, typeof(GraphPathResult<object?>));
            var path = (GraphPathResult<object?>)json!;
            Assert.IsTrue(path.Found);
            Assert.AreEqual(2, path.Length);
        }
        store.Dispose();
    }
}
