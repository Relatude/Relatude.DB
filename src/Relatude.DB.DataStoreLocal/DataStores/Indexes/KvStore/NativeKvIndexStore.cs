
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Sets;
using SuperFastIndex;
namespace Relatude.DB.DataStores.Indexes.KvStore;

public class NativeKvIndexStore : IPersistedIndexStore {
    string _path;
    BPlusTreeEngineOptions _options;
    BPlusTreeStorageEngine _fileStorage;
    ISortedIndex<string> _settings;
    enum SettingKey : int {
        WalId = 1,
    }
    readonly IPersistentWordIndexFactory _wordIndexFactory;
    readonly Dictionary<string, IPersistentWordIndex> _wordIndexes = [];
    public NativeKvIndexStore(string folderPath, IPersistentWordIndexFactory wordIndexFactory) {
        _wordIndexFactory = wordIndexFactory;
        if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);
        var filePath = Path.Combine(folderPath, "kvstore.db");
        _path = filePath;
        _options = new BPlusTreeEngineOptions();
        _fileStorage = new BPlusTreeStorageEngine(filePath, _options);
        _settings = _fileStorage.OpenOrCreateIndex<string>("settings");
    }
    public void CancelTransaction() {
        _fileStorage.RollbackTransaction();
    }
    public void CommitTransaction(long timestamp) {
        _fileStorage.CommitTransaction(timestamp, true);
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
        _fileStorage = new BPlusTreeStorageEngine(_path, _options);
        _settings = _fileStorage.OpenOrCreateIndex<string>("settings");
    }
    public void ResetAll() {
        var currentSettings = _settings.Entries.ToArray();
        _fileStorage.DeleteAll();
        _fileStorage.BeginTransaction();
        foreach (var (key, value) in currentSettings) {
            _settings.Set(key, value);
        }
        _fileStorage.CommitTransaction(0, true);
    }
    public Guid WalFileId => Guid.TryParse(_settings.TryGetValue((int)SettingKey.WalId, out var walFileIdStr) ? walFileIdStr : null, out var walFileId) ? walFileId : Guid.Empty;
    public void SetWalFileId(Guid walFileId) => _settings.Set((int)SettingKey.WalId, walFileId.ToString());
    public void StartTransaction() => _fileStorage.BeginTransaction();
    public void UpdateTimestampsDueToHotswap(long timestamp, Guid walFileId) {
        _fileStorage.BeginTransaction();
        _settings.Set((int)SettingKey.WalId, walFileId.ToString());
        _fileStorage.CommitTransaction(timestamp, true);
    }
}
