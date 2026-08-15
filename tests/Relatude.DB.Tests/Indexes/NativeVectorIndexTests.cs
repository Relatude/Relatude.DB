using Relatude.DB.AI;
using Relatude.DB.AI.ISV;
using Relatude.DB.DataStores.Indexes.VectorIndex;
using Relatude.DB.DataStores.Sets;

namespace Relatude.Indexes;

[TestClass]
public class NativeVectorIndexTests {
    string _folder = "";
    [TestInitialize]
    public void Init() {
        _folder = Path.Combine(Path.GetTempPath(), "RelatudeDB_NativeVectorIndexTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }
    [TestCleanup]
    public void Cleanup() {
        try { Directory.Delete(_folder, true); } catch { }
    }
    static NativeVectorIndex create(string folder, NativeVectorIndexOptions? options = null, AIEngine? ai = null) {
        return new NativeVectorIndex(new SetRegister(10_000_000), "test-index", "Test Vector Index", folder, ai, options);
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
        var r = new Random(42);
        const int dims = 64, count = 2000;
        using var index = create(_folder);
        var reference = new Dictionary<int, float[]>();
        for (var id = 1; id <= count; id++) {
            var v = randomUnit(r, dims);
            reference[id] = v;
            index.Add(id, v);
        }
        Assert.AreEqual(count, index.Count);
        // querying an indexed vector must return it first with similarity ~1
        var hits = index.Search(reference[500], 0, 10, 0);
        Assert.AreEqual(10, hits.Count);
        Assert.AreEqual(500, hits[0].NodeId);
        Assert.IsTrue(hits[0].Similarity > 0.999f);
        // results are ordered best first and match a brute-force scan
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
    public void UpdateRemoveAndMerge() {
        var r = new Random(99);
        const int dims = 16;
        var walId = Guid.NewGuid();
        var updated = new Dictionary<int, float[]>();
        using (var index = create(_folder)) {
            index.ReadStateForMemoryIndexes(walId);
            for (var id = 1; id <= 100; id++) index.Add(id, randomUnit(r, dims));
            index.SaveStateForMemoryIndexes(1, walId); // segment 1
            for (var id = 1; id <= 50; id++) {
                var v = randomUnit(r, dims);
                updated[id] = v;
                index.Add(id, v); // overwrite
            }
            for (var id = 51; id <= 60; id++) index.Remove(id, null!);
            index.Add(61, Array.Empty<float>()); // an empty embedding clears the entry
            index.SaveStateForMemoryIndexes(2, walId); // segment 2 + ladder merge
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
    public void ClusteredSearchAndAccuracy() {
        var r = new Random(2024);
        const int dims = 32, centers = 40, perCenter = 100;
        var options = new NativeVectorIndexOptions {
            MinVectorsForClustering = 1000,
            TargetVectorsPerCluster = 64,
        };
        var walId = Guid.NewGuid();
        var reference = new Dictionary<int, float[]>();
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
            index.SaveStateForMemoryIndexes(1, walId); // triggers centroid training
            // accuracy 1 probes every cluster: identical to a brute-force scan
            var query = reference[555];
            var exact = index.Search(query, 0, 10, 0, accuracy: 1f);
            CollectionAssert.AreEqual(bruteForceTopK(reference, query, 10), exact.Select(h => h.NodeId).ToList());
            // reduced accuracy still finds the vector itself (its own cluster ranks first)
            var approx = index.Search(query, 0, 10, 0, accuracy: 0.25f);
            Assert.AreEqual(555, approx[0].NodeId);
            Assert.IsTrue(approx[0].Similarity > 0.999f);
            // and recall against the exact top 10 is high on clustered data
            var overlap = approx.Select(h => h.NodeId).Intersect(exact.Select(h => h.NodeId)).Count();
            Assert.IsTrue(overlap >= 8, "recall@10 too low: " + overlap);
        }
        using (var index = create(_folder, options)) { // clustered state survives a reopen
            index.ReadStateForMemoryIndexes(walId);
            Assert.AreEqual(centers * perCenter, index.Count);
            var hits = index.Search(reference[555], 0, 1, 0, accuracy: 0.25f);
            Assert.AreEqual(555, hits[0].NodeId);
            // adds after training are assigned to the existing clusters
            var extra = randomUnit(r, dims);
            index.Add(99999, extra);
            index.SaveStateForMemoryIndexes(2, walId);
            Assert.AreEqual(99999, index.Search(extra, 0, 1, 0, accuracy: 1f)[0].NodeId);
        }
    }

    [TestMethod]
    public void MakeDurablePersistsOnlyTheChanges() {
        var r = new Random(21);
        const int dims = 32;
        var walId = Guid.NewGuid();
        using (var index = create(_folder)) {
            index.ReadStateForMemoryIndexes(walId);
            for (var id = 1; id <= 100; id++) index.Add(id, randomUnit(r, dims));
            index.MakeDurable(10); // the WAL-flush hook: flushes the delta and stamps the manifest
            Assert.AreEqual(10, index.PersistedTimestamp);
            var sizeAfterFirst = index.GetTotalDiskSize();
            Assert.IsTrue(sizeAfterFirst > 0);
            index.MakeDurable(11); // clean index: only the manifest stamp advances
            Assert.AreEqual(11, index.PersistedTimestamp);
            index.MakeDurable(11); // clean and current: a complete no-op
            for (var id = 101; id <= 110; id++) index.Add(id, randomUnit(r, dims)); // small delta
            index.MakeDurable(12);
            Assert.AreEqual(12, index.PersistedTimestamp);
            var delta = index.GetTotalDiskSize() - sizeAfterFirst;
            Assert.IsTrue(delta < sizeAfterFirst / 2, "a small delta must not rewrite the index (grew " + delta + " bytes)");
        }
        using (var index = create(_folder)) { // everything survives a reopen at the stamped position
            index.ReadStateForMemoryIndexes(walId);
            Assert.AreEqual(12, index.PersistedTimestamp);
            Assert.AreEqual(110, index.Count);
        }
    }

    [TestMethod]
    public void SpillsToDiskDuringBulkLoad() {
        var r = new Random(5);
        const int dims = 16, count = 500;
        var options = new NativeVectorIndexOptions { MemTableFlushThresholdBytes = 4096 }; // spill every ~36 adds
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
            var hits = index.Search(reference[250], 0, 10, 0); // spans spilled segments and the memtable
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
    public void CorruptFilesResetInsteadOfCrashing() {
        var r = new Random(11);
        var walId = Guid.NewGuid();
        using (var index = create(_folder)) {
            index.ReadStateForMemoryIndexes(walId);
            for (var id = 1; id <= 100; id++) index.Add(id, randomUnit(r, 24));
            index.SaveStateForMemoryIndexes(1, walId);
        }
        var segment = Directory.GetFiles(_folder, "seg_*.bin").Single();
        using (var fs = new FileStream(segment, FileMode.Open, FileAccess.ReadWrite)) {
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
    public void TinyCacheStillCorrect() {
        var r = new Random(3);
        const int dims = 64, count = 1000;
        var options = new NativeVectorIndexOptions { MaxCacheBytes = 1024 }; // nothing fits: every search reads disk
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
        CollectionAssert.AreEqual(bruteForceTopK(reference, reference[10], 10),
            index.Search(reference[10], 0, 10, 0).Select(h => h.NodeId).ToList());
        index.MaxCacheBytes = 64L * 1024 * 1024; // adjustable at runtime
        CollectionAssert.AreEqual(bruteForceTopK(reference, reference[10], 10),
            index.Search(reference[10], 0, 10, 0).Select(h => h.NodeId).ToList());
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
