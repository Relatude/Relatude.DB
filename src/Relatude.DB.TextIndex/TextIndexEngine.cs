using Relatude.DB.DataStores.Indexes.TextIndexing;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Indexes;

/// <summary>
/// Text index engine backed by the built-in disk-based <see cref="TextIndex"/>, one index folder
/// per word index under <c>&lt;indexFolder&gt;/textindex</c>. Writes go to each index's in-memory
/// write buffer and are searchable immediately, so the engine transaction is in-memory only:
/// CommitTransaction just records the published timestamp, and durability happens in
/// <see cref="MakeDurableCore"/> â€” called by the data store right after every successful WAL flush â€”
/// where each index flushes its buffer to an immutable segment and re-points its manifest,
/// stamped with the position (timestamp + WAL file id). A crash therefore loses only ops the
/// durable log can replay: on the next open every index reports the position of its last durable
/// manifest and the startup loader feeds it exactly the missing transactions.
///
/// <para>The engine-level WAL file id lives in a small marker file next to the index folders.
/// Per index, the manifest must carry the same id; anything else (foreign log file, restored or
/// torn files) resets that index to empty so the replay rebuilds it â€” see <see cref="TextIndex"/>.</para>
/// </summary>
public class TextIndexEngine : IndexEngineBase, ITextIndexEngine {
    readonly string _folderPath;
    readonly TextIndexOptions _options;
    readonly TextIndexCache _cache; // one byte budget shared by every index of this engine
    // raw index for lifecycle calls, wrapped for hand-out (and idempotent re-open)
    readonly Dictionary<string, (TextIndex index, IWordIndex wrapped)> _indexes = [];
    Guid _walFileId;
    long _currentTimestamp; // last published (committed) transaction; made durable in MakeDurableCore
    public TextIndexEngine(string baseIndexFolderPath) : this(baseIndexFolderPath, null) { }
    public TextIndexEngine(string baseIndexFolderPath, TextIndexOptions? options) {
        _options = options ?? new TextIndexOptions();
        _cache = new TextIndexCache(_options.MaxCacheBytes);
        _folderPath = Path.Combine(baseIndexFolderPath, FileKeyUtility.IndexEngine_TextIndexFolderKey);
        if (!Directory.Exists(_folderPath)) Directory.CreateDirectory(_folderPath);
        _walFileId = readMarkerFile();
    }
    public override string Name => "Native Text";

    /// <summary>The WAL file id the engine's indexes belong to; the indexes read it when they
    /// validate their manifest on open and stamp it on every flush.</summary>
    internal Guid WalFileId => _walFileId;

    /// <summary>
    /// What the shared read cache currently holds, against its budget. Everything else the engine
    /// keeps in memory is O(documents) or O(segments), not O(text), so this is the number to watch
    /// when the process footprint is bigger than expected â€” and the one
    /// <see cref="TextIndexOptions.MaxCacheBytes"/> bounds.
    /// </summary>
    public (long UsedBytes, long MaxBytes, int Entries) GetCacheStats() => (_cache.UsedBytes, _cache.MaxBytes, _cache.Count);

    public IWordIndex OpenWordIndex(SetRegister sets, string id, string friendlyName, WordIndexOptions options) {
        if (_indexes.TryGetValue(id, out var existing)) return existing.wrapped; // idempotent re-open
        var folder = Path.Combine(_folderPath, FileKeyUtility.IndexEngine_TextIndexIndexFolderKey(id));
        var index = new TextIndex(sets, id, friendlyName, folder, options, _options, _cache, this);
        // Never registered as just-created: the index carries its own persisted timestamp
        // (0 when fresh), so it is not part of the first-commit protocol.
        RegisterManagedIndex(id, index, justCreated: false);
        // Wrapped here for the same reason as in the other engines: this engine owns the queue
        // lifecycle, flushing the wrapper's queued remove at every commit boundary.
        var optimized = WrapWordIndexAndRegisterQueue(id, index);
        _indexes[id] = (index, optimized);
        return optimized;
    }

