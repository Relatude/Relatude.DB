using Relatude.DB.Datastores.Indexes.BTreeIndex;

namespace Relatude.Indexes;

/// <summary>
/// ulong and Guid as index id types in the B+Tree engine: round-trip through disk and reopen,
/// ordering (unsigned numeric for ulong, Guid.CompareTo for Guid — including ids above
/// long.MaxValue that would break a signed encoding), range scans and counts over the composite
/// (value, id) keys, the value cache, and id-kind mismatch detection on open/reopen.
/// </summary>
[TestClass]
public class KvUlongGuidIndexTests {

    static void commit(IStorageEngine engine, Action mutate, long timestamp = 1) {
        engine.BeginTransaction();
        mutate();
        engine.CommitTransaction(timestamp, true);
    }

    static string tempDir() {
        var dir = Path.Combine(Path.GetTempPath(), "RelatudeDB_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ids deliberately unsorted, spanning both sides of long.MaxValue
    static readonly (ulong Id, string Value)[] _ulongEntries = [
        (ulong.MaxValue, "top"),
        (0ul, "zero"),
        (long.MaxValue + 1ul, "past-signed"),
        (42ul, "mid"),
        ((ulong)int.MaxValue, "int-max"),
    ];

    [TestMethod]
    public void BPlusTree_UlongIds_RoundTripThroughDiskAndReopen() {
        var dir = tempDir();
        try {
            var filePath = Path.Combine(dir, "ulong.db");
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                var index = engine.OpenOrCreateSortedUlongIndex<string>("by-ulong");
                commit(engine, () => { foreach (var (id, value) in _ulongEntries) index.Set(id, value); });
                verifyUlongEntries(index);
            }
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                verifyUlongEntries(engine.OpenOrCreateSortedUlongIndex<string>("by-ulong")); // decode path: read back from disk
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    static void verifyUlongEntries(ISortedUlongIndex<string> index) {
        Assert.AreEqual(_ulongEntries.Length, index.Count);
        foreach (var (id, value) in _ulongEntries) {
            Assert.AreEqual(value, index.GetValue(id));
            Assert.IsTrue(index.ContainsKey(id));
            CollectionAssert.AreEqual(new[] { id }, index.GetIds(value).ToArray());
        }
        Assert.IsFalse(index.ContainsKey(7ul));
        // Keys and Entries must come back in ascending UNSIGNED id order
        var expectedIds = _ulongEntries.Select(e => e.Id).OrderBy(id => id).ToArray();
        CollectionAssert.AreEqual(expectedIds, index.Keys.ToArray());
        CollectionAssert.AreEqual(expectedIds, index.Entries.Select(e => e.Key).ToArray());
        foreach (var entry in index.Entries) Assert.AreEqual(_ulongEntries.Single(e => e.Id == entry.Key).Value, entry.Value);
    }

    [TestMethod]
    public void BPlusTree_GuidIds_RoundTripThroughDiskAndReopen() {
        var guids = new[] {
            Guid.Empty,
            new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff"),
            new Guid("00000000-0000-0000-0000-000000000001"),
            new Guid("80000000-0000-0000-0000-000000000000"), // high bit set: catches signed comparison of the first component
            Guid.NewGuid(),
        };
        var dir = tempDir();
        try {
            var filePath = Path.Combine(dir, "guid.db");
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                var index = engine.OpenOrCreateSortedGuidIndex<int>("by-guid");
                commit(engine, () => { for (var i = 0; i < guids.Length; i++) index.Set(guids[i], i * 10); });
                verifyGuidEntries(engine.OpenOrCreateSortedGuidIndex<int>("by-guid"), guids);
            }
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                verifyGuidEntries(engine.OpenOrCreateSortedGuidIndex<int>("by-guid"), guids);
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    static void verifyGuidEntries(ISortedGuidIndex<int> index, Guid[] guids) {
        Assert.AreEqual(guids.Length, index.Count);
        for (var i = 0; i < guids.Length; i++) {
            Assert.AreEqual(i * 10, index.GetValue(guids[i]));
            CollectionAssert.AreEqual(new[] { guids[i] }, index.GetIds(i * 10).ToArray());
        }
        // Keys must come back in Guid.CompareTo order (the encoding is RFC 4122 big-endian bytes)
        CollectionAssert.AreEqual(guids.OrderBy(g => g).ToArray(), index.Keys.ToArray());
    }

    [TestMethod]
    public void BPlusTree_UlongIds_RangeScansAndCounts() {
        using var engine = new BPlusTreeStorageEngine(null); // memory-only
        var index = engine.OpenOrCreateSortedUlongIndex<int>("ranges");
        // two ids per value so (value, id) ordering inside a value is exercised, with
        // id order deliberately opposite to value order
        commit(engine, () => {
            foreach (var value in new[] { 10, 20, 30 }) {
                index.Set((ulong)(100 - value) * 2 + 1, value);
                index.Set((ulong)(100 - value) * 2, value);
            }
        });

        Assert.AreEqual(10, index.GetMinValue());
        Assert.AreEqual(30, index.GetMaxValue());
        Assert.AreEqual(3, index.DistinctValueCount);
        CollectionAssert.AreEqual(new[] { 10, 20, 30 }, index.DistinctValues.ToArray());

        // ascending (value, id): 10 -> ids 180,181; 20 -> 160,161; 30 -> 140,141
        CollectionAssert.AreEqual(new ulong[] { 180, 181, 160, 161 }, index.GetIdsInRange(10, 20).ToArray());
        CollectionAssert.AreEqual(new ulong[] { 161, 160, 181, 180 }, index.GetIdsInRange(10, 20, descending: true).ToArray());
        CollectionAssert.AreEqual(new ulong[] { 160, 161 }, index.GetIdsInRange(10, 30, includeFrom: false, includeTo: false).ToArray());
        CollectionAssert.AreEqual(new ulong[] { 160, 161, 140, 141 }, index.GetIdsGreaterThan(20).ToArray());
        CollectionAssert.AreEqual(new ulong[] { 140, 141 }, index.GetIdsGreaterThan(20, includeValue: false).ToArray());
        CollectionAssert.AreEqual(new ulong[] { 180, 181 }, index.GetIdsSmallerThan(20, includeValue: false).ToArray());

        var entries = index.GetEntriesInRange(10, 30).ToArray();
        CollectionAssert.AreEqual(new ulong[] { 180, 181, 160, 161, 140, 141 }, entries.Select(e => e.Key).ToArray());
        CollectionAssert.AreEqual(new[] { 10, 10, 20, 20, 30, 30 }, entries.Select(e => e.Value).ToArray());

        Assert.AreEqual(4, index.CountIdsInRange(10, 20));
        Assert.AreEqual(2, index.CountIdsInRange(10, 30, includeFrom: false, includeTo: false));
        Assert.AreEqual(4, index.CountIdsGreaterThan(20));
        Assert.AreEqual(2, index.CountIdsSmallerThan(20, includeValue: false));
    }

    [TestMethod]
    public void BPlusTree_GuidIds_DuplicateValues_AscendingIdsPerValue() {
        using var engine = new BPlusTreeStorageEngine(null);
        var index = engine.OpenOrCreateSortedGuidIndex<string>("dupes");
        var shared = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var single = Guid.NewGuid();
        commit(engine, () => {
            foreach (var id in shared) index.Set(id, "shared");
            index.Set(single, "single");
        });

        Assert.AreEqual(6, index.Count);
        Assert.AreEqual(2, index.DistinctValueCount);
        CollectionAssert.AreEqual(shared.OrderBy(g => g).ToArray(), index.GetIds("shared").ToArray());
        CollectionAssert.AreEqual(new[] { single }, index.GetIds("single").ToArray());
        CollectionAssert.AreEqual(new[] { "shared", "single" }, index.DistinctValues.ToArray()); // "shared" < "single" ordinally

        // moving an id off a shared value must not disturb the others
        commit(engine, () => index.Set(shared[2], "single"), timestamp: 2);
        CollectionAssert.AreEqual(shared.Where((_, i) => i != 2).OrderBy(g => g).ToArray(), index.GetIds("shared").ToArray());
        Assert.AreEqual(2, index.GetIds("single").Count());

        commit(engine, () => Assert.IsTrue(index.Remove(single)), timestamp: 3);
        Assert.AreEqual(5, index.Count);
        Assert.IsFalse(index.ContainsKey(single));
    }

    [TestMethod]
    public void BPlusTree_ValueCache_ServesAndEvictsUlongAndGuidIds() {
        using var engine = new BPlusTreeStorageEngine(null, new BPlusTreeEngineOptions { ValueCacheEntries = 64 });
        var byUlong = engine.OpenOrCreateSortedUlongIndex<string>("cached-ulong");
        var byGuid = engine.OpenOrCreateSortedGuidIndex<string>("cached-guid");
        var guid = Guid.NewGuid();

        commit(engine, () => {
            byUlong.Set(ulong.MaxValue, "u1");
            byGuid.Set(guid, "g1");
        });
        // twice: first populates the cache, second must hit it
        Assert.AreEqual("u1", byUlong.GetValue(ulong.MaxValue));
        Assert.AreEqual("u1", byUlong.GetValue(ulong.MaxValue));
        Assert.AreEqual("g1", byGuid.GetValue(guid));
        Assert.AreEqual("g1", byGuid.GetValue(guid));

        // overwrite: commit-time eviction must invalidate the cached entries
        commit(engine, () => {
            byUlong.Set(ulong.MaxValue, "u2");
            byGuid.Set(guid, "g2");
        }, timestamp: 2);
        Assert.AreEqual("u2", byUlong.GetValue(ulong.MaxValue));
        Assert.AreEqual("g2", byGuid.GetValue(guid));

        commit(engine, () => {
            Assert.IsTrue(byUlong.Remove(ulong.MaxValue));
            Assert.IsTrue(byGuid.Remove(guid));
        }, timestamp: 3);
        Assert.IsFalse(byUlong.TryGetValue(ulong.MaxValue, out _));
        Assert.IsFalse(byGuid.TryGetValue(guid, out _));
    }

    [TestMethod]
    public void BPlusTree_IdKindMismatch_Throws() {
        var dir = tempDir();
        try {
            var filePath = Path.Combine(dir, "kinds.db");
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                var index = engine.OpenOrCreateSortedIntIndex<string>("idx");
                commit(engine, () => index.Set(1, "a")); // persists the definition to the catalog

                // same session, already open with a different id type
                Assert.ThrowsException<InvalidOperationException>(() => engine.OpenOrCreateSortedUlongIndex<string>("idx"));
                Assert.ThrowsException<InvalidOperationException>(() => engine.OpenOrCreateSortedGuidIndex<string>("idx"));
            }
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                // reopen from disk with a different id type must fail even though the value type matches
                Assert.ThrowsException<InvalidOperationException>(() => engine.OpenOrCreateSortedUlongIndex<string>("idx"));
                // matching id and value type still opens
                Assert.AreEqual("a", engine.OpenOrCreateSortedIntIndex<string>("idx").GetValue(1));
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }
}
