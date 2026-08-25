using Relatude.DB.Datastores.Indexes.BTreeIndex;

namespace Relatude.Indexes;

/// <summary>
/// Values too large for a bucket cell in the hash layout: they spill onto a chain of overflow
/// pages while the cell keeps a reference. Covers the size boundaries and every transition between
/// inline and spilled storage, the bucket machinery carrying references through splits and copies,
/// reclamation of the chains a replace/remove/drop releases, durability across reopen, and the
/// unescaped value encoding this layout uses (which is what lets zero-filled content — a float
/// vector is roughly a quarter zero bytes — cost its own size and nothing more).
/// </summary>
[TestClass]
public class KvOverflowValueTests {

    // Just inside the inline limit, just past it, one full chain page, and several pages with a
    // partial last one — the sizes where the cell layout or the chain arithmetic changes.
    static readonly int[] _sizes = [1023, 1024, 1025, 4088, 4089, 8176, 12288, 100_000];

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

    /// <summary>Deterministic content of a given size, a quarter of it zero bytes like a real float vector.</summary>
    static byte[] payload(int size, int seed) {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++) bytes[i] = (byte)(i % 4 == 3 ? 0 : i + seed);
        return bytes;
    }

    [TestMethod]
    public void Overflow_RoundTripsEverySizeAndBothTransitions() {
        using var engine = new BPlusTreeStorageEngine(null);
        var index = engine.OpenOrCreateIntHashIndex<byte[]>("sizes");

        commit(engine, () => {
            for (var i = 0; i < _sizes.Length; i++) index.Set(i, payload(_sizes[i], i));
        });
        Assert.AreEqual(_sizes.Length, index.Count);
        for (var i = 0; i < _sizes.Length; i++)
            CollectionAssert.AreEqual(payload(_sizes[i], i), index.GetValue(i), $"size {_sizes[i]} did not round-trip");

        // enumeration resolves chains too, and lookup by value compares them without materializing
        foreach (var entry in index.Entries) CollectionAssert.AreEqual(payload(_sizes[entry.Key], entry.Key), entry.Value);
        CollectionAssert.AreEqual(new[] { 6 }, index.GetIds(payload(12288, 6)).ToArray());
        Assert.AreEqual(0, index.GetIds(payload(12288, 99)).Count()); // same size, different content
        Assert.AreEqual(0, index.GetIds(payload(12287, 6)).Count());  // same content, one byte short

        // every transition between inline and spilled, in both directions and between chain lengths
        commit(engine, () => {
            index.Set(0, payload(50_000, 0));   // inline -> overflow
            index.Set(6, payload(10, 6));       // overflow -> inline
            index.Set(7, payload(4089, 7));     // long chain -> short chain
            index.Set(4, payload(60_000, 4));   // short chain -> long chain
        }, timestamp: 2);
        CollectionAssert.AreEqual(payload(50_000, 0), index.GetValue(0));
        CollectionAssert.AreEqual(payload(10, 6), index.GetValue(6));
        CollectionAssert.AreEqual(payload(4089, 7), index.GetValue(7));
        CollectionAssert.AreEqual(payload(60_000, 4), index.GetValue(4));

        // rewriting an entry with the value it already holds is a no-op, chain compare included
        commit(engine, () => index.Set(4, payload(60_000, 4)), timestamp: 3);
        CollectionAssert.AreEqual(payload(60_000, 4), index.GetValue(4));

        commit(engine, () => Assert.IsTrue(index.Remove(4)), timestamp: 4);
        Assert.IsFalse(index.ContainsKey(4));
        Assert.AreEqual(_sizes.Length - 1, index.Count);
        CollectionAssert.AreEqual(payload(50_000, 0), index.GetValue(0)); // its neighbours are untouched
    }

    [TestMethod]
    public void Overflow_SurvivesDiskAndReopen() {
        var dir = tempDir();
        try {
            var filePath = Path.Combine(dir, "overflow.db");
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                var index = engine.OpenOrCreateUlongHashIndex<byte[]>("vectors");
                commit(engine, () => {
                    for (var i = 0; i < 200; i++) index.Set((ulong)i, payload(12288, i)); // 3072-dim float vectors
                }, timestamp: 9);
            }
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                var index = engine.OpenOrCreateUlongHashIndex<byte[]>("vectors");
                Assert.AreEqual(200, index.Count);
                for (var i = 0; i < 200; i++) CollectionAssert.AreEqual(payload(12288, i), index.GetValue((ulong)i));

                // and it keeps mutating on top of a directory and chains read back from disk
                commit(engine, () => {
                    for (var i = 0; i < 200; i += 2) index.Set((ulong)i, payload(20_000, i));
                    for (var i = 1; i < 200; i += 4) Assert.IsTrue(index.Remove((ulong)i));
                }, timestamp: 10);
            }
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                var index = engine.OpenOrCreateUlongHashIndex<byte[]>("vectors");
                Assert.AreEqual(150, index.Count);
                for (var i = 0; i < 200; i++) {
                    if (i % 2 == 0) CollectionAssert.AreEqual(payload(20_000, i), index.GetValue((ulong)i));
                    else if (i % 4 == 1) Assert.IsFalse(index.ContainsKey((ulong)i));
                    else CollectionAssert.AreEqual(payload(12288, i), index.GetValue((ulong)i));
                }
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void Overflow_ChainsAreReclaimedWhenReplacedOrRemoved() {
        // A replaced or removed entry is the only reference to its chain, so those pages must come
        // back through the freelist. If they did not, rewriting the whole index would multiply the
        // file size by the number of rounds instead of leaving it flat.
        const int n = 100;
        var dir = tempDir();
        try {
            var filePath = Path.Combine(dir, "reclaim.db");
            using var engine = new BPlusTreeStorageEngine(filePath);
            var index = engine.OpenOrCreateIntHashIndex<byte[]>("churn");

            commit(engine, () => { for (var i = 0; i < n; i++) index.Set(i, payload(12288, i)); });
            var afterFirstWrite = engine.GetTotalDiskSpace();
            Assert.IsTrue(afterFirstWrite > n * 12288L, $"{n} spilled values should occupy at least their own bytes, not {afterFirstWrite}");

            for (var round = 2; round <= 6; round++) {
                var r = round;
                commit(engine, () => { for (var i = 0; i < n; i++) index.Set(i, payload(12288, i + r * 1000)); }, timestamp: round);
            }
            var afterRewrites = engine.GetTotalDiskSpace();
            Assert.IsTrue(afterRewrites < afterFirstWrite * 5 / 2,
                $"five rewrites grew the file from {afterFirstWrite} to {afterRewrites} bytes: replaced chains are leaking");
            for (var i = 0; i < n; i++) CollectionAssert.AreEqual(payload(12288, i + 6000), index.GetValue(i));

            // emptying it and refilling must run mostly on those recycled pages
            commit(engine, () => { for (var i = 0; i < n; i++) Assert.IsTrue(index.Remove(i)); }, timestamp: 7);
            commit(engine, () => { for (var i = 0; i < n; i++) index.Set(i, payload(12288, i)); }, timestamp: 8);
            Assert.IsTrue(engine.GetTotalDiskSpace() < afterRewrites + afterFirstWrite,
                "refilling after a full delete should reuse the freed chain pages");
            for (var i = 0; i < n; i++) CollectionAssert.AreEqual(payload(12288, i), index.GetValue(i));
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void Overflow_SplitsAndDoublingsCarryChainsAlong() {
        // Enough spilled values to split buckets and double the directory many times: a cell moving
        // to another page must take its reference with it, and no chain may be freed on the way.
        const int n = 3000;
        using var engine = new BPlusTreeStorageEngine(null);
        var index = engine.OpenOrCreateGuidHashIndex<byte[]>("split");
        var ids = Enumerable.Range(0, n).Select(_ => Guid.NewGuid()).ToArray();

        commit(engine, () => { for (var i = 0; i < n; i++) index.Set(ids[i], payload(2000 + i % 7, i)); });

        Assert.AreEqual(n, index.Count);
        for (var i = 0; i < n; i++) CollectionAssert.AreEqual(payload(2000 + i % 7, i), index.GetValue(ids[i]));
        CollectionAssert.AreEquivalent(ids, index.Keys.ToArray());
        Assert.AreEqual(n, index.Entries.Count());
    }

    [TestMethod]
    public void Overflow_RollbackDiscardsChains() {
        using var engine = new BPlusTreeStorageEngine(null);
        var index = engine.OpenOrCreateIntHashIndex<byte[]>("rollback");
        commit(engine, () => { for (var i = 0; i < 50; i++) index.Set(i, payload(12288, i)); });

        engine.BeginTransaction();
        for (var i = 0; i < 50; i++) index.Set(i, payload(30_000, i)); // replaces every chain
        index.Set(999, payload(30_000, 999));
        index.Remove(7);
        CollectionAssert.AreEqual(payload(30_000, 0), index.GetValue(0)); // the writer sees its own state
        engine.RollbackTransaction();

        Assert.AreEqual(50, index.Count);
        Assert.IsFalse(index.ContainsKey(999));
        for (var i = 0; i < 50; i++) CollectionAssert.AreEqual(payload(12288, i), index.GetValue(i));
    }

    [TestMethod]
    public void Overflow_DeleteUnopenedIndexes_ReclaimsChains() {
        var dir = tempDir();
        try {
            var filePath = Path.Combine(dir, "drop.db");
            long beforeDrop;
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                var keep = engine.OpenOrCreateIntHashIndex<byte[]>("keep");
                var drop = engine.OpenOrCreateUlongHashIndex<byte[]>("drop");
                commit(engine, () => {
                    for (var i = 0; i < 20; i++) keep.Set(i, payload(12288, i));
                    for (var i = 0; i < 100; i++) drop.Set((ulong)i, payload(12288, i));
                });
                beforeDrop = engine.GetTotalDiskSpace();
            }
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                var keep = engine.OpenOrCreateIntHashIndex<byte[]>("keep");
                engine.DeleteUnopenedIndexes(); // "drop" is never opened: its buckets AND chains go
                for (var i = 0; i < 20; i++) CollectionAssert.AreEqual(payload(12288, i), keep.GetValue(i));

                // the dropped index's chain pages are on the freelist, so writing an index of the
                // same shape must not grow the file by another copy of it
                var again = engine.OpenOrCreateUlongHashIndex<byte[]>("again");
                commit(engine, () => { for (var i = 0; i < 100; i++) again.Set((ulong)i, payload(12288, i)); }, timestamp: 2);
                var grew = engine.GetTotalDiskSpace() - beforeDrop;
                Assert.IsTrue(grew < beforeDrop / 2,
                    $"the file grew by {grew} bytes after dropping and rewriting an index of {beforeDrop} bytes: chains were not reclaimed");
                for (var i = 0; i < 100; i++) CollectionAssert.AreEqual(payload(12288, i), again.GetValue((ulong)i));
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void HashValues_AreStoredWithoutEscaping() {
        // A sorted index's values are ordered tree keys, so they are escaped to stay prefix-free:
        // 1024 zero bytes become 2050 and no longer fit. The hash layout only ever asks whether two
        // encodings are byte-identical, so it stores the bytes verbatim — the same value fits inline
        // there, and content full of zeros costs exactly what content without any costs.
        var zeros = new byte[1024];
        var noZeros = Enumerable.Range(0, 1024).Select(i => (byte)(i | 1)).ToArray();

        using var engine = new BPlusTreeStorageEngine(null);
        var sorted = engine.OpenOrCreateSortedIntIndex<byte[]>("sorted");
        engine.BeginTransaction();
        Assert.ThrowsException<ArgumentException>(() => sorted.Set(1, zeros));
        engine.RollbackTransaction();

        var hash = engine.OpenOrCreateIntHashIndex<byte[]>("hash");
        commit(engine, () => { hash.Set(1, zeros); hash.Set(2, noZeros); }, timestamp: 2);
        CollectionAssert.AreEqual(zeros, hash.GetValue(1));
        CollectionAssert.AreEqual(noZeros, hash.GetValue(2));
        CollectionAssert.AreEqual(new[] { 1 }, hash.GetIds(new byte[1024]).ToArray()); // matched by content

        // both are inline: a page-for-page identical footprint proves neither was inflated
        Assert.AreEqual(diskFootprint(zeros), diskFootprint(noZeros));

        // strings lose their escaping too, embedded NULs and all (the payload is length-delimited)
        var strings = engine.OpenOrCreateIntHashIndex<string>("strings");
        var hostile = "a\0b\0\0c" + new string('\0', 2000) + "end";
        commit(engine, () => { strings.Set(1, hostile); strings.Set(2, ""); }, timestamp: 3);
        Assert.AreEqual(hostile, strings.GetValue(1));
        Assert.AreEqual("", strings.GetValue(2));
        CollectionAssert.AreEqual(new[] { 1 }, strings.GetIds(hostile).ToArray());
    }

    static long diskFootprint(byte[] value) {
        var dir = tempDir();
        try {
            using var engine = new BPlusTreeStorageEngine(Path.Combine(dir, "one.db"));
            var index = engine.OpenOrCreateIntHashIndex<byte[]>("one");
            commit(engine, () => index.Set(1, value));
            return engine.GetTotalDiskSpace();
        } finally {
            Directory.Delete(dir, true);
        }
    }
}
