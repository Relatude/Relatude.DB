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
    readonly IIOProvider _io;
    readonly FileKeyUtility _fileKeys;
    long _searchIndexStateId;
    public SemanticIndex(SetRegister sets, string uniqueKey, IIOProvider io, FileKeyUtility fileKey, AIEngine ai) {
        _register = sets;
        //_index = new HnswVectorIndex();
        UniqueKey = uniqueKey;
        _index = new FlatMemoryVectorIndex();
        _ai = ai;
        newSetState();
        _io = io;
        _fileKeys = fileKey;
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
    public void RegisterAddDuringStateLoad(int nodeId, object value) => Add(nodeId, value);
    public void RegisterRemoveDuringStateLoad(int nodeId, object value) => Remove(nodeId, value);
    public int MaxCount(string value) {
        return 10;
    }
    public void SaveStateForMemoryIndexes(long logTimestamp) {
        var fileName = _fileKeys.Index_GetFileKey(UniqueKey);
        _io.DeleteIfItExists(fileName); // could be optimized to keep old file
        using var stream = _io.OpenAppend(fileName);
        newSetState();
        _index.SaveState(stream);
        stream.WriteVerifiedLong(logTimestamp);
        PersistedTimestamp = logTimestamp;
    }
    public void ReadStateForMemoryIndexes() {
        PersistedTimestamp = 0;
        var fileName = _fileKeys.Index_GetFileKey(UniqueKey);
        if (_io.DoesNotExistsOrIsEmpty(fileName)) return;
        using var stream = _io.OpenRead(fileName, 0);
        newSetState();
        _index.ReadState(stream);
        PersistedTimestamp = stream.ReadVerifiedLong();
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
    public long PersistedTimestamp { get; set; }
}
