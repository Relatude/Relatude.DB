using Relatude.DB.Datastores.Indexes.BTreeIndex;

namespace Relatude.Indexes;

/// <summary>
/// The unordered hash layout of the B+Tree engine (OpenOrCreate*HashIndex): the same file,
/// transactions and timestamps as a sorted index, but reached by hashed lookup. Covers the
/// mapping semantics, the growth path that splits buckets and doubles the directory, enumeration
/// (each bucket exactly once, however many directory slots name it), durability across reopen,
/// snapshot isolation and rollback, page reclamation, and the layout binding on an index name.
/// </summary>
[TestClass]
public class KvHashIndexTests {

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

    [TestMethod]
    public void HashIndex_IntIds_MapSemantics() {
        using var engine = new BPlusTreeStorageEngine(null); // memory-only
        var index = engine.OpenOrCreateIntHashIndex<string>("by-int");

        Assert.AreEqual(0, index.Count);
        Assert.IsFalse(index.TryGetValue(1, out _));
        Assert.ThrowsException<KeyNotFoundException>(() => index.GetValue(1));
        Assert.ThrowsException<InvalidOperationException>(() => index.Set(1, "no transaction"));

        commit(engine, () => {
            index.Set(int.MinValue, "min");
            index.Set(0, "zero");
            index.Set(7, "seven");
            index.Set(int.MaxValue, "max");
        });

        Assert.AreEqual(4, index.Count);
        Assert.AreEqual("min", index.GetValue(int.MinValue));
        Assert.AreEqual("zero", index.GetValue(0));
        Assert.AreEqual("max", index.GetValue(int.MaxValue));
        Assert.IsTrue(index.ContainsKey(7));
        Assert.IsFalse(index.ContainsKey(8));
        CollectionAssert.AreEquivalent(new[] { int.MinValue, 0, 7, int.MaxValue }, index.Keys.ToArray());

        // replacing a mapping keeps one entry per id, and the old value stops being reachable
        commit(engine, () => index.Set(7, "SEVEN"), timestamp: 2);
        Assert.AreEqual(4, index.Count);
        Assert.AreEqual("SEVEN", index.GetValue(7));
        Assert.AreEqual(0, index.GetIds("seven").Count());
        CollectionAssert.AreEqual(new[] { 7 }, index.GetIds("SEVEN").ToArray());

        // many ids may share a value
        commit(engine, () => { index.Set(100, "dup"); index.Set(200, "dup"); }, timestamp: 3);
        CollectionAssert.AreEquivalent(new[] { 100, 200 }, index.GetIds("dup").ToArray());

        commit(engine, () => {
            Assert.IsTrue(index.Remove(0));
            Assert.IsFalse(index.Remove(0));
            Assert.IsFalse(index.Remove(12345));
        }, timestamp: 4);
        Assert.AreEqual(5, index.Count);
        Assert.IsFalse(index.ContainsKey(0));
    }

    [TestMethod]
    public void HashIndex_GrowsThroughSplitsAndDirectoryDoublings() {
        const int n = 20_000; // far past one bucket page: forces repeated splits and doublings
        using var engine = new BPlusTreeStorageEngine(null);
        var index = engine.OpenOrCreateIntHashIndex<string>("grow");

        commit(engine, () => { for (var i = 0; i < n; i++) index.Set(i, "v" + i); });

        Assert.AreEqual(n, index.Count);
        for (var i = 0; i < n; i++) Assert.AreEqual("v" + i, index.GetValue(i));
        Assert.IsFalse(index.ContainsKey(n));

        // every entry surfaces exactly once, however many directory slots point at its bucket
        var keys = index.Keys.ToArray();
        Assert.AreEqual(n, keys.Length);
        CollectionAssert.AreEquivalent(Enumerable.Range(0, n).ToArray(), keys);
        var entries = index.Entries.ToArray();
        Assert.AreEqual(n, entries.Length);
        foreach (var entry in entries) Assert.AreEqual("v" + entry.Key, entry.Value);

        // shrink it back down: emptied buckets release their pages, and the survivors stay reachable
        commit(engine, () => { for (var i = 0; i < n; i++) if (i % 3 != 0) index.Remove(i); }, timestamp: 2);
        var expected = Enumerable.Range(0, n).Where(i => i % 3 == 0).ToArray();
        Assert.AreEqual(expected.Length, index.Count);
        CollectionAssert.AreEquivalent(expected, index.Keys.ToArray());
        foreach (var i in expected) Assert.AreEqual("v" + i, index.GetValue(i));

        commit(engine, () => { for (var i = 0; i < n; i++) index.Remove(i); }, timestamp: 3);
        Assert.AreEqual(0, index.Count);
        Assert.AreEqual(0, index.Keys.Count());

        // and it refills correctly from empty
        commit(engine, () => { for (var i = 0; i < 500; i++) index.Set(i, "again"); }, timestamp: 4);
        Assert.AreEqual(500, index.Count);
        Assert.AreEqual(500, index.GetIds("again").Count());
    }

