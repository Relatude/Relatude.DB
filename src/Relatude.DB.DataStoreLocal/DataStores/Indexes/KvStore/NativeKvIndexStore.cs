
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
    public void RollbackTransaction() {
        if (!_fileStorage.IsInTransaction) throw new InvalidOperationException("No transaction is currently active.");
        _fileStorage.RollbackTransaction();
    }
    public void CommitTransaction(long timestamp) {
        _fileStorage.CommitTransaction(timestamp, true);
    }
    public void Dispose() {
        _fileStorage.Dispose();
    }
    public void CleanUpOnUnknownTransactionError() {
        if (_fileStorage.IsInTransaction) {
            _fileStorage.RollbackTransaction();
        }
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
    public void DeleteUnopenedIndexes() {
        // every value index opened this session (and the settings index) is open in the engine,
        // so this only deletes kv indexes that have left the schema
        _fileStorage.DeleteUnopenedIndexes();
        _wordIndexFactory.DeleteUnopenedFiles(_wordIndexes.Keys);
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
    public Guid GetWalFileId() {
        if (_settings.TryGetValue((int)SettingKey.WalId, out var s)) {
            if (Guid.TryParse(s, out var walFileId)) {
                return walFileId;
            }
        }
        return Guid.Empty;
    }
    public void SetWalFileId(Guid walFileId) {
        _fileStorage.BeginTransaction();
        _settings.Set((int)SettingKey.WalId, walFileId.ToString());
        _fileStorage.CommitTransaction(_fileStorage.GetTimestamp(), true);
    }
    public void BeginTransaction() => _fileStorage.BeginTransaction();
    public void SetWalFileIdAndTimestamp(long timestamp, Guid walFileId) {
        _fileStorage.BeginTransaction();
        _settings.Set((int)SettingKey.WalId, walFileId.ToString());
        _fileStorage.CommitTransaction(timestamp, true);
    }
}
