using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.DataStores.Indexes.VectorIndex;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
using System.Diagnostics;
using System.Reflection;

namespace Relatude.DB.DataStores.Indexes;

internal class SemanticIndex : IIndex {
    readonly IVectorIndex _index;
    readonly AIEngine _ai;
    readonly SetRegister _register;
    readonly IIOProvider _io;
    readonly FileKeyUtility _fileKeys;
    long _searchIndexStateId;
    public SemanticIndex(SetRegister sets, string uniqueKey, string friendlyName, IIOProvider io, FileKeyUtility fileKey, AIEngine ai, Action<string> log) {
        _register = sets;
        UniqueKey = uniqueKey;
        var indexType = ai.Settings.IndexType ?? AIProviderIndexType.FlatMemory;
        _index = indexType switch {
            AIProviderIndexType.FlatMemory => new FlatMemoryVectorIndex(),
            //AIProviderIndexType.HnswVector => new HnswVectorIndex(), 
            //AIProviderIndexType.FlatDisk => new FlatDiskVectorIndex(),
            AIProviderIndexType.TurboQuant =>
            ai.Settings.ModelDimensions.HasValue ? new TurboQuantVectorIndex(ai.Settings.ModelDimensions.Value, log) :
            throw new Exception("AI model dimensions must be specified in settings to create a semantic index. "),
            _ => throw new Exception("AIProviderIndexType not supported: " + indexType)
        };
        _ai = ai;
        newSetState();
        _io = io;
        _fileKeys = fileKey;
        FriendlyName = friendlyName;
    }
    public string UniqueKey { get; private set; }
    void newSetState() {
        _searchIndexStateId = SetRegister.NewStateId();
    }
    internal List<RawSearchHit> SearchForHitData(string value, int top, int maxHits, float minimumCosineSimilarity, out int totalHits) {
        var vector = _ai.GetEmbeddingsAsync([value]).Result.First();
        List<VectorHit> vectorHits;

        //var sw = Stopwatch.StartNew();
        //var iterations = 100;
        //for (int i = 0; i < iterations; i++) {
        //    vectorHits = _index.Search(vector, 0, maxHits, minimumCosineSimilarity);
        //}
        //sw.Stop();
        //Console.WriteLine($"SearchForHitData: Average time for {iterations} iterations: {sw.Elapsed.TotalMilliseconds / iterations} ms");

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
    public void WriteNewTimestampDueToRewriteHotswap(long newTimestamp, Guid walFileId) {
        var fileName = _fileKeys.Index_GetFileKey(UniqueKey);
        using var stream = _io.OpenAppend(fileName);
        stream.WriteVerifiedLong(newTimestamp);
        stream.WriteGuid(walFileId);
        PersistedTimestamp = newTimestamp;
    }
    public void SaveStateForMemoryIndexes(long logTimestamp, Guid walFileId) {
        var fileName = _fileKeys.Index_GetFileKey(UniqueKey);
        _io.DeleteFileIfItExists(fileName); // could be optimized to keep old file
        using var stream = _io.OpenAppend(fileName);
        newSetState();
        _index.SaveState(stream);
        stream.WriteVerifiedLong(logTimestamp);
        stream.WriteGuid(walFileId);
        PersistedTimestamp = logTimestamp;
    }
    public void ReadStateForMemoryIndexes(Guid walFileId) {
        PersistedTimestamp = 0;
        var fileName = _fileKeys.Index_GetFileKey(UniqueKey);
        if (_io.DoesNotExistsOrIsEmpty(fileName)) return;
        using var stream = _io.OpenRead(fileName, 0);
        newSetState();
        _index.ReadState(stream);
        Guid walId = Guid.Empty;
        while (stream.More()) {
            PersistedTimestamp = stream.ReadVerifiedLong();
            walId = stream.ReadGuid();
        }
        if (walId != walFileId) throw new Exception("WAL file ID mismatch when reading index state. ");
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
    public string FriendlyName { get; }
}
