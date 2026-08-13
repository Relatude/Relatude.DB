using Relatude.DB.AI;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.VectorIndexHNSW2;

namespace Relatude.Indexes;

[TestClass]
public class Hnsw2VectorIndexTests {
    string _folder = "";
    [TestInitialize]
    public void Init() {
        _folder = Path.Combine(Path.GetTempPath(), "RelatudeDB_Hnsw2VectorIndexTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }
    [TestCleanup]
    public void Cleanup() {
        try { Directory.Delete(_folder, true); } catch { }
    }
    static Hnsw2VectorIndex create(string folder, Hnsw2VectorIndexOptions? options = null, AIEngine? ai = null) {
        return new Hnsw2VectorIndex(new SetRegister(10_000_000), "test-index", "Test HNSW2 Index", folder, ai, options);
    }
    static float[] randomUnit(Random r, int dims) {
        var v = new float[dims];
        for (var i = 0; i < dims; i++) v[i] = (float)(r.NextDouble() * 2 - 1);
        normalize(v);
        return v;
    }
    static void normalize(float[] v) {
        var len = Math.Sqrt(v.Sum(x => (double)x * x));
        for (var i = 0; i < v.Length; i++) v[i] = (float)(v[i] / len);
    }
    static float dot(float[] a, float[] b) {
        var sum = 0f;
        for (var i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }
    static List<int> bruteForceTopK(Dictionary<int, float[]> reference, float[] query, int k) {
        return reference.OrderByDescending(kv => dot(kv.Value, query)).Take(k).Select(kv => kv.Key).ToList();
    }

    [TestMethod]
    public void AddAndSearchExact() {
        // Below MinVectorsForGraphSearch the index answers by scanning, so it is exactly comparable
        // with a brute-force scan and the whole result ordering can be asserted.
        var r = new Random(42);
        const int dims = 64, count = 800;
        using var index = create(_folder);
        var reference = new Dictionary<int, float[]>();
        for (var id = 1; id <= count; id++) {
            var v = randomUnit(r, dims);
            reference[id] = v;
            index.Add(id, v);
        }
        Assert.AreEqual(count, index.Count);
        var hits = index.Search(reference[500], 0, 10, 0);
        Assert.AreEqual(10, hits.Count);
        Assert.AreEqual(500, hits[0].NodeId);
        Assert.IsTrue(hits[0].Similarity > 0.999f);
        for (var i = 1; i < hits.Count; i++) Assert.IsTrue(hits[i - 1].Similarity >= hits[i].Similarity);
        var expected = bruteForceTopK(reference, reference[500], 10);
        CollectionAssert.AreEqual(expected, hits.Select(h => h.NodeId).ToList());
        // paging: skip 3 take 4 equals the same slice of the full ordering
        var page = index.Search(reference[500], 3, 4, -1);
        CollectionAssert.AreEqual(expected.Skip(3).Take(4).ToList(), page.Select(h => h.NodeId).ToList());
        // threshold 1 matches only the identical vector, -1 matches all
        Assert.AreEqual(1, index.Search(reference[500], 0, int.MaxValue, 1).Count);
        Assert.AreEqual(count, index.Search(reference[500], 0, int.MaxValue, -1).Count);
    }

    [TestMethod]
    public void ThrowsOnBadVectors() {
        var r = new Random(1);
        using var index = create(_folder);
        index.Add(1, randomUnit(r, 32));
        Assert.ThrowsException<ArgumentException>(() => index.Add(2, randomUnit(r, 64))); // wrong length
        var notNormalized = new float[32];
        notNormalized[0] = 2f;
        Assert.ThrowsException<ArgumentException>(() => index.Add(3, notNormalized));
        Assert.ThrowsException<ArgumentException>(() => index.Search(randomUnit(r, 16), 0, 10, 0)); // wrong query length
        Assert.AreEqual(1, index.Count);
    }

    [TestMethod]
    public void PersistAndReopen() {
        var r = new Random(7);
        const int dims = 48, count = 300;
        var walId = Guid.NewGuid();
        var reference = new Dictionary<int, float[]>();
        using (var index = create(_folder)) {
            index.ReadStateForMemoryIndexes(walId); // fresh: nothing on disk
            Assert.AreEqual(0, index.PersistedTimestamp);
            for (var id = 1; id <= count; id++) {
                var v = randomUnit(r, dims);
                reference[id] = v;
                index.Add(id, v);
            }
            index.SaveStateForMemoryIndexes(1234, walId);
            Assert.AreEqual(1234, index.PersistedTimestamp);
        }
        using (var index = create(_folder)) {
            index.ReadStateForMemoryIndexes(walId);
            Assert.AreEqual(1234, index.PersistedTimestamp);
            Assert.AreEqual(count, index.Count);
            var hits = index.Search(reference[10], 0, 5, 0);
            Assert.AreEqual(10, hits[0].NodeId);
            Assert.IsTrue(hits[0].Similarity > 0.999f);
        }
        // a foreign WAL id means the data is of unknown provenance: reset for replay, no crash
        using (var index = create(_folder)) {
            index.ReadStateForMemoryIndexes(Guid.NewGuid());
            Assert.AreEqual(0, index.PersistedTimestamp);
            Assert.AreEqual(0, index.Count);
        }
    }

    [TestMethod]
    public void UpdateAndRemove() {
        var r = new Random(99);
        const int dims = 16;
        var walId = Guid.NewGuid();
        var updated = new Dictionary<int, float[]>();
        using (var index = create(_folder)) {
            index.ReadStateForMemoryIndexes(walId);
            for (var id = 1; id <= 100; id++) index.Add(id, randomUnit(r, dims));
            index.SaveStateForMemoryIndexes(1, walId);
            for (var id = 1; id <= 50; id++) {
                var v = randomUnit(r, dims);
                updated[id] = v;
                index.Add(id, v); // overwrite: a re-link under a new ordinal
            }
            for (var id = 51; id <= 60; id++) index.Remove(id, null!);
            index.Add(61, Array.Empty<float>()); // an empty embedding clears the entry
            index.SaveStateForMemoryIndexes(2, walId);
            Assert.AreEqual(89, index.Count);
        }
        using (var index = create(_folder)) {
            index.ReadStateForMemoryIndexes(walId);
            Assert.AreEqual(89, index.Count);
            var all = index.Search(updated[1], 0, int.MaxValue, -1);
            Assert.AreEqual(89, all.Count);
            var ids = all.Select(h => h.NodeId).ToHashSet();
            for (var id = 51; id <= 61; id++) Assert.IsFalse(ids.Contains(id), "removed id " + id + " resurfaced");
            var hits = index.Search(updated[1], 0, 1, 0);
            Assert.AreEqual(1, hits[0].NodeId); // finds the updated vector, not the original
            Assert.IsTrue(hits[0].Similarity > 0.999f);
        }
    }

    [TestMethod]
    public void GraphSearchAccuracy() {
        // Large and wide enough that the graph walk is the cheaper way to answer, so this is the
        // approximate path: recall is high rather than perfect, and a vector still finds itself.
        var r = new Random(2024);
        const int dims = 64, centers = 100, perCenter = 100;
        var walId = Guid.NewGuid();
        var reference = new Dictionary<int, float[]>();
        var options = new Hnsw2VectorIndexOptions { MinVectorsForGraphSearch = 1 };
        using (var index = create(_folder, options)) {
            index.ReadStateForMemoryIndexes(walId);
            var id = 1;
            for (var c = 0; c < centers; c++) { // naturally clustered data
                var center = randomUnit(r, dims);
                for (var i = 0; i < perCenter; i++) {
                    var v = new float[dims];
                    for (var d = 0; d < dims; d++) v[d] = center[d] + (float)(r.NextDouble() * 0.3 - 0.15);
                    normalize(v);
                    reference[id] = v;
                    index.Add(id++, v);
                }
            }
            index.SaveStateForMemoryIndexes(1, walId);
            Assert.AreEqual(centers * perCenter, index.Count);
            foreach (var probe in new[] { 555, 1234, 8000 }) {
                var query = reference[probe];
                var hits = index.Search(query, 0, 10, 0);
                Assert.AreEqual(probe, hits[0].NodeId, "a vector must find itself first");
                Assert.IsTrue(hits[0].Similarity > 0.999f);
                var exact = bruteForceTopK(reference, query, 10);
                var overlap = hits.Select(h => h.NodeId).Intersect(exact).Count();
                Assert.IsTrue(overlap >= 8, $"recall@10 too low for {probe}: {overlap}");
            }
            // the unranked filter path: everything above a threshold, found by widening the beam until
            // it reaches past it. The threshold is taken from the data — the 50th nearest neighbour —
            // so the expected set is a real one whatever the generated similarities come out at.
            var filterQuery = reference[555];
            var similarities = reference.Values.Select(v => dot(v, filterQuery)).OrderByDescending(s => s).ToArray();
            var threshold = similarities[49];
            var expected = reference.Where(kv => dot(kv.Value, filterQuery) >= threshold).Select(kv => kv.Key).ToHashSet();
            Assert.IsTrue(expected.Count >= 50, "the test threshold should select a real set");
            var found = index.SearchAbove(filterQuery, threshold);
            var filterRecall = expected.Count(found.Contains) / (double)expected.Count;
            Assert.IsTrue(filterRecall >= 0.9, "filter recall too low: " + filterRecall);
            Assert.IsTrue(found.All(id => dot(reference[id], filterQuery) >= threshold - 0.0001f), "a filter hit was below the threshold");
        }
        using (var index = create(_folder, options)) { // the graph survives a reopen
            index.ReadStateForMemoryIndexes(walId);
            Assert.AreEqual(centers * perCenter, index.Count);
            Assert.AreEqual(555, index.Search(reference[555], 0, 1, 0)[0].NodeId);
            // and adds after the reopen link into the loaded graph
            var extra = randomUnit(r, dims);
            index.Add(99999, extra);
            index.SaveStateForMemoryIndexes(2, walId);
            Assert.AreEqual(99999, index.Search(extra, 0, 1, 0)[0].NodeId);
        }
    }

    [TestMethod]
    public void MakeDurablePersistsOnlyTheChanges() {
        var r = new Random(21);
        const int dims = 32;
        var walId = Guid.NewGuid();
        using (var index = create(_folder)) {
            index.ReadStateForMemoryIndexes(walId);
            for (var id = 1; id <= 2000; id++) index.Add(id, randomUnit(r, dims));
            index.MakeDurable(10); // the WAL-flush hook: writes the changed records and stamps the manifest
            Assert.AreEqual(10, index.PersistedTimestamp);
            var sizeAfterFirst = index.GetTotalDiskSize();
            Assert.IsTrue(sizeAfterFirst > 0);
            index.MakeDurable(11); // clean index: only the manifest stamp advances
            Assert.AreEqual(11, index.PersistedTimestamp);
            index.MakeDurable(11); // clean and current: a complete no-op
            for (var id = 2001; id <= 2020; id++) index.Add(id, randomUnit(r, dims)); // small delta
            index.MakeDurable(12);
            Assert.AreEqual(12, index.PersistedTimestamp);
            var delta = index.GetTotalDiskSize() - sizeAfterFirst;
            Assert.IsTrue(delta < sizeAfterFirst / 2, "a small delta must not rewrite the index (grew " + delta + " bytes)");
        }
        using (var index = create(_folder)) { // everything survives a reopen at the stamped position
            index.ReadStateForMemoryIndexes(walId);
            Assert.AreEqual(12, index.PersistedTimestamp);
            Assert.AreEqual(2020, index.Count);
        }
    }

    [TestMethod]
    public void EdgeLogCarriesGraphChangesBetweenStateSaves() {
        // A WAL-flush checkpoint appends the changed neighbour lists to the edge log instead of writing
        // them in place, so the routing file is deliberately behind between state saves. What must hold
        // is that a reopen sees the same graph either way — through the log's replay before a state
        // save, and out of the routing file after one.
        var r = new Random(4242);
        const int dims = 64, count = 10_000;
        var options = new Hnsw2VectorIndexOptions { MinVectorsForGraphSearch = 1 };
        var walId = Guid.NewGuid();
        var reference = new Dictionary<int, float[]>();
        using (var index = create(_folder, options)) {
            index.ReadStateForMemoryIndexes(walId);
            for (var id = 1; id <= count / 2; id++) {
                var v = randomUnit(r, dims);
                reference[id] = v;
                index.Add(id, v);
            }
            index.MakeDurable(9); // a pure bulk load only appends, so nothing needs the log yet
            Assert.AreEqual(0, logBytes() - 64);
            for (var id = count / 2 + 1; id <= count; id++) { // these link into records already written
                var v = randomUnit(r, dims);
                reference[id] = v;
                index.Add(id, v);
            }
            index.MakeDurable(10);
            Assert.AreEqual(10, index.PersistedTimestamp);
        }
        Assert.IsTrue(logBytes() > 64, "the WAL-flush checkpoint should have written edges to the log");
        using (var index = create(_folder, options)) {
            index.ReadStateForMemoryIndexes(walId);
            Assert.AreEqual(count, index.Count);
            index.ClearCache(); // in cached mode every record now comes off the disk; resident mode replayed the log at open
            assertFindsItself(index, reference, 5000);
            index.SaveStateForMemoryIndexes(11, walId); // the full checkpoint applies the log and drops it
        }
        Assert.IsTrue(logBytes() <= 64, "a state save should have applied and dropped the log");
        using (var index = create(_folder, options)) {
            index.ReadStateForMemoryIndexes(walId);
            Assert.AreEqual(count, index.Count);
            assertFindsItself(index, reference, 5000);
        }
        // and a log torn by a crash is survivable like any other file
        using (var index = create(_folder, options)) {
            index.ReadStateForMemoryIndexes(walId);
            index.Add(count + 1, randomUnit(r, dims));
            index.MakeDurable(12);
        }
        using (var fs = new FileStream(Directory.GetFiles(_folder, "edges_*.log").Single(), FileMode.Open, FileAccess.ReadWrite)) {
            fs.SetLength(70); // a header and a fragment of one entry
        }
        using (var index = create(_folder, options)) {
            index.ReadStateForMemoryIndexes(walId); // must not throw
            Assert.AreEqual(0, index.PersistedTimestamp);
        }

        long logBytes() => new FileInfo(Directory.GetFiles(_folder, "edges_*.log").Single()).Length;

        void assertFindsItself(Hnsw2VectorIndex index, Dictionary<int, float[]> vectors, int probe) {
            var hits = index.Search(vectors[probe], 0, 10, 0);
            Assert.AreEqual(probe, hits[0].NodeId, "a vector must find itself, so the graph is intact");
            var overlap = hits.Select(h => h.NodeId).Intersect(bruteForceTopK(vectors, vectors[probe], 10)).Count();
            Assert.IsTrue(overlap >= 8, "recall@10 too low, the graph lost edges: " + overlap);
        }
    }

    [TestMethod]
    public void SequentialBuildIsDeterministic() {
        // Sequential adds draw their levels from a fixed seed and link one at a time, so building the
        // same vectors twice must produce byte-identical files; a difference means a hidden source of
        // nondeterminism (or a race) somewhere in the write path.
        var dims = 48;
        var count = 4_000;
        var walId = Guid.NewGuid();
        var vectors = new float[count][];
        var r = new Random(8080);
        for (var i = 0; i < count; i++) vectors[i] = randomUnit(r, dims);

        var first = build(Path.Combine(_folder, "a"));
        var second = build(Path.Combine(_folder, "b"));
        Assert.AreEqual(first.Count, second.Count);
        foreach (var name in first.Keys) {
            CollectionAssert.AreEqual(first[name], second[name], $"{name} differs between two identical builds");
        }

        Dictionary<string, byte[]> build(string folder) {
            Directory.CreateDirectory(folder);
            using (var index = create(folder, new Hnsw2VectorIndexOptions { MinVectorsForGraphSearch = 1 })) {
                index.ReadStateForMemoryIndexes(walId);
                for (var id = 1; id <= count; id++) index.Add(id, vectors[id - 1]);
                index.SaveStateForMemoryIndexes(1, walId);
            }
            var files = new Dictionary<string, byte[]>();
            foreach (var path in Directory.GetFiles(folder)) {
                if (Path.GetFileName(path) == "manifest.bin") continue; // carries the WAL id, not the graph
                files[Path.GetFileName(path)] = File.ReadAllBytes(path);
            }
            return files;
        }
    }

    [TestMethod]
    public void EdgeLogSurvivesEviction() {
        // The awkward combination: a cache too small to hold the index, and edges that live in the log
        // rather than in the routing file. Evicting such a record would throw away the only correct copy
        // of its neighbour list, so the eviction has to keep the list — and the state save afterwards has
        // to find it there rather than in the record it no longer has.
        var r = new Random(31337);
        const int dims = 64, count = 8_000;
        var options = new Hnsw2VectorIndexOptions {
            LowMemoryMode = true,
            MinVectorsForGraphSearch = 1,
            MaxMemoryBytes = 700 * 1024, // a cache budget of a fraction of the index
            EfConstruction = 32,         // a cheaper graph; this test is about the edges surviving, not their quality
        };
        var walId = Guid.NewGuid();
        var reference = new Dictionary<int, float[]>();
        using (var index = create(_folder, options)) {
            index.ReadStateForMemoryIndexes(walId);
            for (var id = 1; id <= count; id++) {
                var v = randomUnit(r, dims);
                reference[id] = v;
                index.Add(id, v);
                if (id % (count / 4) == 0) index.MakeDurable(id); // edges to the log, four times over
            }
            index.MakeDurable(count);
            assertFindsItself(index, reference, 4000);
            index.SaveStateForMemoryIndexes(count + 1, walId); // consolidates lists it no longer holds
        }
        using (var index = create(_folder, options)) {
            index.ReadStateForMemoryIndexes(walId);
            Assert.AreEqual(count, index.Count);
            assertFindsItself(index, reference, 4000);
            assertFindsItself(index, reference, 7999);
        }

        void assertFindsItself(Hnsw2VectorIndex index, Dictionary<int, float[]> vectors, int probe) {
            var hits = index.Search(vectors[probe], 0, 10, 0);
            Assert.AreEqual(probe, hits[0].NodeId, "a vector must find itself, so the graph is intact");
            var overlap = hits.Select(h => h.NodeId).Intersect(bruteForceTopK(vectors, vectors[probe], 10)).Count();
            Assert.IsTrue(overlap >= 8, "recall@10 too low, the graph lost edges: " + overlap);
        }
    }

    [TestMethod]
    public void SpillsToDiskDuringBulkLoad() {
        var r = new Random(5);
        const int dims = 16, count = 500;
        var options = new Hnsw2VectorIndexOptions { MemTableFlushThresholdBytes = 4096 }; // spill constantly
        var walId = Guid.NewGuid();
        var reference = new Dictionary<int, float[]>();
        using (var index = create(_folder, options)) {
            index.ReadStateForMemoryIndexes(walId);
            for (var id = 1; id <= count; id++) {
                var v = randomUnit(r, dims);
                reference[id] = v;
                index.Add(id, v);
            }
            Assert.AreEqual(count, index.Count);
            // spans records already written and records still only in memory
            var hits = index.Search(reference[250], 0, 10, 0);
            Assert.AreEqual(250, hits[0].NodeId);
            CollectionAssert.AreEqual(bruteForceTopK(reference, reference[250], 10), hits.Select(h => h.NodeId).ToList());
            index.SaveStateForMemoryIndexes(1, walId);
        }
        using (var index = create(_folder, options)) {
            index.ReadStateForMemoryIndexes(walId);
            Assert.AreEqual(count, index.Count);
            CollectionAssert.AreEqual(bruteForceTopK(reference, reference[250], 10),
                index.Search(reference[250], 0, 10, 0).Select(h => h.NodeId).ToList());
        }
    }

    [TestMethod]
    public void CompactionReclaimsDeletedRecords() {
        var r = new Random(1234);
        const int dims = 32, count = 400;
        var options = new Hnsw2VectorIndexOptions { CompactionMinDeadRecords = 10, CompactionDeadFraction = 0.2f };
        var walId = Guid.NewGuid();
        var reference = new Dictionary<int, float[]>();
        using var index = create(_folder, options);
        index.ReadStateForMemoryIndexes(walId);
        for (var id = 1; id <= count; id++) {
            var v = randomUnit(r, dims);
            reference[id] = v;
            index.Add(id, v);
        }
        index.SaveStateForMemoryIndexes(1, walId);
        var full = index.GetTotalDiskSize();
        for (var id = 1; id <= count / 2; id++) { // half the index deleted: past both thresholds
            index.Remove(id, null!);
            reference.Remove(id);
        }
        index.SaveStateForMemoryIndexes(2, walId); // the state save compacts
        Assert.AreEqual(count / 2, index.Count);
        Assert.IsTrue(index.GetTotalDiskSize() < full * 0.75, "compaction did not reclaim the deleted records");
        Assert.AreEqual(1, Directory.GetFiles(_folder, "routing_*.bin").Length, "the replaced generation was not deleted");
        // and the surviving half still answers correctly
        var query = reference.Keys.First();
        CollectionAssert.AreEqual(bruteForceTopK(reference, reference[query], 10),
            index.Search(reference[query], 0, 10, 0).Select(h => h.NodeId).ToList());
    }

    [TestMethod]
    public void CorruptFilesResetInsteadOfCrashing() {
        var r = new Random(11);
        var walId = Guid.NewGuid();
        using (var index = create(_folder)) {
            index.ReadStateForMemoryIndexes(walId);
            for (var id = 1; id <= 100; id++) index.Add(id, randomUnit(r, 24));
            index.SaveStateForMemoryIndexes(1, walId);
        }
        var routing = Directory.GetFiles(_folder, "routing_*.bin").Single();
        using (var fs = new FileStream(routing, FileMode.Open, FileAccess.ReadWrite)) {
            fs.SetLength(fs.Length / 2); // torn file, as after a crash mid-write
        }
        using (var index = create(_folder)) {
            index.ReadStateForMemoryIndexes(walId); // must not throw
            Assert.AreEqual(0, index.PersistedTimestamp); // reset: the loader replays the whole WAL
            Assert.AreEqual(0, index.Count);
            index.Add(1, randomUnit(r, 24)); // and the index is usable again
            Assert.AreEqual(1, index.Search(randomUnit(r, 24), 0, 1, -1).Count);
        }
        // a corrupt manifest is equally survivable
        File.WriteAllBytes(Path.Combine(_folder, "manifest.bin"), new byte[17]);
        using (var index = create(_folder)) {
            index.ReadStateForMemoryIndexes(walId);
            Assert.AreEqual(0, index.PersistedTimestamp);
        }
    }

    [TestMethod]
    public void GraphLayoutChangeForcesRebuild() {
        // The graph degree decides the record layout, so a configuration change cannot be read: the
        // index resets to position 0 and the store replays the log into a graph of the new shape.
        var r = new Random(77);
        var walId = Guid.NewGuid();
        using (var index = create(_folder, new Hnsw2VectorIndexOptions { Connectivity = 16 })) {
            index.ReadStateForMemoryIndexes(walId);
            for (var id = 1; id <= 100; id++) index.Add(id, randomUnit(r, 24));
            index.SaveStateForMemoryIndexes(1, walId);
        }
        using (var index = create(_folder, new Hnsw2VectorIndexOptions { Connectivity = 32 })) {
            index.ReadStateForMemoryIndexes(walId);
            Assert.AreEqual(0, index.PersistedTimestamp);
            Assert.AreEqual(0, index.Count);
        }
        // the same settings as the write, on the other hand, must open cleanly
        using (var index = create(_folder, new Hnsw2VectorIndexOptions { Connectivity = 32 })) {
            index.ReadStateForMemoryIndexes(walId);
            for (var id = 1; id <= 100; id++) index.Add(id, randomUnit(r, 24));
            index.SaveStateForMemoryIndexes(2, walId);
        }
        using (var index = create(_folder, new Hnsw2VectorIndexOptions { Connectivity = 32 })) {
            index.ReadStateForMemoryIndexes(walId);
            Assert.AreEqual(2, index.PersistedTimestamp);
            Assert.AreEqual(100, index.Count);
        }
    }

    [TestMethod]
    public void TinyBudgetStillCorrect() {
        var r = new Random(3);
        const int dims = 64, count = 1000;
        // Next to nothing stays in memory: every hop goes through a tiny cache, every re-score to the file.
        var options = new Hnsw2VectorIndexOptions { LowMemoryMode = true, MaxMemoryBytes = 64 * 1024, MinVectorsForGraphSearch = 1 };
        var walId = Guid.NewGuid();
        var reference = new Dictionary<int, float[]>();
        using var index = create(_folder, options);
        index.ReadStateForMemoryIndexes(walId);
        for (var id = 1; id <= count; id++) {
            var v = randomUnit(r, dims);
            reference[id] = v;
            index.Add(id, v);
        }
        index.SaveStateForMemoryIndexes(1, walId);
        var cold = index.Search(reference[10], 0, 10, 0);
        Assert.AreEqual(10, cold.Count);
        Assert.AreEqual(10, cold[0].NodeId);
        index.MaxMemoryBytes = 64L * 1024 * 1024; // adjustable at runtime
        CollectionAssert.AreEqual(bruteForceTopK(reference, reference[10], 10),
            index.Search(reference[10], 0, 10, 0).Select(h => h.NodeId).ToList());
    }

    [TestMethod]
    public void QuantizedTierServesFloatsFromDisk() {
        // A budget with room for the routing graph but not the float vectors: the walk runs in
        // memory and the exact re-scoring reads the vector file. Scores must stay full precision,
        // and the tier choice must survive a reopen.
        var r = new Random(606);
        const int dims = 96, count = 4_000;
        var options = new Hnsw2VectorIndexOptions {
            MinVectorsForGraphSearch = 1,
            // graph core is ~250 B/vector here (~1 MB total), floats are ~384 B/vector (~1.5 MB)
            MaxMemoryBytes = 1_400 * 1024,
        };
        var walId = Guid.NewGuid();
        var reference = new Dictionary<int, float[]>();
        using (var index = create(_folder, options)) {
            index.ReadStateForMemoryIndexes(walId);
            for (var id = 1; id <= count; id++) {
                var v = randomUnit(r, dims);
                reference[id] = v;
                index.Add(id, v);
            }
            index.SaveStateForMemoryIndexes(1, walId);
            assertRecall(index, 2000);
        }
        using (var index = create(_folder, options)) {
            index.ReadStateForMemoryIndexes(walId);
            Assert.AreEqual(count, index.Count);
            assertRecall(index, 123);
            assertRecall(index, 3999);
        }

        void assertRecall(Hnsw2VectorIndex index, int probe) {
            var query = reference[probe];
            var hits = index.Search(query, 0, 10, 0);
            Assert.AreEqual(probe, hits[0].NodeId, "a vector must find itself first");
            Assert.IsTrue(hits[0].Similarity > 0.999f, "the returned score must be the exact float score");
            var overlap = hits.Select(h => h.NodeId).Intersect(bruteForceTopK(reference, query, 10)).Count();
            Assert.IsTrue(overlap >= 8, $"recall@10 too low for {probe}: {overlap}");
        }
    }

    [TestMethod]
    public void SingleThreadOptionStillCorrect() {
        // MaxThreads = 1 forces every parallel fan-out — batch build, scans, re-scoring reads —
        // down its sequential path; the answers must be the same.
        var r = new Random(52);
        const int dims = 32, count = 3_000;
        var options = new Hnsw2VectorIndexOptions { MaxThreads = 1, MinVectorsForGraphSearch = 1 };
        var walId = Guid.NewGuid();
        var reference = new Dictionary<int, float[]>();
        var items = new List<(int nodeId, float[] vector)>();
        for (var id = 1; id <= count; id++) {
            var v = randomUnit(r, dims);
            reference[id] = v;
            items.Add((id, v));
        }
        using var index = create(_folder, options);
        index.ReadStateForMemoryIndexes(walId);
        index.AddRange(items);
        Assert.AreEqual(count, index.Count);
        var hits = index.Search(reference[1500], 0, 10, 0);
        Assert.AreEqual(1500, hits[0].NodeId);
        Assert.IsTrue(hits[0].Similarity > 0.999f);
        Assert.AreEqual(count, index.Search(reference[1500], 0, int.MaxValue, -1).Count); // exact scan path
    }

    [TestMethod]
    public void LowMemoryModeStillCorrect() {
        // The whole low-memory surface in one pass: upper layers read from the file per hop, a
        // record cache that tracks entries instead of ordinals (and is far too small to hold the
        // index), constant spilling, the edge log, and a reopen. Correctness must be unchanged.
        var r = new Random(777);
        const int dims = 64, centers = 60, perCenter = 100;
        const int count = centers * perCenter;
        var options = new Hnsw2VectorIndexOptions {
            LowMemoryMode = true,
            MinVectorsForGraphSearch = 1,
            MaxMemoryBytes = 400 * 1024,             // a fraction of the index: eviction runs constantly
            MemTableFlushThresholdBytes = 64 * 1024, // and the memtable spills constantly
        };
        var walId = Guid.NewGuid();
        var reference = new Dictionary<int, float[]>();
        using (var index = create(_folder, options)) {
            index.ReadStateForMemoryIndexes(walId);
            var id = 1;
            for (var c = 0; c < centers; c++) { // clustered, like real embeddings
                var center = randomUnit(r, dims);
                for (var i = 0; i < perCenter; i++) {
                    var v = new float[dims];
                    for (var d = 0; d < dims; d++) v[d] = center[d] + (float)(r.NextDouble() * 0.3 - 0.15);
                    normalize(v);
                    reference[id] = v;
                    index.Add(id++, v);
                    if (id % 2000 == 0) index.MakeDurable(id); // the cheap checkpoint, mid-load
                }
            }
            assertRecall(index, 1500);
            index.SaveStateForMemoryIndexes(count + 1, walId);
        }
        using (var index = create(_folder, options)) {
            index.ReadStateForMemoryIndexes(walId);
            Assert.AreEqual(count, index.Count);
            assertRecall(index, 4321);
            for (var id = 1; id <= 200; id++) { // updates and removes against the on-disk graph
                var v = randomUnit(r, dims);
                reference[id] = v;
                index.Add(id, v);
            }
            for (var id = 201; id <= 300; id++) {
                index.Remove(id, null!);
                reference.Remove(id);
            }
            index.SaveStateForMemoryIndexes(count + 2, walId);
            Assert.AreEqual(count - 100, index.Count);
            var hits = index.Search(reference[1], 0, 10, 0);
            Assert.AreEqual(1, hits[0].NodeId, "an updated vector must be found under its id");
            Assert.IsTrue(hits[0].Similarity > 0.999f);
            for (var id = 201; id <= 300; id++) Assert.IsFalse(hits.Any(h => h.NodeId == id), "a removed id resurfaced");
        }

        void assertRecall(Hnsw2VectorIndex index, int probe) {
            var query = reference[probe];
            var hits = index.Search(query, 0, 10, 0);
            Assert.AreEqual(probe, hits[0].NodeId, "a vector must find itself first");
            Assert.IsTrue(hits[0].Similarity > 0.999f);
            var overlap = hits.Select(h => h.NodeId).Intersect(bruteForceTopK(reference, query, 10)).Count();
            Assert.IsTrue(overlap >= 8, $"recall@10 too low for {probe}: {overlap}");
        }
    }

    [TestMethod]
    public void LowMemoryAndNormalModeShareFiles() {
        // The mode changes residency, never the files: an index written in one mode must open in
        // the other at the same durable position, and writes made in either mode survive the swap.
        var r = new Random(4040);
        const int dims = 48, count = 1200;
        var walId = Guid.NewGuid();
        var reference = new Dictionary<int, float[]>();
        var normal = new Hnsw2VectorIndexOptions { MinVectorsForGraphSearch = 1 };
        var lowMem = new Hnsw2VectorIndexOptions { LowMemoryMode = true, MinVectorsForGraphSearch = 1 };
        using (var index = create(_folder, normal)) { // written by the normal mode
            index.ReadStateForMemoryIndexes(walId);
            for (var id = 1; id <= count; id++) {
                var v = randomUnit(r, dims);
                reference[id] = v;
                index.Add(id, v);
            }
            index.SaveStateForMemoryIndexes(1, walId);
        }
        using (var index = create(_folder, lowMem)) { // opened by the low-memory mode: no reset
            index.ReadStateForMemoryIndexes(walId);
            Assert.AreEqual(1, index.PersistedTimestamp, "a mode switch must not reset the index");
            Assert.AreEqual(count, index.Count);
            Assert.AreEqual(500, index.Search(reference[500], 0, 1, 0)[0].NodeId);
            for (var id = count + 1; id <= count + 50; id++) { // and writes link into the loaded graph
                var v = randomUnit(r, dims);
                reference[id] = v;
                index.Add(id, v);
            }
            index.SaveStateForMemoryIndexes(2, walId);
        }
        using (var index = create(_folder, normal)) { // and back again
            index.ReadStateForMemoryIndexes(walId);
            Assert.AreEqual(2, index.PersistedTimestamp);
            Assert.AreEqual(count + 50, index.Count);
            Assert.AreEqual(count + 25, index.Search(reference[count + 25], 0, 1, 0)[0].NodeId);
        }
    }

    [TestMethod]
    public void AddRemoveChurnKeepsIdentityConsistent() {
        // Hammers the id → ordinal map — adds, replacements, removes and re-adds under colliding
        // random ids — and verifies the index's whole answer set against a reference dictionary.
        var r = new Random(90210);
        const int dims = 16;
        using var index = create(_folder);
        var reference = new Dictionary<int, float[]>();
        for (var round = 0; round < 8; round++) {
            for (var i = 0; i < 400; i++) { // add or replace
                var id = r.Next(1, 1500);
                var v = randomUnit(r, dims);
                reference[id] = v;
                index.Add(id, v);
            }
            for (var i = 0; i < 150; i++) { // remove; some ids miss, which must be harmless
                var id = r.Next(1, 1500);
                reference.Remove(id);
                index.Remove(id, null!);
            }
            Assert.AreEqual(reference.Count, index.Count, "count diverged in round " + round);
        }
        var all = index.Search(reference.Values.First(), 0, int.MaxValue, -1); // exact: scans everything
        Assert.AreEqual(reference.Count, all.Count);
        var got = all.Select(h => h.NodeId).ToHashSet();
        foreach (var id in reference.Keys) Assert.IsTrue(got.Contains(id), "missing id " + id);
        foreach (var (id, v) in reference.Take(50)) { // every id answers with its newest vector
            var hit = index.Search(v, 0, 1, 0)[0];
            Assert.AreEqual(id, hit.NodeId);
            Assert.IsTrue(hit.Similarity > 0.999f);
        }
    }

    [TestMethod]
    public void AddRangeBuildsACorrectIndex() {
        // The parallel batch build must answer like the sequential one: same ids, replace-on-same-id
        // semantics (within a batch and across batches), empty vector as remove, comparable recall,
        // and a clean persistence round trip.
        var r = new Random(1717);
        const int dims = 64, centers = 50, perCenter = 100;
        const int count = centers * perCenter;
        var options = new Hnsw2VectorIndexOptions { MinVectorsForGraphSearch = 1 };
        var walId = Guid.NewGuid();
        var reference = new Dictionary<int, float[]>();
        var items = new List<(int nodeId, float[] vector)>();
        var id = 1;
        for (var c = 0; c < centers; c++) {
            var center = randomUnit(r, dims);
            for (var i = 0; i < perCenter; i++) {
                var v = new float[dims];
                for (var d = 0; d < dims; d++) v[d] = center[d] + (float)(r.NextDouble() * 0.3 - 0.15);
                normalize(v);
                reference[id] = v;
                items.Add((id++, v));
            }
        }
        using (var index = create(_folder, options)) {
            index.ReadStateForMemoryIndexes(walId);
            index.AddRange(items.Take(count / 2));
            index.AddRange(items.Skip(count / 2));
            Assert.AreEqual(count, index.Count);
            foreach (var probe in new[] { 111, 2500, 4999 }) assertRecall(index, probe);
            // one batch carrying replaces of existing ids, a duplicate id (last wins) and a remove
            var replacement = randomUnit(r, dims);
            reference[10] = replacement;
            reference[20] = replacement;
            reference.Remove(30);
            index.AddRange([(10, randomUnit(r, dims)), (10, replacement), (20, replacement), (30, [])]);
            Assert.AreEqual(count - 1, index.Count);
            var hit = index.Search(replacement, 0, 2, 0);
            CollectionAssert.AreEquivalent(new[] { 10, 20 }, hit.Select(h => h.NodeId).ToArray());
            Assert.IsTrue(hit[0].Similarity > 0.999f);
            index.SaveStateForMemoryIndexes(1, walId);
        }
        using (var index = create(_folder, options)) {
            index.ReadStateForMemoryIndexes(walId);
            Assert.AreEqual(count - 1, index.Count);
            assertRecall(index, 2500);
            Assert.IsFalse(index.Search(reference[2500], 0, int.MaxValue, -1).Any(h => h.NodeId == 30), "the removed id resurfaced");
        }

        void assertRecall(Hnsw2VectorIndex index, int probe) {
            var query = reference[probe];
            var hits = index.Search(query, 0, 10, 0);
            Assert.AreEqual(probe, hits[0].NodeId, "a vector must find itself first");
            Assert.IsTrue(hits[0].Similarity > 0.999f);
            var overlap = hits.Select(h => h.NodeId).Intersect(bruteForceTopK(reference, query, 10)).Count();
            Assert.IsTrue(overlap >= 8, $"recall@10 too low for {probe}: {overlap}");
        }
    }

    [TestMethod]
    public void AddRangeInLowMemoryMode() {
        // The batch build's workers against the on-disk graph: pending upper slots, the keyed record
        // cache and constant eviction, all at once.
        var r = new Random(818);
        const int dims = 48, count = 4000;
        var options = new Hnsw2VectorIndexOptions {
            LowMemoryMode = true,
            MinVectorsForGraphSearch = 1,
            MaxMemoryBytes = 400 * 1024,
            MemTableFlushThresholdBytes = 64 * 1024,
        };
        var walId = Guid.NewGuid();
        var reference = new Dictionary<int, float[]>();
        var items = new List<(int nodeId, float[] vector)>();
        for (var id = 1; id <= count; id++) {
            var v = randomUnit(r, dims);
            reference[id] = v;
            items.Add((id, v));
        }
        using (var index = create(_folder, options)) {
            index.ReadStateForMemoryIndexes(walId);
            index.AddRange(items);
            Assert.AreEqual(count, index.Count);
            var hits = index.Search(reference[2000], 0, 1, 0);
            Assert.AreEqual(2000, hits[0].NodeId);
            Assert.IsTrue(hits[0].Similarity > 0.999f);
            index.SaveStateForMemoryIndexes(1, walId);
        }
        using (var index = create(_folder, options)) {
            index.ReadStateForMemoryIndexes(walId);
            Assert.AreEqual(count, index.Count);
            Assert.AreEqual(123, index.Search(reference[123], 0, 1, 0)[0].NodeId);
        }
    }

    [TestMethod]
    public void StateLoadBufferingKeepsReplaySemantics() {
        // RegisterAdd/RemoveDuringStateLoad buffer and batch; what must hold is that per id the last
        // replayed op wins, and that every other operation sees the buffered state as if it had been
        // applied immediately.
        var r = new Random(929);
        const int dims = 32, count = 3000;
        var walId = Guid.NewGuid();
        var reference = new Dictionary<int, float[]>();
        using (var index = create(_folder)) {
            index.ReadStateForMemoryIndexes(walId);
            for (var id = 1; id <= count; id++) {
                var v = randomUnit(r, dims);
                reference[id] = v;
                index.RegisterAddDuringStateLoad(id, v);
            }
            for (var id = 1; id <= 100; id++) { // replayed updates: later op per id wins
                var v = randomUnit(r, dims);
                reference[id] = v;
                index.RegisterAddDuringStateLoad(id, v);
            }
            for (var id = 101; id <= 150; id++) { // replayed removes
                index.RegisterRemoveDuringStateLoad(id, null!);
                reference.Remove(id);
            }
            Assert.AreEqual(count - 50, index.Count); // Count drains the buffer
            var hits = index.Search(reference[50], 0, 1, 0);
            Assert.AreEqual(50, hits[0].NodeId, "an updated id must answer with its newest vector");
            Assert.IsTrue(hits[0].Similarity > 0.999f);
            index.SaveStateForMemoryIndexes(1, walId);
        }
        using (var index = create(_folder)) {
            index.ReadStateForMemoryIndexes(walId);
            Assert.AreEqual(count - 50, index.Count);
            var all = index.Search(reference[50], 0, int.MaxValue, -1).Select(h => h.NodeId).ToHashSet();
            for (var id = 101; id <= 150; id++) Assert.IsFalse(all.Contains(id), "a removed id resurfaced: " + id);
            Assert.AreEqual(reference.Count, all.Count);
        }
    }

    [TestMethod]
    public void TextSearchThroughAiEngine() {
        var ai = AIEngine.CreateDummy(); // deterministic normalized 1536-dim embeddings
        using var index = create(_folder, null, ai);
        var embeddings = ai.GetEmbeddingsAsync(["red sports car", "green apple pie"]).Result;
        index.Add(1, embeddings[0]);
        index.Add(2, embeddings[1]);
        var ids = index.SearchForIdSetUnranked("red sports car", 0.99f);
        Assert.IsTrue(ids.Has(1));
        Assert.IsFalse(ids.Has(2));
        var hits = index.SearchForHitData("green apple pie", 5, 100, -1, out var totalHits);
        Assert.AreEqual(2, totalHits);
        Assert.AreEqual(2, hits[0].NodeId);
        Assert.IsTrue(hits[0].Score > 0.999f);
    }
}
