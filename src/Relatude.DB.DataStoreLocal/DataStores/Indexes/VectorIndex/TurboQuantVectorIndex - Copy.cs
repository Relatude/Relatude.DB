//using Relatude.DB.DataStores.Indexes.VectorIndex2;
//using Relatude.DB.IO;

//namespace Relatude.DB.DataStores.Indexes.VectorIndex;

//public class TurboQuantVectorIndex : IVectorIndex {
//    bool _multiThreaded;
//    readonly Dictionary<int, EncodedVector> _index = [];
//    TurboQuant? _turboQuant;
//    float[] padVector(float[] vector) {
//        if (vector.Length == _indexDimensions) return vector;
//        var padded = new float[_indexDimensions];
//        Array.Copy(vector, padded, vector.Length);
//        return padded;
//    }
//    int _indexDimensions = 1;
//    Action<string>? _log;
//    public TurboQuantVectorIndex(int modelDimensions, Action<string>? log = null) {
//        // TurboQuant requires dimensions to be a power of 2, so we round up to the next power of 2.
//        while (_indexDimensions < modelDimensions) _indexDimensions *= 2;
//        _multiThreaded = Environment.ProcessorCount > 2;
//        _log = log;
//    }
//    object _turboQuantLock = new();
//    TurboQuant getTurboQuant() {
//        lock (_turboQuantLock) {
//            if (_turboQuant == null) {
//                if (_log != null) _log("Training TURBO-QUANT " + _indexDimensions + " dimensions, need 30-60 secs...");
//                _turboQuant = TurboQuant.Create(_indexDimensions);
//                if (_log != null) _log("Completed training of TURBO-QUANT");
//            }
//            return _turboQuant;
//        }
//    }
//    public void Set(int nodeId, float[] vector) {
//        _index[nodeId] = getTurboQuant().Encode(padVector(vector));
//    }
//    public void Clear(int nodeId) {
//        _index.Remove(nodeId);
//    }
//    public List<VectorHit> Search(float[] u, int skip, int take, float minCosineSimilarity) {
//        var hits = _multiThreaded ? multi(u, minCosineSimilarity) : single(u, minCosineSimilarity);
//        return hits.OrderByDescending(h => h.Similarity).Skip(skip).Take(take).ToList();
//    }
//    List<VectorHit> multi(float[] u, float minCosineSimilarity) {
//        var hits = new List<VectorHit>();
//        var turboQuant = getTurboQuant();
//        var e = turboQuant.Encode(padVector(u));
//        void search(KeyValuePair<int, EncodedVector> kv) {
//            float similarity = turboQuant.ApproxDot(e, kv.Value);
//            if (similarity >= minCosineSimilarity) {
//                lock (hits) hits.Add(new(kv.Key, similarity));
//            }
//        }
//        Parallel.ForEach(_index, search);
//        return hits;
//    }
//    List<VectorHit> single(float[] u, float minCosineSimilarity) {
//        var hits = new List<VectorHit>();
//        var turboQuant = getTurboQuant();
//        var e = turboQuant.Encode(padVector(u));
//        foreach (var kv in _index) {
//            float similarity = turboQuant.ApproxDot(e, kv.Value);
//            if (similarity >= minCosineSimilarity) hits.Add(new(kv.Key, similarity));
//        }
//        return hits;
//    }
//    public void CompressMemory() {

//    }
//    public void ReadState(IReadStream stream) {        
//        _turboQuant = TurboQuant.FromByteArray(stream.ReadByteArray());
//        var indexDimensions = stream.ReadInt();
//        if (indexDimensions != _indexDimensions) throw new InvalidDataException($"Index dimensions mismatch. Expected {_indexDimensions}, got {indexDimensions}");
//        int count = stream.ReadInt();
//        for (int i = 0; i < count; i++) {
//            int nodeId = stream.ReadInt();
//            var encodedVector = EncodedVector.FromByteArray(stream.ReadByteArray());
//            _index[nodeId] = encodedVector;
//        }
//    }
//    public void SaveState(IAppendStream stream) {
//        var turboQuant = getTurboQuant();
//        var state = turboQuant.ToByteArray();
//        stream.WriteByteArray(state);
//        stream.WriteInt(_indexDimensions);
//        stream.WriteInt(_index.Count);
//        foreach (var kv in _index) {
//            stream.WriteInt(kv.Key);
//            stream.WriteByteArray(kv.Value.ToByteArray());
//        }
//    }

//}