    [TestMethod]
    public void HashIndex_LargeValues_StillSplitWithFewCellsPerBucket() {
        using var engine = new BPlusTreeStorageEngine(null);
        var index = engine.OpenOrCreateUlongHashIndex<byte[]>("big");
        var ids = Enumerable.Range(0, 200).Select(i => (ulong)i * 0x1000_0000_0000_0001ul).ToArray();

        // 500 raw bytes encode to ~1000 with the escaping codec: only a couple fit a page,
        // so the table has to split all the way down to very small buckets
        byte[] payload(int seed) => Enumerable.Range(0, 500).Select(i => (byte)(i + seed)).ToArray();
        commit(engine, () => { for (var i = 0; i < ids.Length; i++) index.Set(ids[i], payload(i)); });

        Assert.AreEqual(ids.Length, index.Count);
        for (var i = 0; i < ids.Length; i++) CollectionAssert.AreEqual(payload(i), index.GetValue(ids[i]));
        CollectionAssert.AreEquivalent(ids, index.Keys.ToArray());

        // a value too large for a bucket is rejected, and leaves the index untouched
        commit(engine, () => {
            Assert.ThrowsException<ArgumentException>(() => index.Set(1ul, new byte[2000]));
        }, timestamp: 2);
        Assert.AreEqual(ids.Length, index.Count);
    }

