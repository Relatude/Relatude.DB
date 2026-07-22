using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;

namespace Tests;

[TestClass]
public class DenseBitSetTests {

    [TestMethod]
    public void AddRemoveContainsEnumerate_MatchesReference() {
        var rnd = new Random(7);
        var bits = new DenseBitSet(0, 100);
        var reference = new HashSet<int>();
        for (var i = 0; i < 20_000; i++) {
            var id = rnd.Next(500_000); // forces several grow() calls
            if (rnd.Next(3) == 0) {
                Assert.AreEqual(reference.Remove(id), bits.Remove(id));
            } else {
                var added = reference.Add(id);
                bits.Add(id);
                if (!added) Assert.AreEqual(reference.Count, bits.Count, "double add must not change count");
            }
            Assert.AreEqual(reference.Count, bits.Count);
        }
        foreach (var id in reference) Assert.IsTrue(bits.Contains(id));
        CollectionAssert.AreEqual(reference.OrderBy(i => i).ToList(), bits.ToList(), "enumeration must be ascending and complete");
    }

    [TestMethod]
    public void WordOps_MatchReferenceSets() {
        var rnd = new Random(42);
        for (var run = 0; run < 20; run++) {
            var a = new HashSet<int>();
            var b = new HashSet<int>();
            var baseA = rnd.Next(100_000); // different windows/bases each run
            var baseB = rnd.Next(100_000);
            for (var i = 0; i < 5000; i++) { a.Add(baseA + rnd.Next(50_000)); b.Add(baseB + rnd.Next(50_000)); }
            var ba = DenseBitSet.From(a, a.Min(), a.Max());
            var bb = DenseBitSet.From(b, b.Min(), b.Max());
            Assert.AreEqual(a.Intersect(b).Count(), DenseBitSet.AndCount(ba, bb));
            CollectionAssert.AreEqual(a.Intersect(b).OrderBy(i => i).ToList(), DenseBitSet.And(ba, bb).ToList());
            CollectionAssert.AreEqual(a.Union(b).OrderBy(i => i).ToList(), DenseBitSet.Or(ba, bb).ToList());
            CollectionAssert.AreEqual(a.Except(b).OrderBy(i => i).ToList(), DenseBitSet.AndNot(ba, bb).ToList());
        }
    }

    [TestMethod]
    public void MutableSet_UpgradesToBitsAndCountsCorrectly() {
        var set = new MutableSet(1, 2);
        var reference = new HashSet<int> { 1, 2 };
        var rnd = new Random(3);
        for (var i = 3; i < 30_000; i++) { // crosses array -> list -> hashset -> bitset thresholds
            var id = rnd.Next(60_000);
            if (reference.Add(id)) set.Add(id);
        }
        Assert.IsTrue(set.TryGetBits(out _), "a dense set this size should have upgraded to bits");
        Assert.AreEqual(reference.Count, set.Count);
        var probe = new IdSet(Enumerable.Range(0, 60_000).Where(i => i % 3 == 0).ToHashSet(), 1);
        Assert.AreEqual(reference.Count(id => id % 3 == 0), set.CountIntersection(probe));
        var some = reference.Take(500).ToList();
        foreach (var id in some) set.Remove(id);
        foreach (var id in some) reference.Remove(id);
        Assert.AreEqual(reference.Count, set.Count);
        CollectionAssert.AreEquivalent(reference.ToList(), set.ToList());
        var snapshot = set.AsUnmutableIdSet();
        Assert.AreEqual(reference.Count, snapshot.Count);
        set.Add(59_999_9); // mutating after snapshot must not affect the snapshot
        Assert.AreEqual(reference.Count, snapshot.Count);
    }

    [TestMethod]
    public void IdSet_BitsRepresentation_PreservesSemantics() {
        var ids = Enumerable.Range(1000, 50_000).Where(i => i % 2 == 0).ToHashSet();
        var set = new IdSet(ids, 1);
        Assert.IsNotNull(set.GetType()); // representation is internal; verify behavior:
        Assert.AreEqual(ids.Count, set.Count);
        Assert.IsTrue(set.Has(1002));
        Assert.IsFalse(set.Has(1001));
        CollectionAssert.AreEqual(ids.OrderBy(i => i).ToList(), set.Enumerate().ToList());
        // ordered inputs (arrays/lists) must keep their order:
        var ordered = new List<int> { 5, 3, 9, 1 };
        var orderedSet = new IdSet(ordered, 2);
        CollectionAssert.AreEqual(ordered, orderedSet.Enumerate().ToList());
        // intersection counts agree regardless of representation mix:
        var sparse = new IdSet(new List<int> { 1002, 1004, 999_999 }, 3);
        Assert.AreEqual(2, IdSet.IntersectionCount(set, sparse));
    }

    [TestMethod]
    public void ValueByIdMap_UpgradesToDenseArray() {
        var map = new ValueByIdMap<double>();
        var reference = new Dictionary<int, double>();
        var rnd = new Random(11);
        for (var i = 0; i < 25_000; i++) { // crosses the dictionary -> dense array threshold
            var id = rnd.Next(40_000);
            if (reference.TryAdd(id, id * 1.5)) map.Add(id, id * 1.5);
        }
        Assert.AreEqual(reference.Count, map.Count);
        foreach (var (id, v) in reference) {
            Assert.IsTrue(map.TryGetValue(id, out var actual));
            Assert.AreEqual(v, actual);
        }
        Assert.IsFalse(map.TryGetValue(40_001, out _));
        var removed = reference.Keys.Take(1000).ToList();
        foreach (var id in removed) { map.Remove(id); reference.Remove(id); }
        Assert.AreEqual(reference.Count, map.Count);
        foreach (var id in removed) Assert.IsFalse(map.TryGetValue(id, out _));
        CollectionAssert.AreEquivalent(reference.Keys.ToList(), map.Keys.ToList());
        Assert.AreEqual(reference.Count, map.Count());
    }
}
