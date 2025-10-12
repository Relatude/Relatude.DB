using Relatude.DB.IO;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
namespace Relatude.DB.DataStores.Indexes.VectorIndex;
/// <summary>
/// Memorybased and brute force vector index for medium sized data sets.
/// Since all vectors are normalized we can use "dot product" as a similarity measure.
/// </summary>
public class FlatMemoryVectorIndex : IVectorIndex {
    readonly Dictionary<int, float[]> _index = [];
    bool multiThreaded {
        get {
            return Environment.ProcessorCount > 2 && _index.Count > 10000;
        }
    }
    public void Set(int nodeId, float[] vector) => _index[nodeId] = vector;
    public void Clear(int nodeId) => _index.Remove(nodeId);


    public List<VectorHit> Search(float[] u, int skip, int take, float minCosineSimilarity) {
        if (minCosineSimilarity >= 1f) minCosineSimilarity = 0.9999f;
        else if (minCosineSimilarity <= -1f) minCosineSimilarity = -0.9999f; // avoid precision issues
#if DEBUG
        var result1 = SearchOld(u, skip, take, minCosineSimilarity);
        var result2 = SearchNew(u, skip, take, minCosineSimilarity);
        if (result1.Count != result2.Count) throw new Exception("Search result count mismatch");
        foreach (var (r1, r2) in result1.Zip(result2)) {
            if (r1.NodeId != r2.NodeId) throw new Exception("Search result NodeId mismatch");
            var denominator = Math.Abs(r1.Similarity) + Math.Abs(r2.Similarity);
            if (denominator < 0.0001f) continue; // both are zero
            var percentageDiff = Math.Abs(r1.Similarity - r2.Similarity) / (denominator / 2);
            if (percentageDiff > 0.001f) throw new Exception("Search result Similarity mismatch");
        }
        return result1;
#else
        return SearchNew(u, skip, take, minCosineSimilarity);
#endif
    }

    #region Old search (without SIMD and top-k optimization)
    public List<VectorHit> SearchOld(float[] u, int skip, int take, float minCosineSimilarity) {
        var hits = multiThreaded ? multi(u, minCosineSimilarity) : single(u, minCosineSimilarity);
        return hits.OrderByDescending(h => h.Similarity).Skip(skip).Take(take).ToList();
    }
    List<VectorHit> multi(float[] u, float minCosineSimilarity) {
        var hits = new List<VectorHit>();
        void search(KeyValuePair<int, float[]> kv) {
            float similarity = 0;
            var v = kv.Value;
            for (var i = 0; i < u.Length; i++) similarity += u[i] * v[i];
            if (similarity >= minCosineSimilarity) {
                lock (hits) hits.Add(new(kv.Key, similarity));
            }
        }
        Parallel.ForEach(_index, search);
        return hits;
    }
    List<VectorHit> single(float[] u, float minCosineSimilarity) {
        var hits = new List<VectorHit>();
        foreach (var kv in _index) {
            float similarity = 0;
            var v = kv.Value;
            if (v.Length != u.Length) continue; // skip if dimensions do not match
            for (var i = 0; i < u.Length; i++) similarity += u[i] * v[i];
            if (similarity >= minCosineSimilarity) hits.Add(new(kv.Key, similarity));
        }
        return hits;
    }
    #endregion

    #region New search with SIMD and top-k optimization
    List<VectorHit> SearchNew(float[] u, int skip, int take, float minCosineSimilarity) {
        int k = Math.Max(0, skip) + Math.Max(0, take);
        if (k == 0) k = 32;

        // Snapshot keys/values once to get a contiguous array for fast Parallel.For
        // (also avoids enumeration invalidation if the index changes concurrently)
        var snapshot = _index.ToArray();

        if (multiThreaded) {
            // Thread-local top-k; merge at the end.
            var merged = new ConcurrentBag<(int id, float sim)>();

            Parallel.ForEach(
                source: Partitioner.Create(0, snapshot.Length),
                localInit: () => new PriorityQueue<(int id, float sim), float>(),
                body: (range, state, localPQ) => {
                    var uSpan = u.AsSpan();
                    for (int i = range.Item1; i < range.Item2; i++) {
                        var (id, v) = snapshot[i];
                        if (v == null || v.Length != u.Length) continue;

                        float sim = dotSimd(uSpan, v);
                        if (sim >= minCosineSimilarity)
                            topKPush(localPQ, (id, sim), sim, k);
                    }
                    return localPQ;
                },
                localFinally: localPQ => {
                    while (localPQ.Count > 0) merged.Add(localPQ.Dequeue());
                });

            // Final ordering and paging
            var list = merged.ToList();
            list.Sort((a, b) => b.sim.CompareTo(a.sim));
            return list.Skip(skip).Take(take).Select(x => new VectorHit(x.id, x.sim)).ToList();
        } else {
            var pq = new PriorityQueue<(int id, float sim), float>();
            var uSpan = u.AsSpan();

            for (int i = 0; i < snapshot.Length; i++) {
                var (id, v) = snapshot[i];
                if (v == null || v.Length != u.Length) continue;

                float sim = dotSimd(uSpan, v);
                if (sim >= minCosineSimilarity)
                    topKPush(pq, (id, sim), sim, k);
            }

            var tmp = new List<(int id, float sim)>(pq.Count);
            while (pq.Count > 0) tmp.Add(pq.Dequeue());
            tmp.Sort((a, b) => b.sim.CompareTo(a.sim));
            return tmp.Skip(skip).Take(take).Select(x => new VectorHit(x.id, x.sim)).ToList();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void topKPush(
        PriorityQueue<(int id, float sim), float> pq,
        (int id, float sim) item,
        float priority,
        int k) {
        if (pq.Count < k) {
            pq.Enqueue(item, priority);
        } else if (pq.TryPeek(out var peek, out float minPri) && priority > minPri) {
            pq.Dequeue();
            pq.Enqueue(item, priority);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float dotSimd(ReadOnlySpan<float> a, float[] bArr) {
        var b = bArr.AsSpan();
        int n = a.Length;
        float sum = 0f;
        if (Vector.IsHardwareAccelerated) {
            int w = Vector<float>.Count;
            int i = 0;
            for (; i <= n - w; i += w) {
                var va = new Vector<float>(a.Slice(i, w));
                var vb = new Vector<float>(b.Slice(i, w));
                sum += Vector.Dot(va, vb);
            }
            for (; i < n; i++) sum += a[i] * b[i];
        } else {
            for (int i = 0; i < n; i++) sum += a[i] * b[i];
        }
        return sum;
    }
    #endregion

    public void ReadState(IReadStream stream) {
        var nodeCount = stream.ReadVerifiedInt();
        for (var i = 0; i < nodeCount; i++) {
            var nodeId = (int)stream.ReadUInt();
            var vector = stream.ReadFloatArray();
            _index.Add(nodeId, vector);
        }
    }
    public void SaveState(IAppendStream stream) {
        stream.WriteVerifiedInt(_index.Count);
        foreach (var kv in _index) {
            stream.WriteUInt((uint)kv.Key);
            stream.WriteFloatArray(kv.Value);
        }
    }
    public void CompressMemory() { }
}