    [TestMethod]
    public void HashIndex_RoundTripsThroughDiskAndReopen() {
        var dir = tempDir();
        try {
            var filePath = Path.Combine(dir, "hash.db");
            var guids = Enumerable.Range(0, 300).Select(_ => Guid.NewGuid()).ToArray();
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                var byInt = engine.OpenOrCreateIntHashIndex<string>("ints");
                var byGuid = engine.OpenOrCreateGuidHashIndex<int>("guids");
                Assert.AreEqual(0, byInt.GetTimestamp()); // newly created: not yet synchronized
                commit(engine, () => {
                    for (var i = 0; i < 3000; i++) byInt.Set(i, "v" + i);
                    for (var i = 0; i < guids.Length; i++) byGuid.Set(guids[i], i);
                }, timestamp: 17);
                Assert.AreEqual(17, byInt.GetTimestamp());

                // a second commit must not disturb what the first one wrote
                commit(engine, () => byInt.Set(0, "changed"), timestamp: 18);
            }
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                var byInt = engine.OpenOrCreateIntHashIndex<string>("ints");
                var byGuid = engine.OpenOrCreateGuidHashIndex<int>("guids");
                Assert.AreEqual(18, engine.GetTimestamp());
                Assert.AreEqual(18, byInt.GetTimestamp()); // opened existing: reports the engine's
                Assert.AreEqual(3000, byInt.Count);
                Assert.AreEqual("changed", byInt.GetValue(0));
                for (var i = 1; i < 3000; i++) Assert.AreEqual("v" + i, byInt.GetValue(i));
                Assert.AreEqual(guids.Length, byGuid.Count);
                for (var i = 0; i < guids.Length; i++) Assert.AreEqual(i, byGuid.GetValue(guids[i]));
                CollectionAssert.AreEquivalent(guids, byGuid.Keys.ToArray());

                // and it keeps growing on top of what was read back from disk
                commit(engine, () => { for (var i = 3000; i < 6000; i++) byInt.Set(i, "v" + i); }, timestamp: 19);
                Assert.AreEqual(6000, byInt.Count);
                for (var i = 3000; i < 6000; i++) Assert.AreEqual("v" + i, byInt.GetValue(i));
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void HashIndex_MultiChunkDirectory_SurvivesReopen() {
        // ~900 byte values leave room for only a handful of entries per bucket, so a few thousand
        // of them push the directory past one chunk (1024 slots) — the path where a doubling
        // aliases chunk arrays and persistence spans several pages and a root chain.
        const int n = 8000;
        static byte[] payload(int seed) => Enumerable.Range(0, 900).Select(i => (byte)(i + seed + 1)).ToArray();
        var dir = tempDir();
        try {
            var filePath = Path.Combine(dir, "chunks.db");
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                var index = engine.OpenOrCreateIntHashIndex<byte[]>("wide");
                commit(engine, () => { for (var i = 0; i < n; i++) index.Set(i, payload(i)); });
                Assert.AreEqual(n, index.Count);
            }
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                var index = engine.OpenOrCreateIntHashIndex<byte[]>("wide");
                Assert.AreEqual(n, index.Count);
                for (var i = 0; i < n; i++) CollectionAssert.AreEqual(payload(i), index.GetValue(i));
                CollectionAssert.AreEquivalent(Enumerable.Range(0, n).ToArray(), index.Keys.ToArray());

                // keep growing on a directory that came back from disk: doubling now has to copy
                // chunks it does not own before writing them
                commit(engine, () => { for (var i = n; i < n * 2; i++) index.Set(i, payload(i)); }, timestamp: 2);
                Assert.AreEqual(n * 2, index.Count);
                for (var i = 0; i < n * 2; i++) CollectionAssert.AreEqual(payload(i), index.GetValue(i));
            }
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                var index = engine.OpenOrCreateIntHashIndex<byte[]>("wide");
                Assert.AreEqual(n * 2, index.Count);
                CollectionAssert.AreEquivalent(Enumerable.Range(0, n * 2).ToArray(), index.Keys.ToArray());
                commit(engine, () => { for (var i = 0; i < n; i++) Assert.IsTrue(index.Remove(i)); }, timestamp: 3);
                Assert.AreEqual(n, index.Count);
                for (var i = n; i < n * 2; i++) CollectionAssert.AreEqual(payload(i), index.GetValue(i));
                Assert.IsFalse(index.ContainsKey(0));
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void HashIndex_RollbackDiscardsEverythingTheTransactionDid() {
        using var engine = new BPlusTreeStorageEngine(null);
        var index = engine.OpenOrCreateIntHashIndex<string>("rollback");
        commit(engine, () => { for (var i = 0; i < 2000; i++) index.Set(i, "before"); });

        engine.BeginTransaction();
        for (var i = 0; i < 4000; i++) index.Set(i, "after"); // grows the table and rewrites entries
        index.Remove(5);
        Assert.AreEqual("after", index.GetValue(0)); // the writer sees its own uncommitted state
        Assert.IsFalse(index.ContainsKey(5));
        engine.RollbackTransaction();

        Assert.AreEqual(2000, index.Count);
        Assert.AreEqual("before", index.GetValue(0));
        Assert.AreEqual("before", index.GetValue(5));
        Assert.IsFalse(index.ContainsKey(3000));
    }

    [TestMethod]
    public void HashIndex_ReadersSeeTheSnapshotTheyPinned() {
        using var engine = new BPlusTreeStorageEngine(null);
        var index = engine.OpenOrCreateIntHashIndex<string>("snapshot");
        commit(engine, () => { for (var i = 0; i < 1000; i++) index.Set(i, "v1"); });

        engine.BeginTransaction();
        for (var i = 0; i < 2000; i++) index.Set(i, "v2"); // splits buckets and doubles the directory

        // another thread reads the committed snapshot while the writer is mid-transaction
        var reader = Task.Run(() => {
            var count = index.Count;
            var values = Enumerable.Range(0, 1000).Select(index.GetValue).Distinct().ToArray();
            var keys = index.Keys.Count();
            return (count, values, keys);
        });
        var (committedCount, committedValues, committedKeys) = reader.Result;
        engine.CommitTransaction(2, true);

        Assert.AreEqual(1000, committedCount);
        Assert.AreEqual(1000, committedKeys);
        CollectionAssert.AreEqual(new[] { "v1" }, committedValues);
        Assert.AreEqual(2000, index.Count);
        Assert.AreEqual("v2", index.GetValue(0));
    }

    [TestMethod]
    public void HashIndex_ConcurrentReadersSurviveAStreamOfCommits() {
        // Commits move buckets to new pages, free the old ones and rewrite directory chunks; a
        // reader that had pinned a snapshot must keep seeing a coherent one throughout.
        const int n = 3000;
        using var engine = new BPlusTreeStorageEngine(null);
        var index = engine.OpenOrCreateIntHashIndex<string>("churn");
        commit(engine, () => { for (var i = 0; i < n; i++) index.Set(i, "v0"); });

        using var stop = new CancellationTokenSource();
        Exception? failure = null;
        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() => {
            var random = new Random();
            try {
                while (!stop.IsCancellationRequested) {
                    Assert.IsTrue(index.TryGetValue(random.Next(n), out var value));
                    Assert.IsTrue(value.StartsWith("v"), value);
                    Assert.AreEqual(n, index.Keys.Count()); // the id set never changes, only the values
                }
            } catch (Exception ex) {
                Interlocked.CompareExchange(ref failure, ex, null);
            }
        })).ToArray();

