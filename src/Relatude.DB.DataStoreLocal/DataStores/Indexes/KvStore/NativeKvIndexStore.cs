using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.Datastores.Indexes.BTreeIndex;
namespace Relatude.DB.DataStores.Indexes.KvStore;

public class NativeKvIndexStore : PersistedIndexStoreBase {
    readonly BPlusTreeStorageEngine _fileStorage;
    readonly ISortedIndex<string> _settings;
    readonly string? _kvFolder;
    readonly Dictionary<string, byte[]>? _pendingFacetSets; // sidecar sections awaiting their index, see FacetSetsFile
    readonly Dictionary<string, IValueIdsCachePersistence> _cachePersistableIndexes = [];
    bool _deepDiskFlush = false;
    long _lastPersistedCacheTimestamp = 0;
    enum SettingKey : int {
        WalId = 1,
    }
    readonly Action<string>? _log;
    public NativeKvIndexStore(string? folderPath, IPersistentWordIndexFactory? wordIndexFactory, Action<string>? log = null) : base(wordIndexFactory) {
        string? filePath;
        if (log == null) {
            log = (msg) => { 
                Console.WriteLine("IndexStore: " + msg); };
        }
        _log = log;
        if (folderPath != null) {
            _kvFolder = Path.Combine(folderPath, "nativekv");
            if (!Directory.Exists(_kvFolder)) Directory.CreateDirectory(_kvFolder);
            filePath = Path.Combine(_kvFolder, "nativekv.db");
        } else {
            filePath = null;// memory only
        }
        var options = new BPlusTreeEngineOptions() {
            //PageCacheBytes = 64L * 1024 * 1024 * 100, // 64 MB
            ValueCacheEntries = 10000,
        };
        _fileStorage = new BPlusTreeStorageEngine(filePath, options);
        _settings = _fileStorage.OpenOrCreateIndex<string>("settings");
        if (_kvFolder != null) _pendingFacetSets = FacetSetsFile.TryRead(Path.Combine(_kvFolder, FacetSetsFile.FileName), _fileStorage.GetTimestamp(), _log, out _lastPersistedCacheTimestamp);
    }
    // The native store's word indexes are always factory-supplied (Lucene). They use a near-real-time
    // reader and are rebuilt from the WAL when behind, so committing them on every data transaction
    // would only add cost — defer instead (they still commit on OptimizeDisk and Dispose).
    protected override bool CommitFactoryWordIndexesOnCommit => false;
    protected override IValueIndex<T> CreateValueIndex<T>(SetRegister sets, string id, string friendlyName, PropertyType type, out bool justCreated) {
        var index = new NativeKvValueIndex<T>(id, this, _fileStorage, sets, friendlyName);
        justCreated = index.PersistedTimestamp == 0;
        _cachePersistableIndexes[id] = index;
        if (_pendingFacetSets != null && _pendingFacetSets.Remove(id, out var section)) {
            try { ((IValueIdsCachePersistence)index).LoadCachedSets(section); } catch { } // stale or corrupt section: stay cold
        }
        return index;
    }
    protected override IWordIndex CreateBuiltInWordIndex(SetRegister sets, string id, string friendlyName, int minWordLength, int maxWordLength, bool prefixSearch, bool infixSearch, out bool justCreated) {
        throw new InvalidOperationException("The native KV index store has no built-in word index; a word index factory is required.");
    }
    protected override IStringArrayIndex CreateStringArrayIndex(SetRegister sets, string id, string friendlyName, PropertyType type, out bool justCreated) {
        var index = new NativeKvStringArrayIndex(id, this, _fileStorage, sets, friendlyName);
        justCreated = index.PersistedTimestamp == 0;
        return index;
    }
    protected override IGuidArrayIndex CreateGuidArrayIndex(SetRegister sets, string id, string friendlyName, PropertyType type, out bool justCreated) {
        var index = new NativeKvGuidArrayIndex(id, this, _fileStorage, sets, friendlyName);
        justCreated = index.PersistedTimestamp == 0;
        return index;
    }
    protected override void BeginTransactionCore() => _fileStorage.BeginTransaction();
    protected override void CommitTransactionCore(long timestamp) => _fileStorage.CommitTransaction(timestamp, _deepDiskFlush);
    protected override void RollbackTransactionCore() => _fileStorage.RollbackTransaction();
    protected override Guid ReadWalFileId() {
        if (_settings.TryGetValue((int)SettingKey.WalId, out var s) && Guid.TryParse(s, out var walFileId)) return walFileId;
        return Guid.Empty;
    }
    protected override void WriteWalFileId(Guid walFileId, long? timestamp) {
        // A one-off durable engine transaction; when no timestamp is given, keep the current one.
        _fileStorage.BeginTransaction();
        try {
            _settings.Set((int)SettingKey.WalId, walFileId.ToString());
            _fileStorage.CommitTransaction(timestamp ?? _fileStorage.GetTimestamp(), _deepDiskFlush);
        } catch {
            // roll back so the engine transaction is not left open, which would make every later
            // BeginTransaction fail and wedge the store
            try { _fileStorage.RollbackTransaction(); } catch { /* best effort: the original error is rethrown */ }
            throw;
        }
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
    public override void UpdatePersistedCaches() {
        if (_kvFolder != null) {
            var timestamp = GetTimestamp();
            var isCacheOutOfDate = timestamp > _lastPersistedCacheTimestamp;
            var anyCacheDirty = _cachePersistableIndexes.Values.Any(i => i.AreThereNewUnsavedCachedSets);
            if (isCacheOutOfDate || anyCacheDirty) {
                var filePath = Path.Combine(_kvFolder, FacetSetsFile.FileName);
                try {
                    FacetSetsFile.Write(filePath, timestamp, _cachePersistableIndexes.Values, _log);
                    _lastPersistedCacheTimestamp = timestamp;
                } catch { }
            }
        }
    }
    protected override void DisposeCore() {
        UpdatePersistedCaches();
        _fileStorage.Dispose();
    }
}
