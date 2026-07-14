using KvStore;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Sets;
using static Relatude.DB.DataStores.Relations.RelationDataDictionary;

namespace Relatude.DB.DataStores.Indexes.KvStore;

public class NativeKvIndexStore : IPersistedIndexStore {
    string _path;
    DatabaseFile _fileStorage;
    KeyValueStore<string, string> _settings;
    readonly IPersistentWordIndexFactory _wordIndexFactory;
    readonly Dictionary<string, IPersistentWordIndex> _wordIndexes = [];
    public NativeKvIndexStore(string folderPath, IPersistentWordIndexFactory wordIndexFactory) {
        _wordIndexFactory = wordIndexFactory;
        if(!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);
        var filePath = Path.Combine(folderPath, "kvstore.db");
        _path = filePath;
        _fileStorage = DatabaseFile.Open(filePath);
        _settings = _fileStorage.GetStore<string, string>("settings");
    }
    public void CancelTransaction() {
        _fileStorage.CancelTransaction();
    }
    public void CommitTransaction(long timestamp) {
        _fileStorage.CommitTransaction(timestamp);
    }
    public void Dispose() {
        _fileStorage.Dispose();
    }
    public void FullCleanUpOnBadError() {
    }
    public long GetTotalDiskSpace() {
        return _fileStorage.GetTotalDiskSpace();
    }
    public IValueIndex<T> OpenValueIndex<T>(SetRegister sets, string id, string friendlyName, PropertyType type) where T : notnull {
        return new NativeKvValueIndex<T>(id, _fileStorage, sets, friendlyName);
    }
    public IWordIndex OpenWordIndex(SetRegister sets, string id, string friendlyName, int minWordLength, int maxWordLength, bool prefixSearch, bool infixSearch) {
        IWordIndex index;
        if (_wordIndexes.ContainsKey(id)) return _wordIndexes[id];
        var idx = _wordIndexFactory.Create(sets, this, id, friendlyName, minWordLength, maxWordLength, prefixSearch, infixSearch);
        _wordIndexes.Add(id, idx);        
        index = idx;
        return index;
    }
    public void OptimizeDisk() {
    }
    public void ReOpen() {
        _fileStorage.Dispose();
        _fileStorage = DatabaseFile.Open(_path);
        _settings = _fileStorage.GetStore<string, string>("settings");
    }
    public void ResetAll() {
        var currentSettings = _settings.RangeAll().ToArray();
        _fileStorage.DeleteAll();
        _fileStorage.StartTransaction();
        foreach (var (key, value) in currentSettings) {
            _settings.Put(key, value);
        }
        _fileStorage.CommitTransaction(0);
    }
    public Guid WalFileId => Guid.TryParse(_settings.TryGet("walFileId", out var walFileIdStr) ? walFileIdStr : null, out var walFileId) ? walFileId : Guid.Empty;
    public void SetWalFileId(Guid walFileId) => _settings.Put("walFileId", walFileId.ToString());
    public void StartTransaction() => _fileStorage.StartTransaction();
    public void UpdateTimestampsDueToHotswap(long timestamp, Guid walFileId) {
        _fileStorage.StartTransaction();
        _settings.Put("walFileId", walFileId.ToString());
        _fileStorage.CommitTransaction(timestamp);
    }
}
