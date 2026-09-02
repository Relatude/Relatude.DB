using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Indexes;

/// <summary>
/// Text index engine backed by Lucene, one index directory per word index under
/// <c>&lt;indexFolder&gt;/lucene</c>. Writes are near-real-time (searchable immediately without a
/// commit), so the engine transaction is in-memory only: <see cref="IndexEngineBase"/>'s
/// CommitTransaction just records the published timestamp, and durability happens in
/// <see cref="MakeDurableCore"/> — called by the data store right after every successful WAL flush —
/// where each index with pending changes commits with its position (timestamp + WAL file id) in
/// the Lucene commit user data. A crash therefore loses only documents the durable log can replay:
/// on the next open every index reports the position of its last durable commit
/// (<see cref="WordIndexLucene.PersistedTimestamp"/>) and the startup loader feeds it exactly the
/// missing transactions.
///
/// <para>The engine-level WAL file id lives in a small marker file next to the index directories.
/// Per index, the commit user data must carry the same id; anything else (legacy index, partial
/// re-bind, restored files) resets that index to empty so the replay rebuilds it — see
/// <see cref="WordIndexLucene"/>.</para>
/// </summary>
public class LuceneTextIndexEngine : IndexEngineBase, ITextIndexEngine {
    readonly string _luceneFolderPath;
    // raw index for lifecycle calls, wrapped for hand-out (and idempotent re-open)
    readonly Dictionary<string, (WordIndexLucene index, IWordIndex wrapped)> _indexes = [];
    Guid _walFileId;
    long _currentTimestamp; // last published (committed) transaction; made durable in MakeDurableCore
    public LuceneTextIndexEngine(string baseIndexFolderPath) : this(baseIndexFolderPath, -1) { }
    /// <summary>
    /// <paramref name="maxMemoryBytes"/> caps each word index's writer RAM buffer, the memory knob
    /// Lucene offers; negative keeps Lucene's default. Lucene refuses a buffer below 1 MB, so that is
    /// the floor a budget of 0 lands on.
    /// </summary>
    public LuceneTextIndexEngine(string baseIndexFolderPath, long maxMemoryBytes) {
        _luceneFolderPath = Path.Combine(baseIndexFolderPath, FileKeyUtility.IndexEngine_LuceneFolderKey);
        if (!Directory.Exists(_luceneFolderPath)) Directory.CreateDirectory(_luceneFolderPath);
        if (maxMemoryBytes >= 0) RamBufferSizeMb = Math.Max(1.0, maxMemoryBytes / (1024.0 * 1024.0));
        _walFileId = readMarkerFile();
    }
    public override string Name => "Lucene";
    /// <summary>The writer RAM buffer every index of this engine is created with; null for Lucene's default.</summary>
    internal double? RamBufferSizeMb { get; }

    /// <summary>The WAL file id the engine's indexes belong to; the indexes read it when they
    /// validate their commit data on open and stamp it on every commit.</summary>
    internal Guid WalFileId => _walFileId;

    public IWordIndex OpenWordIndex(SetRegister sets, string id, string friendlyName, WordIndexOptions options) {
        if (_indexes.TryGetValue(id, out var existing)) return existing.wrapped; // idempotent re-open
        var index = new WordIndexLucene(sets, id, friendlyName, _luceneFolderPath, options, this);
        // Never registered as just-created: a Lucene index carries its own persisted timestamp
        // (0 when fresh), so it is not part of the first-commit protocol.
        RegisterManagedIndex(id, index, justCreated: false);
        // Wrapped here for the same reason as in the value engines: this engine owns the queue
        // lifecycle, flushing the wrapper's queued remove at every commit boundary.
        var optimized = WrapWordIndexAndRegisterQueue(id, index);
        _indexes[id] = (index, optimized);
        return optimized;
    }

    // ---- transactions: publish in memory, persist in MakeDurable -------------------------------
    protected override void BeginTransactionCore() { }
    protected override void CommitTransactionCore(long timestamp) => _currentTimestamp = timestamp;
    // Nothing to undo here: near-real-time documents written by the failed transaction are removed
    // by the compensating actions and the queued removes the base executes on rollback.
    protected override void RollbackTransactionCore() { }
    protected override void MakeDurableCore() {
        foreach (var (index, _) in _indexes.Values) index.Commit(_currentTimestamp);
    }

    /// <summary>The engine's durable position: the oldest position among its indexes (each commits
    /// its own). 0 with no indexes open, or when any index is fresh — forcing a full replay for it.</summary>
    public override long GetTimestamp() {
        if (_indexes.Count == 0) return _currentTimestamp;
        return _indexes.Values.Min(v => v.index.PersistedTimestamp);
    }

    // ---- WAL binding ----------------------------------------------------------------------------
    protected override Guid ReadWalFileId() => _walFileId;
    protected override void WriteWalFileId(Guid walFileId, long? timestamp) {
        _walFileId = walFileId; // in memory first, so the index commits below stamp the new id
        if (timestamp.HasValue) {
            // Re-stamp every index before the durable marker: a crash in between leaves the marker
            // on the old WAL id, which the next open detects as a mismatch and resets everything.
            foreach (var (index, _) in _indexes.Values) index.Commit(timestamp.Value);
            _currentTimestamp = timestamp.Value;
        }
        writeMarkerFile(walFileId);
    }
    Guid readMarkerFile() {
        var path = Path.Combine(_luceneFolderPath, FileKeyUtility.IndexEngine_LuceneWalIdFileKey);
        try {
            if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out var id)) return id;
        } catch { }
        return Guid.Empty;
    }
    void writeMarkerFile(Guid walFileId) {
        var path = Path.Combine(_luceneFolderPath, FileKeyUtility.IndexEngine_LuceneWalIdFileKey);
        var tmp = FileKeyUtility.TempFileName(path);
        File.WriteAllText(tmp, walFileId.ToString("D"));
        File.Move(tmp, path, overwrite: true);
    }

    // ---- maintenance / lifecycle -----------------------------------------------------------------
    public override void OptimizeDisk() {
        foreach (var (index, _) in _indexes.Values) index.OptimizeAndMerge(_currentTimestamp);
    }
    protected override void DeleteUnopenedIndexesCore() {
        // Drops the index directories of word indexes that have left the schema, so a later re-add
        // starts with a fresh, empty index (timestamp 0) instead of stale data claiming to be current.
        var openFolders = _indexes.Keys.Select(FileKeyUtility.IndexEngine_LuceneIndexFolderKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.GetDirectories(_luceneFolderPath)) {
            if (openFolders.Contains(Path.GetFileName(dir))) continue;
            try { Directory.Delete(dir, true); } catch { } // a locked folder is skipped, not fatal
        }
    }
    protected override void ResetAllDataCore() {
        // every open index resets in place (empty directory, timestamp 0); dropping the unopened
        // directories covers word indexes that have left the schema. The marker file stays: the
        // base re-writes the WAL id and a timestamp of 0 immediately after this returns.
        foreach (var (index, _) in _indexes.Values) index.ResetToEmpty();
        DeleteUnopenedIndexesCore();
    }
    public override long GetTotalDiskSpace() {
        if (!Directory.Exists(_luceneFolderPath)) return 0;
        return Directory.GetFiles(_luceneFolderPath, "*", SearchOption.AllDirectories).Sum(f => {
            try { return new FileInfo(f).Length; } catch { return 0L; }
        });
    }
    protected override void DisposeCore() {
        foreach (var (index, _) in _indexes.Values) {
            try { index.Dispose(); } catch { }
        }
    }
}
