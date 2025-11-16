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

    void ClearCache();
    void CompressMemory();

    void Add(int id, object value);
    void Remove(int id, object value);

    void RegisterAddDuringStateLoad(int id, object value, long timestampId);
    void RegisterRemoveDuringStateLoad(int id, object value, long timestampId);

    void ReadState(Guid walFileId);
    void SaveState(IAppendStream stream);

    Guid WalFileId { get; }
    long Timestamp { get; }
    void Commit(long timestamp);
    void Reset();

}