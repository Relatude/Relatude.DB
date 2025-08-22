using WAF.IO;
namespace WAF.DataStores.Indexes.VectorIndex;
/// <summary>
/// Memorybased and brute force vector index for medium sized data sets.
/// Since all vectors are normalized we can use "dot product" as a similarity measure.
/// </summary>
public class FlatVectorIndex : IVectorIndex {
    readonly Dictionary<int, float[]> _index = [];
    readonly bool _multiThreaded = false;
    public void Set(int nodeId, float[] vector) => _index[nodeId] = vector;
    public void Clear(int nodeId) => _index.Remove(nodeId);
    public List<VectorHit> Search(float[] u, int skip, int take, float minCosineSimilarity) {
        var hits = _multiThreaded ? multi(u, minCosineSimilarity) : single(u, minCosineSimilarity);
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
