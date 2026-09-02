using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.Datastores.Indexes.BTreeIndex;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Indexes.KvStore;

public class NativeKvIndexStore : ValueIndexEngineBase {
    readonly BPlusTreeStorageEngine _fileStorage;
    readonly ISortedIntIndex<string> _settings;
    readonly string? _kvFolder;
    readonly Dictionary<string, byte[]>? _pendingFacetSets; // sidecar sections awaiting their index, see FacetSetsFile
    readonly Dictionary<string, IValueIdsCachePersistence> _cachePersistableIndexes = [];
    long _lastPersistedCacheTimestamp = 0;
    enum SettingKey : int {
        WalId = 1,
    }
    readonly Action<string>? _log;
    /// <summary>
    /// <paramref name="maxMemoryBytes"/> bounds what the engine spends on its page cache and on
    /// published-but-not-durable pages, two thirds to one; negative keeps the built-in sizes. It is
    /// a budget, not an allocation: a small store never grows into it, and 0 asks for the smallest
    /// caches the pager runs with.
    /// </summary>
    public NativeKvIndexStore(string? folderPath, Action<string>? log = null, long maxMemoryBytes = -1) {
        string? filePath;
        if (log == null) {
            log = (msg) => {
                Console.WriteLine("IndexStore: " + msg);
            };
        }
        _log = log;
        if (folderPath != null) {
            _kvFolder = Path.Combine(folderPath, FileKeyUtility.IndexEngine_NativeKvFolderKey);
            if (!Directory.Exists(_kvFolder)) Directory.CreateDirectory(_kvFolder);
            filePath = Path.Combine(_kvFolder, FileKeyUtility.IndexEngine_NativeKvFileKey);
        } else {
            filePath = null;// memory only
        }
        const long minBytes = 256L * 1024; // a handful of pages: the pager needs some room to work in at all
        var options = maxMemoryBytes < 0
            ? new BPlusTreeEngineOptions() {
                PageCacheBytes = 16L * 1024 * 1024, // 16 MB
                PendingWriteBytes = 32L * 1024 * 1024, // 32 MB
                ValueCacheEntries = 0,
            }
            : new BPlusTreeEngineOptions() {
                PageCacheBytes = Math.Max(minBytes, maxMemoryBytes / 3 * 2),
                PendingWriteBytes = Math.Max(minBytes, maxMemoryBytes / 3),
                ValueCacheEntries = 0,
            };
        _fileStorage = new BPlusTreeStorageEngine(filePath, options);
        _settings = _fileStorage.OpenOrCreateSortedIntIndex<string>("settings");
        if (_kvFolder != null) _pendingFacetSets = FacetSetsFile.TryRead(Path.Combine(_kvFolder, FileKeyUtility.IndexEngine_FacetSetsFileKey), _fileStorage.GetTimestamp(), _log, out _lastPersistedCacheTimestamp);
    }
    protected override IValueIndex<T> CreateValueIndex<T>(SetRegister sets, string id, string friendlyName, PropertyType type, out bool justCreated) {
        var index = new NativeKvValueIndex<T>(id, this, _fileStorage, sets, friendlyName);
        justCreated = index.PersistedTimestamp == 0;
        _cachePersistableIndexes[id] = index;
        if (_pendingFacetSets != null && _pendingFacetSets.Remove(id, out var section)) {
            try { ((IValueIdsCachePersistence)index).LoadCachedSets(section); } catch { } // stale or corrupt section: stay cold
        }
        return index;
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
    protected override IIntArrayIndex CreateIntArrayIndex(SetRegister sets, string id, string friendlyName, PropertyType type, out bool justCreated) {
        var index = new NativeKvIntArrayIndex(id, this, _fileStorage, sets, friendlyName);
        justCreated = index.PersistedTimestamp == 0;
        return index;
    }
    protected override void BeginTransactionCore() => _fileStorage.BeginTransaction();
    // Publish only: readers see the commit immediately, but the durable meta is written first in
    // MakeDurableCore — which the data store calls right after a successful WAL flush. A crash
    // between the two rolls the engine back to the last durable point, which is always at or behind
    // the durable WAL, so the indexes can never durably contain transactions the log is missing.
    protected override void CommitTransactionCore(long timestamp) => _fileStorage.PublishTransaction(timestamp);
    // deepFlush false: the engine's fsync-based durability suffices; a deep (FlushFileBuffers-style)
    // flush could be wired from SettingsLocal.DeepFlushDisk via the constructor if ever needed
    protected override void MakeDurableCore() => _fileStorage.MakeDurable(deepDiskFlush: false);
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
            _fileStorage.CommitTransaction(timestamp ?? _fileStorage.GetTimestamp(), deepDiskFlush: false);
        } catch {
            // roll back so the engine transaction is not left open, which would make every later
            // BeginTransaction fail and wedge the store
            try { _fileStorage.RollbackTransaction(); } catch { /* best effort: the original error is rethrown */ }
            throw;
        }
    }
    public override long GetTimestamp() => _fileStorage.GetTimestamp();
    public override long GetTotalDiskSpace() => _fileStorage.GetTotalDiskSpace();
    public override void OptimizeDisk() {
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
    public override void SaveIndexCaches(bool force) {
        if (_kvFolder != null) {
            var timestamp = GetTimestamp();
            var isCacheOutOfDate = timestamp > _lastPersistedCacheTimestamp;
            var anyCacheDirty = _cachePersistableIndexes.Values.Any(i => i.AreThereNewUnsavedCachedSets);
            if (isCacheOutOfDate || anyCacheDirty || force) {
                var filePath = Path.Combine(_kvFolder, FileKeyUtility.IndexEngine_FacetSetsFileKey);
                try {
                    if (File.Exists(filePath)) File.Delete(filePath);
                    FacetSetsFile.Write(filePath, timestamp, _cachePersistableIndexes.Values, _log);
                    _lastPersistedCacheTimestamp = timestamp;
                } catch { }
            }
        }
    }
    public override void ResetIndexCaches() {
        if (_kvFolder != null) {
            var filePath = Path.Combine(_kvFolder, FileKeyUtility.IndexEngine_FacetSetsFileKey);
            try {
                if (File.Exists(filePath)) File.Delete(filePath);
            } catch { }
            // NB: _cachePersistableIndexes stays as is - indexes register once at open
            // (CreateValueIndex), so unregistering them here would silently disable every
            // later sidecar save for the rest of the process lifetime
            _lastPersistedCacheTimestamp = 0;
        }
    }
    protected override void DisposeCore() {
        SaveIndexCaches(false);
        _fileStorage.Dispose();
    }
}