        for (var round = 1; round <= 30; round++) {
            var value = "v" + round;
            commit(engine, () => { for (var i = 0; i < n; i++) index.Set(i, value); }, timestamp: round);
        }
        stop.Cancel();
        Task.WaitAll(readers);

        Assert.IsNull(failure, failure?.ToString());
        Assert.AreEqual(n, index.Count);
        Assert.AreEqual("v30", index.GetValue(0));
    }

    [TestMethod]
    public void HashIndex_ValueCacheServesAndEvicts() {
        using var engine = new BPlusTreeStorageEngine(null, new BPlusTreeEngineOptions { ValueCacheEntries = 64 });
        var index = engine.OpenOrCreateIntHashIndex<string>("cached");
        commit(engine, () => index.Set(1, "a"));

        Assert.AreEqual("a", index.GetValue(1)); // populates
        Assert.AreEqual("a", index.GetValue(1)); // hits

        commit(engine, () => index.Set(1, "b"), timestamp: 2);
        Assert.AreEqual("b", index.GetValue(1));

        commit(engine, () => Assert.IsTrue(index.Remove(1)), timestamp: 3);
        Assert.IsFalse(index.TryGetValue(1, out _));
        Assert.IsFalse(index.ContainsKey(1));
    }

    [TestMethod]
    public void HashIndex_NameIsBoundToItsLayoutAndTypes() {
        var dir = tempDir();
        try {
            var filePath = Path.Combine(dir, "layouts.db");
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                var sorted = engine.OpenOrCreateIntIndex<string>("sorted");
                var hash = engine.OpenOrCreateIntHashIndex<string>("hash");
                commit(engine, () => { sorted.Set(1, "a"); hash.Set(1, "a"); });

                Assert.ThrowsException<InvalidOperationException>(() => engine.OpenOrCreateIntHashIndex<string>("sorted"));
                Assert.ThrowsException<InvalidOperationException>(() => engine.OpenOrCreateIntIndex<string>("hash"));
                Assert.ThrowsException<InvalidOperationException>(() => engine.OpenOrCreateUlongHashIndex<string>("hash"));
                Assert.ThrowsException<InvalidOperationException>(() => engine.OpenOrCreateIntHashIndex<int>("hash"));
            }
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                // the layout is persisted, so the same mismatches are caught on a fresh open too
                Assert.ThrowsException<InvalidOperationException>(() => engine.OpenOrCreateIntHashIndex<string>("sorted"));
                Assert.ThrowsException<InvalidOperationException>(() => engine.OpenOrCreateIntIndex<string>("hash"));
                Assert.AreEqual("a", engine.OpenOrCreateIntHashIndex<string>("hash").GetValue(1));
                Assert.AreEqual("a", engine.OpenOrCreateIntIndex<string>("sorted").GetValue(1));
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void HashIndex_DeleteAllEmptiesOpenIndexes() {
        using var engine = new BPlusTreeStorageEngine(null);
        var hash = engine.OpenOrCreateIntHashIndex<string>("hash");
        var sorted = engine.OpenOrCreateIntIndex<string>("sorted");
        commit(engine, () => {
            for (var i = 0; i < 5000; i++) hash.Set(i, "v");
            sorted.Set(1, "v");
        }, timestamp: 5);

        engine.DeleteAll();
        Assert.AreEqual(0, engine.GetTimestamp());
        Assert.AreEqual(0, hash.Count);
        Assert.AreEqual(0, sorted.Count);
        Assert.AreEqual(0, hash.Keys.Count());
        Assert.IsFalse(hash.ContainsKey(1));

        // the handle stays valid and usable
        commit(engine, () => { for (var i = 0; i < 5000; i++) hash.Set(i, "w"); }, timestamp: 6);
        Assert.AreEqual(5000, hash.Count);
        Assert.AreEqual("w", hash.GetValue(4999));
    }

    [TestMethod]
    public void HashIndex_DeleteUnopenedIndexes_ReclaimsPagesAndDefinitions() {
        var dir = tempDir();
        try {
            var filePath = Path.Combine(dir, "drop.db");
            long keepOnly, withBoth;
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                var keep = engine.OpenOrCreateIntHashIndex<string>("keep");
                commit(engine, () => { for (var i = 0; i < 5000; i++) keep.Set(i, "k" + i); });
                keepOnly = engine.GetTotalDiskSpace();

                var drop = engine.OpenOrCreateIntHashIndex<string>("drop");
                commit(engine, () => { for (var i = 0; i < 5000; i++) drop.Set(i, "d" + i); }, timestamp: 2);
                withBoth = engine.GetTotalDiskSpace();
            }
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                var keep = engine.OpenOrCreateIntHashIndex<string>("keep");
                engine.DeleteUnopenedIndexes();
                Assert.AreEqual(5000, keep.Count);
                Assert.AreEqual("k42", keep.GetValue(42));

                // the dropped index is gone: reopening its name yields a fresh, empty index
                var reopened = engine.OpenOrCreateIntHashIndex<string>("drop");
                Assert.AreEqual(0, reopened.Count);
                Assert.AreEqual(0, reopened.GetTimestamp());

                // its pages were freed, so refilling it runs mostly on recycled pages: the file
                // grows by a fraction of what a second copy of the index costs (some growth is
                // expected — a page is only reusable once the transaction that freed it commits)
                commit(engine, () => { for (var i = 0; i < 5000; i++) reopened.Set(i, "d" + i); }, timestamp: 3);
                var grew = engine.GetTotalDiskSpace() - withBoth;
                var indexCost = withBoth - keepOnly;
                Assert.IsTrue(grew < indexCost / 2,
                    $"refilling a dropped index grew the file by {grew} bytes, against {indexCost} bytes for the index itself");
                Assert.AreEqual("d42", reopened.GetValue(42));
                Assert.AreEqual("k42", keep.GetValue(42));
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void HashIndex_MatchesTheSortedIndexOverARandomWorkload() {
        var random = new Random(20260805);
        using var engine = new BPlusTreeStorageEngine(null);
        var hash = engine.OpenOrCreateUlongHashIndex<int>("hash");
        var sorted = engine.OpenOrCreateUlongIndex<int>("sorted");
        var model = new Dictionary<ulong, int>();

        for (var round = 1; round <= 20; round++) {
            commit(engine, () => {
                for (var op = 0; op < 500; op++) {
                    var id = (ulong)random.Next(3000);
                    if (random.Next(4) == 0) {
                        Assert.AreEqual(model.Remove(id), hash.Remove(id));
                        sorted.Remove(id);
                    } else {
                        var value = random.Next(50);
                        model[id] = value;
                        hash.Set(id, value);
                        sorted.Set(id, value);
                    }
                }
            }, timestamp: round);

            Assert.AreEqual(model.Count, hash.Count);
            Assert.AreEqual(sorted.Count, hash.Count);
        }

        CollectionAssert.AreEquivalent(model.Keys.ToArray(), hash.Keys.ToArray());
        CollectionAssert.AreEquivalent(sorted.Keys.ToArray(), hash.Keys.ToArray());
        foreach (var (id, value) in model) Assert.AreEqual(value, hash.GetValue(id));
        foreach (var value in model.Values.Distinct())
            CollectionAssert.AreEquivalent(sorted.GetIds(value).ToArray(), hash.GetIds(value).ToArray());
        CollectionAssert.AreEquivalent(
            sorted.Entries.ToArray(), hash.Entries.OrderBy(e => e.Key).ToArray());
    }
}
