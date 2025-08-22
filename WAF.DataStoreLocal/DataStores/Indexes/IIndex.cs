using WAF.IO;

namespace WAF.DataStores.Indexes {
    public interface IIndex : IDisposable {
        string UniqueKey { get; }
        void ClearCache();
        void CompressMemory();

        void Add(int id, object value);
        void Remove(int id, object value);

        void RegisterAddDuringStateLoad(int id, object value, long timestampId);
        void RegisterRemoveDuringStateLoad(int id, object value, long timestampId);

        void ReadState(IReadStream stream);
        void SaveState(IAppendStream stream);
    }
}