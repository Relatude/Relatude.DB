using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Indexes;

public enum IndexState {
    Closed,
    Ready,
    Loading,
    Saving,
}
public interface IIndex : IDisposable {

    string UniqueKey { get; }

    string FriendlyName { get; }

    void ClearCache();
    void CompressMemory();

    void Add(int id, object value);
    void Remove(int id, object value);


    void RegisterAddDuringStateLoad(int id, object value);
    void RegisterRemoveDuringStateLoad(int id, object value);
    /// <summary>
    /// Called once by the store when the replay of the log is over, before anything reads or saves
    /// the index. An index that only gathers the actions registered during the state load applies
    /// them here; most indexes have nothing to do. A wrapper must forward the call to the index it
    /// wraps - the default does nothing, and a load left unapplied is an empty index.
    /// </summary>
    void CompleteStateLoad() { }

    void ReadStateForMemoryIndexes(Guid walFileId);
    void SaveStateForMemoryIndexes(long logTimestamp, Guid walFileId);

    void WriteNewTimestampDueToRewriteHotswap(long newTimestamp, Guid walFileId);

    long PersistedTimestamp { get; }
    void FlagFirstCommit();
}
