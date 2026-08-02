using Relatude.DB.DataStores.Relations;

namespace Relatude.Indexes;

// Guards the symmetric index optimizations: the RelationKeyComparer hash change,
// the OneOne Contains rewrite (adjacency probe instead of pair dictionary), and the
// switch to IdSet.SingleIdSet in the single value Get paths (OneOne, OneToOne, OneToMany).
[TestClass]
public class SymmetricIndexTests {

    [TestMethod]
    public void TestManyManyCollidingSumKeys() {
        // every pair (i, 200 - i) has the same source + target sum; with the old additive hash
        // they all shared one bucket. Correctness must hold under heavy (former) collisions.
        var r = new ManyManyIndex();
        for (int i = 1; i <= 99; i++) r.Add(i, 200 - i, DateTime.UtcNow);
        Assert.AreEqual(99, r.TotalCount);
        for (int i = 1; i <= 99; i++) {
            Assert.IsTrue(r.Contains(i, 200 - i));
            Assert.IsTrue(r.Contains(200 - i, i)); // symmetric: either order
        }
        Assert.IsFalse(r.Contains(1, 2));
        Assert.IsFalse(r.Contains(100, 100)); // sum is also 200, but never added
        // remove every second edge (in reversed argument order) and verify the rest is intact:
        for (int i = 1; i <= 99; i += 2) r.Remove(200 - i, i);
        for (int i = 1; i <= 99; i++) {
            if (i % 2 == 1) Assert.IsFalse(r.Contains(i, 200 - i));
            else Assert.IsTrue(r.Contains(i, 200 - i));
        }
        Assert.AreEqual(49, r.TotalCount);
        // re-add the removed edges:
        for (int i = 1; i <= 99; i += 2) r.Add(i, 200 - i, DateTime.UtcNow);
        Assert.AreEqual(99, r.TotalCount);
        for (int i = 1; i <= 99; i++) Assert.IsTrue(r.Contains(200 - i, i));
    }

    [TestMethod]
    public void TestManyManyGetIgnoresDirection() {
        var r = new ManyManyIndex();
        r.Add(1, 2, DateTime.UtcNow);
        r.Add(3, 1, DateTime.UtcNow); // 1 participates as source and as target
        CollectionAssert.AreEquivalent(new[] { 2, 3 }, r.Get(1, false).Enumerate().ToArray());
        CollectionAssert.AreEquivalent(new[] { 2, 3 }, r.Get(1, true).Enumerate().ToArray());
        CollectionAssert.AreEqual(new[] { 1 }, r.Get(2, false).Enumerate().ToArray());
        CollectionAssert.AreEqual(new[] { 1 }, r.Get(3, true).Enumerate().ToArray());
        Assert.AreEqual(2, r.CountRelated(1, false));
        Assert.AreEqual(2, r.CountRelated(1, true));
        Assert.AreEqual(0, r.Get(9, false).Count);
    }

    [TestMethod]
    public void TestOneOneGetAndContains() {
        var r = new OneOneIndex();
        r.Add(1, 2, DateTime.UtcNow);
        // single element sets in both directions; the direction flag is ignored:
        CollectionAssert.AreEqual(new[] { 2 }, r.Get(1, false).Enumerate().ToArray());
        CollectionAssert.AreEqual(new[] { 2 }, r.Get(1, true).Enumerate().ToArray());
        CollectionAssert.AreEqual(new[] { 1 }, r.Get(2, false).Enumerate().ToArray());
        Assert.AreEqual(0, r.Get(3, false).Count);
        // stable set identity: repeated probes must yield the same StateId, or downstream
        // cached set operations (intersections etc.) could never hit their cache:
        Assert.AreEqual(r.Get(1, false).StateId, r.Get(1, false).StateId);
        var other = new OneOneIndex();
        other.Add(5, 2, DateTime.UtcNow);
        Assert.AreEqual(r.Get(1, false).StateId, other.Get(5, false).StateId); // deterministic per related id
        // contains, both orders:
        Assert.IsTrue(r.Contains(1, 2));
        Assert.IsTrue(r.Contains(2, 1));
        Assert.IsFalse(r.Contains(1, 3));
        Assert.IsFalse(r.Contains(3, 1));
        Assert.IsFalse(r.Contains(1, 1));
        Assert.AreEqual(1, r.CountRelated(1, false));
        Assert.AreEqual(0, r.CountRelated(3, true));
        Assert.AreEqual(1, r.TotalCount);
        r.Remove(1, 2);
        Assert.IsFalse(r.Contains(1, 2));
        Assert.IsFalse(r.Contains(2, 1));
        Assert.AreEqual(0, r.Get(1, false).Count);
        Assert.AreEqual(0, r.TotalCount);
        // self loop:
        r.Add(7, 7, DateTime.UtcNow);
        Assert.IsTrue(r.Contains(7, 7));
        CollectionAssert.AreEqual(new[] { 7 }, r.Get(7, true).Enumerate().ToArray());
        r.Remove(7, 7);
        Assert.IsFalse(r.Contains(7, 7));
        // removed pair can be re-added:
        r.Add(1, 2, DateTime.UtcNow);
        Assert.IsTrue(r.Contains(2, 1));
    }

    [TestMethod]
    public void TestOneToManyOneSideGet() {
        var r = new OneToManyIndex();
        r.Add(1, 2, DateTime.UtcNow); // parent 1 -> children 2 and 3
        r.Add(1, 3, DateTime.UtcNow);
        CollectionAssert.AreEqual(new[] { 1 }, r.Get(2, true).Enumerate().ToArray()); // one side
        CollectionAssert.AreEqual(new[] { 1 }, r.Get(3, true).Enumerate().ToArray());
        CollectionAssert.AreEqual(new[] { 2, 3 }, r.Get(1, false).Enumerate().ToArray()); // many side
        Assert.AreEqual(r.Get(2, true).StateId, r.Get(3, true).StateId); // same parent -> same set identity
        Assert.AreEqual(0, r.Get(9, true).Count);
        r.Remove(1, 2);
        Assert.AreEqual(0, r.Get(2, true).Count);
        CollectionAssert.AreEqual(new[] { 3 }, r.Get(1, false).Enumerate().ToArray());
    }

    [TestMethod]
    public void TestOneToOneGetBothSides() {
        var r = new OneToOneIndex();
        r.Add(1, 2, DateTime.UtcNow);
        CollectionAssert.AreEqual(new[] { 2 }, r.Get(1, false).Enumerate().ToArray()); // target of 1
        CollectionAssert.AreEqual(new[] { 1 }, r.Get(2, true).Enumerate().ToArray());  // source of 2
        Assert.AreEqual(0, r.Get(2, false).Count); // 2 is not a source
        Assert.AreEqual(0, r.Get(1, true).Count);  // 1 is not a target
        Assert.AreEqual(r.Get(1, false).StateId, r.Get(1, false).StateId);
        r.Remove(1, 2);
        Assert.AreEqual(0, r.Get(1, false).Count);
    }
}
