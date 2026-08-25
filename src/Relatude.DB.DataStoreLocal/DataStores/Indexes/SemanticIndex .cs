using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.DataStores.Indexes.VectorIndex;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
using System.Reflection;

namespace Relatude.DB.DataStores.Indexes;

internal class MemorySemanticIndex : IIndex, ISemanticIndex {
    readonly IVectorIndex _index;
    readonly AIEngine _ai;
    readonly SetRegister _register;
    readonly IIOProvider _io;
    long _searchIndexStateId;
    // true whenever the in-memory state may differ from the persisted body; starts true so an
    // index that was never persisted is never re-stamped as if it were (see WriteNewTimestampDueToRewriteHotswap)
    bool _changedSinceLastSave = true;
    public MemorySemanticIndex(SetRegister sets, string uniqueKey, string friendlyName, IIOProvider io, AIEngine ai) {
        _register = sets;
        UniqueKey = uniqueKey;
        _index = new FlatMemoryVectorIndex();
        _ai = ai;
        newSetState();
        _io = io;
        FriendlyName = friendlyName;
    }
    public string UniqueKey { get; private set; }
    void newSetState() {
        _searchIndexStateId = SetRegister.NewStateId();
    }
    public List<RawSearchHit> SearchForHitData(string value, int top, int maxHits, float minimumCosineSimilarity, out int totalHits) {
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
    public void Add(int nodeId, object value) => Add(nodeId, (float[])value);
    public void Add(int nodeId, float[] value) {
        _changedSinceLastSave = true;
        value = this.EnsureCorrectDimensions(value);
        _index.Set(nodeId, value);
        newSetState();
    }
    public void Remove(int nodeId, object value) => Remove(nodeId, (float[])value);
    public void Remove(int nodeId, float[] value) {
        _changedSinceLastSave = true;
        _index.Clear(nodeId);
        newSetState();
    }
    public void RegisterAddDuringStateLoad(int nodeId, object value) => Add(nodeId, value);
    public void RegisterRemoveDuringStateLoad(int nodeId, object value) => Remove(nodeId, value);
    public int MaxCount(string value) {
        return 10;
    }
    public void WriteNewTimestampDueToRewriteHotswap(long newTimestamp, Guid walFileId) {
        // appending a stamp is only sound when the persisted body equals the in-memory state: the
        // stamp is trusted on the next open, so changes missing from a stale body would be skipped
        // by the log replay and silently lost — and a never-persisted index would get a body-less,
        // unreadable file. Persist the full state whenever the body may be behind:
        if (_changedSinceLastSave) {
            SaveStateForMemoryIndexes(newTimestamp, walFileId);
            return;
        }
        var fileName = FileKeyUtility.Index_GetFileKey(UniqueKey);
        using var stream = _io.OpenAppend(fileName);
        stream.WriteVerifiedLong(newTimestamp);
        stream.WriteGuid(walFileId);
        PersistedTimestamp = newTimestamp;
    }
    public void SaveStateForMemoryIndexes(long logTimestamp, Guid walFileId) {
        var fileName = FileKeyUtility.Index_GetFileKey(UniqueKey);
        _io.DeleteFileIfItExists(fileName); // could be optimized to keep old file
        using var stream = _io.OpenAppend(fileName);
        newSetState();
        _index.SaveState(stream);
        stream.WriteVerifiedLong(logTimestamp);
        stream.WriteGuid(walFileId);
        PersistedTimestamp = logTimestamp;
        _changedSinceLastSave = false;
    }
    public void ReadStateForMemoryIndexes(Guid walFileId) {
        PersistedTimestamp = 0;
        var fileName = FileKeyUtility.Index_GetFileKey(UniqueKey);
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
        _changedSinceLastSave = false; // memory now equals the body just read
    }
    public void CompressMemory() {
    }
    public void Dispose() {
    }
    public void ClearCache() {
    }
    public string GetSample(string search, string sourceText) {
        // more to be done later here....
        return sourceText;
    }
    public string GetContextText(string search, string sourceText) {
        // more to be done later here....
        return sourceText;
    }
    public long PersistedTimestamp { get; private set; }
    public void FlagFirstCommit() { }
    public string FriendlyName { get; }
    public bool TryGetNoDimensions(out int dimensions) {
        return _index.TryGetNoDimensions(out dimensions);
    }
    public void LogWarning(string message) => _ai?.LogCallback?.Invoke(message);
}
