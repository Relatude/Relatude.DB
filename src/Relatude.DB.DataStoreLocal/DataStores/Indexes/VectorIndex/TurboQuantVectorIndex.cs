using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Indexes.VectorIndex;

public class TurboQuantVectorIndex : IVectorIndex {
    readonly Dictionary<int, EncodedVector> _index = [];
    readonly TurboQuant _turboQuant;
    public TurboQuantVectorIndex(int dimensions) {
        _turboQuant = TurboQuant.Create(dimensions);
    }
    public void Set(int nodeId, float[] vector) {
        _index[nodeId] = _turboQuant.Encode(vector, nodeId);
    }
    public void Clear(int nodeId) {
        _index.Remove(nodeId);
    }
    public List<VectorHit> Search(float[] u, int skip, int take, float minVectorDistance) {
        var hits = new List<VectorHit>();
        var e = _turboQuant.Encode(u, 0);
        foreach (var kv in _index) {
            float similarity = _turboQuant.ApproxDot(e, kv.Value);
            if (similarity >= minVectorDistance) hits.Add(new(kv.Key, similarity));
        }
        return hits;

    }
    public void CompressMemory() {
        // throw new NotImplementedException();
    }
    public void ReadState(IReadStream stream) {
        // throw new NotImplementedException();
    }
    public void SaveState(IAppendStream stream) {
        // throw new NotImplementedException();
    }

}
