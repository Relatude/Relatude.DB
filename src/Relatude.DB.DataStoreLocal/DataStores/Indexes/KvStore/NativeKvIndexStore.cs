using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.Datastores.Indexes.BTreeIndex;
namespace Relatude.DB.DataStores.Indexes.KvStore;

public class NativeKvIndexStore : PersistedIndexStoreBase {
    readonly BPlusTreeStorageEngine _fileStorage;
    readonly ISortedIndex<string> _settings;
    enum SettingKey : int {
        WalId = 1,
    }
    public NativeKvIndexStore(string? folderPath, IPersistentWordIndexFactory? wordIndexFactory) : base(wordIndexFactory) {
        string? filePath;
        if (folderPath != null) {
            var kvFolder = Path.Combine(folderPath, "nativekv");
            if (!Directory.Exists(kvFolder)) Directory.CreateDirectory(kvFolder);
            filePath = Path.Combine(kvFolder, "nativekv.db");
        } else {
            filePath = null;// memory only
        }
        var options = new BPlusTreeEngineOptions() {
            PageCacheBytes = 1024 * 1024 * 50, // 50 MB
            ValueCacheEntries = 5000,
        };
        _fileStorage = new BPlusTreeStorageEngine(filePath, options);
        _settings = _fileStorage.OpenOrCreateIndex<string>("settings");
    }
    // The native store's word indexes are always factory-supplied (Lucene). They use a near-real-time
    // reader and are rebuilt from the WAL when behind, so committing them on every data transaction
    // would only add cost — defer instead (they still commit on OptimizeDisk and Dispose).
    protected override bool CommitFactoryWordIndexesOnCommit => false;
    protected override IValueIndex<T> CreateValueIndex<T>(SetRegister sets, string id, string friendlyName, PropertyType type, out bool justCreated) {
        var index = new NativeKvValueIndex<T>(id, this, _fileStorage, sets, friendlyName);
        justCreated = index.PersistedTimestamp == 0;
        return index;
    }
    protected override IWordIndex CreateBuiltInWordIndex(SetRegister sets, string id, string friendlyName, int minWordLength, int maxWordLength, bool prefixSearch, bool infixSearch, out bool justCreated) {
        throw new InvalidOperationException("The native KV index store has no built-in word index; a word index factory is required.");
    }
    protected override void BeginTransactionCore() => _fileStorage.BeginTransaction();
    protected override void CommitTransactionCore(long timestamp) => _fileStorage.CommitTransaction(timestamp, true);
    protected override void RollbackTransactionCore() => _fileStorage.RollbackTransaction();
    protected override Guid ReadWalFileId() {
        if (_settings.TryGetValue((int)SettingKey.WalId, out var s) && Guid.TryParse(s, out var walFileId)) return walFileId;
        return Guid.Empty;
    }
    protected override void WriteWalFileId(Guid walFileId, long? timestamp) {
        // A one-off durable engine transaction; when no timestamp is given, keep the current one.
        _fileStorage.BeginTransaction();
        _settings.Set((int)SettingKey.WalId, walFileId.ToString());
        _fileStorage.CommitTransaction(timestamp ?? _fileStorage.GetTimestamp(), true);
    }
    public override long GetTimestamp() => _fileStorage.GetTimestamp();
    public override long GetTotalDiskSpace() => _fileStorage.GetTotalDiskSpace();
    protected override void OptimizeDiskCore() {
        // The KV engine has no separate compaction step.
    }
    protected override void DeleteUnopenedIndexesCore() {
        // Every value index opened this session (and the settings index) is open in the engine,
        // so this only deletes KV indexes that have left the schema.
        _fileStorage.DeleteUnopenedIndexes();
    }
    protected override void ResetAllDataCore() {
        // DeleteAll keeps the opened indexes (including settings) as empty, uncataloged definitions;
        // the base re-persists the WAL id and a timestamp of 0 immediately after this returns.
        _fileStorage.DeleteAll();
    }
    protected override void DisposeCore() => _fileStorage.Dispose();
}
