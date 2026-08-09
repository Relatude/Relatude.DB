using Relatude.DB.DataStores.Sets;

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
    const string _markerFileName = "engine.walid";
    readonly string _luceneFolderPath;
    readonly Dictionary<string, IWordIndex> _wordIndexes = [];       // wrapped, handed out; for idempotent re-open
    readonly Dictionary<string, WordIndexLucene> _luceneIndexes = []; // unwrapped, for lifecycle calls
    Guid _walFileId;
    long _currentTimestamp; // last published (committed) transaction; made durable in MakeDurableCore
    public LuceneTextIndexEngine(string baseIndexFolderPath) {
        _luceneFolderPath = Path.Combine(baseIndexFolderPath, "lucene");
        if (!Directory.Exists(_luceneFolderPath)) Directory.CreateDirectory(_luceneFolderPath);
        _walFileId = readMarkerFile();
    }
    public override string Name => "Lucene";

    public IWordIndex OpenWordIndex(SetRegister sets, string id, string friendlyName, WordIndexOptions options) {
        if (_wordIndexes.TryGetValue(id, out var existing)) return existing; // idempotent re-open
        var index = new WordIndexLucene(sets, id, friendlyName, _luceneFolderPath, options, _walFileId);
        // Never registered as just-created: a Lucene index carries its own persisted timestamp
        // (0 when fresh), so it is not part of the first-commit protocol.
        RegisterManagedIndex(id, index, justCreated: false);
        // Wrapped here for the same reason as in the value engines: this engine owns the queue
        // lifecycle, flushing the wrapper's queued remove at every commit boundary.
        var optimized = WrapWordIndexAndRegisterQueue(id, index);
        _luceneIndexes[id] = index;
        _wordIndexes[id] = optimized;
        return optimized;
    }

    // ---- transactions: publish in memory, persist in MakeDurable -------------------------------
    protected override void BeginTransactionCore() { }
    protected override void CommitTransactionCore(long timestamp) => _currentTimestamp = timestamp;
    // Nothing to undo here: near-real-time documents written by the failed transaction are removed
    // by the compensating actions and the queued removes the base executes on rollback.
    protected override void RollbackTransactionCore() { }
    protected override void MakeDurableCore() {
        foreach (var w in _luceneIndexes.Values) w.Commit(_currentTimestamp, _walFileId);
    }

    /// <summary>The engine's durable position: the oldest position among its indexes (each commits
    /// its own). 0 with no indexes open, or when any index is fresh — forcing a full replay for it.</summary>
    public override long GetTimestamp() {
        if (_luceneIndexes.Count == 0) return _currentTimestamp;
        return _luceneIndexes.Values.Min(i => i.PersistedTimestamp);
    }

    // ---- WAL binding ----------------------------------------------------------------------------
    protected override Guid ReadWalFileId() => _walFileId;
    protected override void WriteWalFileId(Guid walFileId, long? timestamp) {
        if (timestamp.HasValue) {
            // Re-stamp every index first, the marker last: a crash in between leaves the marker on
            // the old WAL id, which the next open detects as a mismatch and resets everything.
            foreach (var w in _luceneIndexes.Values) w.Commit(timestamp.Value, walFileId);
            _currentTimestamp = timestamp.Value;
        }
        _walFileId = walFileId;
        writeMarkerFile(walFileId);
    }
    Guid readMarkerFile() {
        var path = Path.Combine(_luceneFolderPath, _markerFileName);
        try {
            if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out var id)) return id;
        } catch { }
        return Guid.Empty;
    }
    void writeMarkerFile(Guid walFileId) {
        var path = Path.Combine(_luceneFolderPath, _markerFileName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, walFileId.ToString("D"));
        File.Move(tmp, path, overwrite: true);
    }

    // ---- maintenance / lifecycle -----------------------------------------------------------------
    protected override void OptimizeDiskCore() {
        foreach (var w in _luceneIndexes.Values) w.OptimizeAndMerge(_currentTimestamp, _walFileId);
    }
    protected override void DeleteUnopenedIndexesCore() {
        // Drops the index directories of word indexes that have left the schema, so a later re-add
        // starts with a fresh, empty index (timestamp 0) instead of stale data claiming to be current.
        var openFolders = _luceneIndexes.Keys.Select(WordIndexLucene.GetFolderName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.GetDirectories(_luceneFolderPath)) {
            if (openFolders.Contains(Path.GetFileName(dir))) continue;
            try { Directory.Delete(dir, true); } catch { } // a locked folder is skipped, not fatal
        }
    }
    protected override void ResetAllDataCore() {
        foreach (var w in _luceneIndexes.Values) w.Close();
        // wipe everything under the lucene folder, marker included — the base re-writes the WAL id
        // and a timestamp of 0 immediately after this returns
        foreach (var dir in Directory.GetDirectories(_luceneFolderPath)) {
            try { Directory.Delete(dir, true); } catch { }
        }
        foreach (var file in Directory.GetFiles(_luceneFolderPath)) {
            try { File.Delete(file); } catch { }
        }
        foreach (var w in _luceneIndexes.Values) w.Open(_walFileId);
    }
    public override long GetTotalDiskSpace() {
        if (!Directory.Exists(_luceneFolderPath)) return 0;
        return Directory.GetFiles(_luceneFolderPath, "*", SearchOption.AllDirectories).Sum(f => {
            try { return new FileInfo(f).Length; } catch { return 0L; }
        });
    }
    protected override void DisposeCore() {
        foreach (var w in _luceneIndexes.Values) {
            try { w.Dispose(); } catch { }
        }
    }
}
