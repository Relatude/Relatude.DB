using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;

namespace Relatude.Indexes;

/// <summary>
/// The id keyed collections choose between a dense (array or bit set) and a sparse (dictionary or
/// hash set) representation by the density of the ids, and the choice must go both ways: a map that
/// looked sparse early becomes dense when the ids fill in, and a dense one goes back when an id lands
/// far outside its window, so neither ever costs more than the sparse structure.
/// </summary>
[TestClass]
public class DenseCollectionTests {

    [TestMethod]
    public void ValueByIdMap_UpgradesOnceTheIdsFillIn() {
        var map = new ValueByIdMap<int>();
        for (var i = 1; i <= 10_000; i++) map.Set(i * 100, i); // 10k entries over a million ids: sparse at the first check
        Assert.IsFalse(map.IsDense);
        for (var id = 1; id <= 1_000_000; id++) map.Set(id, -id); // fills the range in, dense long before the end
        Assert.IsTrue(map.IsDense);
        Assert.AreEqual(1_000_000, map.Count);
        Assert.AreEqual(-77, map[77]);
        Assert.AreEqual(-100, map[100]); // overwritten by the fill
        Assert.IsFalse(map.Contains(1_000_001));
    }

    [TestMethod]
    public void ValueByIdMap_DowngradesForAFarId_AndKeepsEveryEntry() {
        var map = new ValueByIdMap<Guid>(20_000); // presized: dense from the start
        Assert.IsTrue(map.IsDense);
        var guids = new Guid[20_001];
        for (var id = 1; id <= 20_000; id++) map.Set(id, guids[id] = Guid.NewGuid());
        var far = Guid.NewGuid();
        map.Set(50_000_000, far); // an array to here would cost 800 MB
        Assert.IsFalse(map.IsDense);
        Assert.AreEqual(20_001, map.Count);
        Assert.AreEqual(far, map[50_000_000]);
        for (var id = 1; id <= 20_000; id++) Assert.AreEqual(guids[id], map[id]);
        Assert.AreEqual(20_001, map.Count());
        map.Remove(50_000_000);
        Assert.IsFalse(map.Contains(50_000_000));
        Assert.AreEqual(20_000, map.Count);
    }

    [TestMethod]
    public void ValueByIdMap_ValueSizeMovesTheDensityBound() {
        // 16 byte values: an array is only worth it up to about 2.75 slots per entry, 4 byte values up to 8
        var guids = new ValueByIdMap<Guid>();
        var ints = new ValueByIdMap<int>();
        for (var i = 1; i <= 20_000; i++) {
            guids.Set(i * 5, Guid.Empty);
            ints.Set(i * 5, i);
        }
        Assert.IsFalse(guids.IsDense);
        Assert.IsTrue(ints.IsDense);
    }

    [TestMethod]
    public void MutableSet_BecomesABitSetOnceDense_AndLeavesItForAFarId() {
        var set = new MutableSet();
        for (var i = 1; i <= 10_000; i++) set.Add(i * 1000); // 10k ids over ten million: too sparse for a bit set
        Assert.IsFalse(set.TryGetBits(out _));
        for (var id = 1; id <= 400_000; id++) set.Add(id); // fills in: dense at a later doubling
        Assert.IsTrue(set.TryGetBits(out _));
        Assert.AreEqual(400_000 + 10_000 - 400, set.Count); // the multiples of 1000 up to 400k were already there
        Assert.IsTrue(set.Contains(9_999_000));
        Assert.IsTrue(set.Contains(123_456));

        set.Add(int.MaxValue - 1); // a window to here is 256 MB: back to a hash set
        Assert.IsFalse(set.TryGetBits(out _));
        Assert.AreEqual(400_000 + 10_000 - 400 + 1, set.Count);
        Assert.IsTrue(set.Contains(int.MaxValue - 1));
        Assert.IsTrue(set.Contains(9_999_000));
        Assert.IsTrue(set.Contains(123_456));
        Assert.IsFalse(set.Contains(400_001));
        var frozen = set.AsUnmutableIdSet();
        Assert.AreEqual(set.Count, frozen.Count);
    }

    [TestMethod]
    public void DenseBitSet_WorthGrowingTo() {
        var bits = DenseBitSet.From(Enumerable.Range(0, 20_000), 0, 19_999);
        Assert.IsTrue(bits.WorthGrowingTo(5)); // inside
        Assert.IsTrue(bits.WorthGrowingTo(100_000)); // 5 words per member is still cheap
        Assert.IsFalse(bits.WorthGrowingTo(20_000 * 64 + 1_000_000)); // past 64 bits per member
        Assert.IsFalse(bits.WorthGrowingTo(-1));
    }
}
