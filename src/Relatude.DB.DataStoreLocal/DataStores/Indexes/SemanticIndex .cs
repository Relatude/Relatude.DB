using Relatude.DB.AI;
using Relatude.DB.IO;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.DataStores.Indexes.VectorIndex;
using Relatude.DB.Common;
using System.Diagnostics;

namespace Relatude.DB.DataStores.Indexes;

internal class SemanticIndex : IIndex {
    readonly IVectorIndex _index;
    readonly AIEngine _ai;
    readonly SetRegister _register;
    long _searchIndexStateId;
    readonly DataStoreLocal _db;
    long _timestamp = 0;
    public SemanticIndex(SetRegister sets, string uniqueKey, AIEngine ai, DataStoreLocal db) {
        _register = sets;
        //_index = new HnswVectorIndex();
        UniqueKey = uniqueKey;
        _index = new FlatMemoryVectorIndex();
        _ai = ai;
        newSetState();
        _db = db;
    }
    public string UniqueKey { get; private set; }
    void newSetState() {
        _searchIndexStateId = SetRegister.NewStateId();
    }
    internal List<RawSearchHit> SearchForHitData(string value, int top, int maxHits, float minimumCosineSimilarity, out int totalHits) {
        var vector = _ai.GetEmbeddingsAsync([value]).Result.First();
        List<VectorHit> vectorHits;
        vectorHits = _index.Search(vector, 0, maxHits, minimumCosineSimilarity);
        totalHits = vectorHits.Count;
        List<RawSearchHit> result = new(vectorHits.Count);
        foreach (var hit in vectorHits.Take(top)) {
            result.Add(new() {
                NodeId = hit.NodeId,
                Score = hit.Similarity,
            });
        }
        return result;
    }
    public IdSet SearchForIdSetUnranked(string value, float minimumVectorSimilarity) {
        var vector = _ai.GetEmbeddingsAsync([value]).Result.First();
        return _register.SearchSemantic(_searchIndexStateId, value, minimumVectorSimilarity, () => {
            List<VectorHit> result;
            result = _index.Search(vector, 0, int.MaxValue, minimumVectorSimilarity);
            return result.Select(v => v.NodeId).ToHashSet();
        });
    }
    public void Add(int nodeId, object value) {
        var vec = (float[])value;
        _index.Set(nodeId, vec);
        newSetState();
    }
    public void Remove(int nodeId, object value) {
        _index.Clear(nodeId);
        newSetState();
    }
    public void RegisterAddDuringStateLoad(int nodeId, object value, long timestampId) => Add(nodeId, value);
    public void RegisterRemoveDuringStateLoad(int nodeId, object value, long timestampId) => Remove(nodeId, value);
    public int MaxCount(string value) {
        return 10;
    }
    public void ReadState(IReadStream stream) {
        newSetState();
        _index.ReadState(stream);
    }
    public void SaveState(IAppendStream stream) {
        newSetState();
        _index.SaveState(stream);
    }
    public void CompressMemory() {
    }
    public void Dispose() {
    }
    public void ClearCache() {
    }
    internal string GetSample(string search, string sourceText) {
        // more to be done later here....
        return sourceText;
    }
    internal string GetContextText(string search, string sourceText) {
        // more to be done later here....
        return sourceText;
    }
    public long Timestamp => _timestamp;
    public void Commit(long timestamp) => _timestamp = timestamp;
}