    // ---- transactions: publish in memory, persist in MakeDurable -------------------------------
    protected override void BeginTransactionCore() { }
    protected override void CommitTransactionCore(long timestamp) => _currentTimestamp = timestamp;
    // Nothing to undo here: buffered ops written by the failed transaction are cancelled by the
    // compensating actions and the queued removes the base executes on rollback.
    protected override void RollbackTransactionCore() { }
    protected override void MakeDurableCore() {
        foreach (var (index, _) in _indexes.Values) index.Flush(_currentTimestamp, _walFileId, force: false);
    }

    /// <summary>The engine's durable position: the oldest position among its indexes (each persists
    /// its own). 0 with no indexes open, or when any index is fresh â€” forcing a full replay for it.</summary>
    public override long GetTimestamp() {
        if (_indexes.Count == 0) return _currentTimestamp;
        return _indexes.Values.Min(v => v.index.PersistedTimestamp);
    }

    // ---- WAL binding ----------------------------------------------------------------------------
    protected override Guid ReadWalFileId() => _walFileId;
    protected override void WriteWalFileId(Guid walFileId, long? timestamp) {
        _walFileId = walFileId; // in memory first, so the index flushes below stamp the new id
        if (timestamp.HasValue) {
            // Re-stamp every index before the durable marker: a crash in between leaves the marker
            // on the old WAL id, which the next open detects as a mismatch and resets everything.
            foreach (var (index, _) in _indexes.Values) index.Flush(timestamp.Value, walFileId, force: true);
            _currentTimestamp = timestamp.Value;
        }
        writeMarkerFile(walFileId);
    }
    Guid readMarkerFile() {
        var path = Path.Combine(_folderPath, FileKeyUtility.IndexEngine_TextIndexWalIdFileKey);
        try {
            if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out var id)) return id;
        } catch { }
        return Guid.Empty;
    }
    void writeMarkerFile(Guid walFileId) {
        var path = Path.Combine(_folderPath, FileKeyUtility.IndexEngine_TextIndexWalIdFileKey);
        var tmp = FileKeyUtility.TempFileName(path);
        File.WriteAllText(tmp, walFileId.ToString("D"));
        File.Move(tmp, path, overwrite: true);
    }

    // ---- maintenance / lifecycle -----------------------------------------------------------------
    public override void OptimizeDisk() {
        foreach (var (index, _) in _indexes.Values) {
            index.Flush(_currentTimestamp, _walFileId, force: true); // the buffer must join the merge
            index.OptimizeDisk();
        }
    }
    protected override void DeleteUnopenedIndexesCore() {
        // Drops the index folders of word indexes that have left the schema, so a later re-add
        // starts with a fresh, empty index (timestamp 0) instead of stale data claiming to be current.
        var openFolders = _indexes.Keys.Select(FileKeyUtility.IndexEngine_TextIndexIndexFolderKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.GetDirectories(_folderPath)) {
            if (openFolders.Contains(Path.GetFileName(dir))) continue;
            try { Directory.Delete(dir, true); } catch { } // a locked folder is skipped, not fatal
        }
    }
    protected override void ResetAllDataCore() {
        // every open index resets in place (no segments, timestamp 0); dropping the unopened
        // folders covers word indexes that have left the schema. The marker file stays: the
        // base re-writes the WAL id and a timestamp of 0 immediately after this returns.
        foreach (var (index, _) in _indexes.Values) index.ResetToEmpty();
        DeleteUnopenedIndexesCore();
    }
    public override long GetTotalDiskSpace() {
        if (!Directory.Exists(_folderPath)) return 0;
        return Directory.GetFiles(_folderPath, "*", SearchOption.AllDirectories).Sum(f => {
            try { return new FileInfo(f).Length; } catch { return 0L; }
        });
    }
    protected override void DisposeCore() {
        // Any un-flushed buffer is discarded, never flushed here: a clean close has already been
        // made durable by the store's final WAL flush, and after a failed transaction the buffer
        // is not commit-consistent â€” persisting it would put phantom ops ahead of the log. The
        // persisted timestamp still points at the last durable manifest, so the replay repairs it.
        foreach (var (index, _) in _indexes.Values) {
            try { index.Dispose(); } catch { }
        }
    }
}
