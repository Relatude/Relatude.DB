using Relatude.DB.DataStores.Sets;

namespace Relatude.DB.DataStores.Indexes;

public interface ISemanticIndex {
    string FriendlyName { get; }
    long PersistedTimestamp { get; }
    string UniqueKey { get; }

    void Add(int nodeId, object value);
    void ClearCache();
    void CompressMemory();
    void Dispose();
    void FlagFirstCommit();
    int MaxCount(string value);
    void ReadStateForMemoryIndexes(Guid walFileId);
    void RegisterAddDuringStateLoad(int nodeId, object value);
    void RegisterRemoveDuringStateLoad(int nodeId, object value);
    void Remove(int nodeId, object value);
    void SaveStateForMemoryIndexes(long logTimestamp, Guid walFileId);
    IdSet SearchForIdSetUnranked(string value, float minimumVectorSimilarity);
    void WriteNewTimestampDueToRewriteHotswap(long newTimestamp, Guid walFileId);
}