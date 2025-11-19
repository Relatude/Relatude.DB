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

    void ReadStateForMemoryIndexes();
    void SaveStateForMemoryIndexes(long logTimestamp);

    long PersistedTimestamp { get; set; }

}