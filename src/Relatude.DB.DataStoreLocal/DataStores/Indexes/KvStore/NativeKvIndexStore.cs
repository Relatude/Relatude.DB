
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Sets;
using SuperFastIndex;
namespace Relatude.DB.DataStores.Indexes.KvStore;

public class NativeKvIndexStore : IPersistedIndexStore {
    BPlusTreeEngineOptions _options;
    BPlusTreeStorageEngine _fileStorage;
    HashSet<string> _justCreated = [];
    ISortedIndex<string> _settings;
    enum SettingKey : int {
        WalId = 1,
    }
    readonly IPersistentWordIndexFactory? _wordIndexFactory;
    readonly Dictionary<string, IPersistentWordIndex> _wordIndexes = [];
    public NativeKvIndexStore(string? folderPath, IPersistentWordIndexFactory? wordIndexFactory) {
        _wordIndexFactory = wordIndexFactory;
        string? filePath;
        if (folderPath != null) {
            if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);
            filePath = Path.Combine(folderPath, "kvstore.db");
        } else {
            filePath = null;// memory only
        }
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
        var index = new NativeKvValueIndex<T>(id, this, _fileStorage, sets, friendlyName);
        var justCreated = index.PersistedTimestamp == 0;
        if (justCreated) {
            _justCreated.Add(id);
        }
        return index;
    }
    public IWordIndex OpenWordIndex(SetRegister sets, string id, string friendlyName, int minWordLength, int maxWordLength, bool prefixSearch, bool infixSearch) {
        if (_wordIndexFactory == null) throw new InvalidOperationException("Word index factory is not set.");
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
        _wordIndexFactory?.DeleteUnopenedFiles(_wordIndexes.Keys);
    }
    public void OptimizeDisk() {
    }
    public void ResetAll() {
        var currentSettings = _settings.Entries.ToArray();
        _fileStorage.DeleteAll();
        _fileStorage.BeginTransaction();
        foreach (var (key, value) in currentSettings) {
            _settings.Set(key, value);
        }
        _fileStorage.CommitTransaction(0, true);
        foreach (var i in _wordIndexes) i.Value.Close();
        if (_wordIndexFactory != null) _wordIndexFactory.DeleteAllFiles();
        foreach (var i in _wordIndexes) i.Value.Open();
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
    public void BeginTransaction()
        => _fileStorage.BeginTransaction();
    public void SetWalFileIdAndTimestamp(long timestamp, Guid walFileId) {
        _fileStorage.BeginTransaction();
        _settings.Set((int)SettingKey.WalId, walFileId.ToString());
        _fileStorage.CommitTransaction(timestamp, true);
    }
    public long GetTimestamp() {
        return _fileStorage.GetTimestamp();
    }
}
