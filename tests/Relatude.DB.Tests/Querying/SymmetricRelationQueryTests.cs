using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.Query;

namespace Relatude.Querying;

#region test model with explicit symmetric relations (the shared model only has directional ones)
[Node]
public class SymPerson {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(Indexed = true)]
    public string Name { get; set; } = "";
    public SymMarriage.Spouse Spouse { get; set; } = new();
    public SymFriendship.Friends Friends { get; set; } = new();
}
public class SymMarriage : OneOne<SymPerson> {
    public class Spouse : One { }
}
public class SymFriendship : ManyMany<SymPerson> {
    public class Friends : Many { }
}
#endregion

// End-to-end verification that OneOne and ManyMany relations behave the same through the full
// query pipeline after the symmetric index optimizations (hash, Contains and Get changes).
[TestClass]
public class SymmetricRelationQueryTests {

    static Datamodel getModel() {
        var dm = new Datamodel();
        dm.Add<SymPerson>();
        dm.Add<SymMarriage>();
        dm.Add<SymFriendship>();
        return dm;
    }
    static SymPerson person(string name) => new() { Id = Guid.NewGuid(), Name = name };

    [TestMethod]
    public void TestOneOneThroughStore() {
        var store = new NodeStore(DataStoreLocal.Open(getModel()));
        var alice = person("alice");
        var bob = person("bob");
        var carol = person("carol");
        store.Insert(alice);
        store.Insert(bob);
        store.Insert(carol);
        store.AddRelation(alice, p => p.Spouse, bob);

        { // WhereRelates works in both directions on a symmetric relation
            var spouseOfBob = store.Query<SymPerson>().WhereRelates(p => p.Spouse, bob.Id).Execute().Single();
            Assert.AreEqual("alice", spouseOfBob!.Name);
            var spouseOfAlice = store.Query<SymPerson>().WhereRelates(p => p.Spouse, alice.Id).Execute().Single();
            Assert.AreEqual("bob", spouseOfAlice!.Name);
            var spouseOfCarol = store.Query<SymPerson>().WhereRelates(p => p.Spouse, carol.Id).Execute().ToList();
            Assert.AreEqual(0, spouseOfCarol.Count);
        }
        { // traversal over a OneOne relation (single value Get path)
            var reached = store.Query<SymPerson>().Where(p => p.Name == "alice")
                .Traverse<SymPerson>(p => p.Spouse, maxLevel: 3).Execute();
            CollectionAssert.AreEqual(new[] { "bob" }, reached.Select(p => p!.Name).ToArray());
        }
        store.Dispose();
    }

    [TestMethod]
    public void TestManyManyTraverseAndShortestPath() {
        var store = new NodeStore(DataStoreLocal.Open(getModel()));
        var a = person("a");
        var b = person("b");
        var c = person("c");
        var d = person("d");
        var e = person("e"); // unconnected
        store.Insert(a);
        store.Insert(b);
        store.Insert(c);
        store.Insert(d);
        store.Insert(e);
        store.AddRelation(a, p => p.Friends, b); // chain: a - b - c - d
        store.AddRelation(b, p => p.Friends, c);
        store.AddRelation(c, p => p.Friends, d);

        { // WhereRelates, either endpoint
            var friendsOfB = store.Query<SymPerson>().WhereRelates(p => p.Friends, b.Id).Execute();
            CollectionAssert.AreEqual(new[] { "a", "c" }, friendsOfB.Select(p => p!.Name).OrderBy(n => n).ToArray());
        }
        { // traversal levels over the undirected chain
            var level1 = store.Query<SymPerson>().Where(p => p.Name == "a").Traverse<SymPerson>(p => p.Friends, maxLevel: 1).Execute();
            CollectionAssert.AreEqual(new[] { "b" }, level1.Select(p => p!.Name).ToArray());
            var level2 = store.Query<SymPerson>().Where(p => p.Name == "a").Traverse<SymPerson>(p => p.Friends, maxLevel: 2).Execute();
            CollectionAssert.AreEqual(new[] { "b", "c" }, level2.Select(p => p!.Name).OrderBy(n => n).ToArray());
            var all = store.Query<SymPerson>().Where(p => p.Name == "a").Traverse<SymPerson>(p => p.Friends, maxLevel: 10).Execute();
            CollectionAssert.AreEqual(new[] { "b", "c", "d" }, all.Select(p => p!.Name).OrderBy(n => n).ToArray());
        }
        { // direction is irrelevant on a symmetric relation: all three settings agree
            int n0 = store.Query<SymPerson>().Where(p => p.Name == "b").Traverse<SymPerson>(p => p.Friends, 10, 1, GraphDirection.Default).Count();
            int n1 = store.Query<SymPerson>().Where(p => p.Name == "b").Traverse<SymPerson>(p => p.Friends, 10, 1, GraphDirection.Reverse).Count();
            int n2 = store.Query<SymPerson>().Where(p => p.Name == "b").Traverse<SymPerson>(p => p.Friends, 10, 1, GraphDirection.Both).Count();
            Assert.AreEqual(3, n0);
            Assert.AreEqual(n0, n1);
            Assert.AreEqual(n0, n2);
        }
        { // shortest path across the chain, both orders
            var path = store.Query<SymPerson>().ShortestPath(p => p.Friends, a.Id, d.Id).Execute();
            Assert.IsTrue(path.Found);
            Assert.AreEqual(3, path.Length);
            CollectionAssert.AreEqual(new[] { "a", "b", "c", "d" }, path.Nodes.Select(p => p.Name).ToArray());
            var reversed = store.Query<SymPerson>().ShortestPath(p => p.Friends, d.Id, a.Id).Execute();
            CollectionAssert.AreEqual(new[] { "d", "c", "b", "a" }, reversed.Nodes.Select(p => p.Name).ToArray());
        }
        { // no path to the unconnected node
            var path = store.Query<SymPerson>().ShortestPath(p => p.Friends, a.Id, e.Id).Execute();
            Assert.IsFalse(path.Found);
        }
        { // a shortcut edge changes the shortest path (also exercises cache invalidation on writes)
            store.AddRelation(a, p => p.Friends, d);
            var path = store.Query<SymPerson>().ShortestPath(p => p.Friends, a.Id, d.Id).Execute();
            Assert.AreEqual(1, path.Length);
            var all = store.Query<SymPerson>().Where(p => p.Name == "a").Traverse<SymPerson>(p => p.Friends, maxLevel: 1).Execute();
            CollectionAssert.AreEqual(new[] { "b", "d" }, all.Select(p => p!.Name).OrderBy(n => n).ToArray());
        }
        store.Dispose();
    }

