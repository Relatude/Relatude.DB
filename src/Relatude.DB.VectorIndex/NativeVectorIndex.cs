using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;

namespace Relatude.DB.VectorIndex;

public class NativeVectorIndex : ISemanticIndex {
    public NativeVectorIndex() { }

    public string FriendlyName => throw new NotImplementedException();

    public long PersistedTimestamp => throw new NotImplementedException();

    public string UniqueKey => throw new NotImplementedException();

    public void Add(int nodeId, object value) {
        throw new NotImplementedException();
    }
    public void Add(int nodeId, float[] value) {
        throw new NotImplementedException();
    }

    public void ClearCache() {
        throw new NotImplementedException();
    }

    public void CompressMemory() {
        throw new NotImplementedException();
    }

    public void Dispose() {
        throw new NotImplementedException();
    }

    public void FlagFirstCommit() {
        throw new NotImplementedException();
    }

    public int MaxCount(string value) {
        throw new NotImplementedException();
    }

    public void ReadStateForMemoryIndexes(Guid walFileId) {
        throw new NotImplementedException();
    }

    public void RegisterAddDuringStateLoad(int nodeId, object value) {
        throw new NotImplementedException();
    }

    public void RegisterRemoveDuringStateLoad(int nodeId, object value) {
        throw new NotImplementedException();
    }

    public void Remove(int nodeId, object value) {
        throw new NotImplementedException();
    }

    public void SaveStateForMemoryIndexes(long logTimestamp, Guid walFileId) {
        throw new NotImplementedException();
    }

    public IdSet SearchForIdSetUnranked(string value, float minimumVectorSimilarity) {
        throw new NotImplementedException();
    }

    public void WriteNewTimestampDueToRewriteHotswap(long newTimestamp, Guid walFileId) {
        throw new NotImplementedException();
    }
}