    [TestMethod]
    public void TestSymmetricPersistenceRoundTrip() {
        // covers SaveState/ReadState of OneOneIndex and ManyManyIndex after the hash change
        // (the pair dictionary enumeration order, and thereby the state file layout, changed)
        var io = new IOProviderMemory();
        var settings = new SettingsLocal();
        Guid aliceId, bobId, aId, bId, cId;
        {
            var store = new NodeStore(DataStoreLocal.Open(getModel(), settings, io));
            var alice = person("alice");
            var bob = person("bob");
            var a = person("a");
            var b = person("b");
            var c = person("c");
            aliceId = alice.Id; bobId = bob.Id; aId = a.Id; bId = b.Id; cId = c.Id;
            store.Insert(alice);
            store.Insert(bob);
            store.Insert(a);
            store.Insert(b);
            store.Insert(c);
            store.AddRelation(alice, p => p.Spouse, bob);
            store.AddRelation(a, p => p.Friends, b);
            store.AddRelation(b, p => p.Friends, c);
            store.Dispose(); // flushes state
        }
        {
            var store = new NodeStore(DataStoreLocal.Open(getModel(), settings, io)); // reopen from the same storage
            // OneOne survived, both directions:
            Assert.AreEqual("alice", store.Query<SymPerson>().WhereRelates(p => p.Spouse, bobId).Execute().Single()!.Name);
            Assert.AreEqual("bob", store.Query<SymPerson>().WhereRelates(p => p.Spouse, aliceId).Execute().Single()!.Name);
            // ManyMany survived:
            var friendsOfB = store.Query<SymPerson>().WhereRelates(p => p.Friends, bId).Execute();
            CollectionAssert.AreEqual(new[] { "a", "c" }, friendsOfB.Select(p => p!.Name).OrderBy(n => n).ToArray());
            var path = store.Query<SymPerson>().ShortestPath(p => p.Friends, aId, cId).Execute();
            Assert.IsTrue(path.Found);
            Assert.AreEqual(2, path.Length);
            // and the reloaded indexes accept further mutations:
            var dave = person("dave");
            store.Insert(dave);
            var bNode = store.Query<SymPerson>().Where(p => p.Name == "b").Execute().Single()!;
            store.AddRelation(dave, p => p.Friends, bNode);
            var friendsOfB2 = store.Query<SymPerson>().WhereRelates(p => p.Friends, bId).Execute();
            CollectionAssert.AreEqual(new[] { "a", "c", "dave" }, friendsOfB2.Select(p => p!.Name).OrderBy(n => n).ToArray());
            store.Dispose();
        }
    }
}
