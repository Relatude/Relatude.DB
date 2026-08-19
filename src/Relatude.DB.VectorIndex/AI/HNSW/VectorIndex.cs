using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Indexes.VectorIndex;
using Relatude.DB.DataStores.Sets;
using System.Diagnostics;

namespace Relatude.DB.AI.HNSW;

/// <summary>
/// A persistent vector index for semantic search built on an HNSW graph: cosine similarity over
/// L2-normalized embeddings of one fixed length (both are enforced), computed as SIMD dot products.
/// The second take on the idea, built for response time: the graph a search walks — int8 vectors
/// and neighbour lists — always lives in flat, prefetch-friendly memory;
/// <see cref="VectorIndexOptions.MaxMemoryBytes"/> decides whether the float vectors are
/// mirrored in memory beside it or read from their file to re-score final candidates. Same contract
/// and the same durability protocol as the other disk vector indexes.
///
/// <para><b>Search.</b> Vectors are linked into a layered proximity graph. A query enters at the top
/// layer, descends greedily to the query's neighbourhood, then explores a beam of
/// <see cref="VectorIndexOptions.EfSearch"/> candidates at layer 0, scoring int8 copies of the
/// vectors; the final candidates are re-scored against the full-precision floats, so a result is
/// never approximate in score, only in coverage. Below
/// <see cref="VectorIndexOptions.MinVectorsForGraphSearch"/> every search is an exact parallel
/// scan instead, which at those sizes is faster and exact.</para>
///
/// <para><b>Storage.</b> The index owns a folder with an atomically swapped manifest and one
/// generation of data files: the float vectors in one file, the routing records (a node's int8
/// vector <i>together with</i> its layer-0 neighbour list, so one small read serves a graph hop) in
/// another, two small files for the node identities and the layers above 0, and an append-only log
/// of recent edge changes. Keeping the floats apart from the routing graph is what keeps the
/// resident graph at about a quarter of the float data: the walk reads only the small records, and
/// the floats are the optional layer the memory budget dials.</para>
///
/// <para><b>Writes.</b> An insert links the new node into the graph, which rewrites the neighbour
/// lists of the nodes it attached to; changed records are held in memory and written at the next
/// durable checkpoint (or earlier once they pass
/// <see cref="VectorIndexOptions.MemTableFlushThresholdBytes"/>). Those rewrites are scattered
/// all over the file and there are tens of them per inserted vector, so a WAL-flush checkpoint
/// appends them to the edge log in one sequential write and a state save is what folds them into the
/// routing file: the frequent path is the cheap one, and the periodic path carries the cost. A
/// delete only marks the record dead — traversals skip dead ordinals — and the space is reclaimed by
/// a compaction at a state save once the dead share passes
/// <see cref="VectorIndexOptions.CompactionDeadFraction"/>.</para>
///
/// <para><b>Durability.</b> These files are updated in place, so the manifest's record count is what
/// commits them: it records the live generation, its committed record count and the timestamp and
/// WAL file id the data corresponds to, and is replaced atomically after the files are fsynced.
/// Records past that count were allocated after the last checkpoint, so an open ignores them and the
/// replay re-adds those vectors. A missing, corrupt or foreign-WAL manifest — or any unreadable data
/// file, or a graph written with different layout settings — resets the index to empty (position 0)
/// and the startup loader replays the missing part of the WAL; partially written data never crashes
/// the process.</para>
/// </summary>
public class VectorIndex : ISemanticIndex, IDisposable {
    readonly SetRegister _sets;
    readonly AIEngine? _ai;
    readonly VectorIndexOptions _options;
    readonly Action<string>? _log;
    readonly HnswPaths _paths;
    readonly ReaderWriterLockSlim _lock = new();
    readonly int _configuredDims;

    // mutable state, guarded by _lock:
    Graph? _graph;              // null until the dimensions are known (no vector added yet)
    readonly List<string> _pendingRetire = []; // files a compaction replaced, deletable after the next manifest write
    // Replay ops buffered by RegisterAdd/RemoveDuringStateLoad, applied as parallel batches; per id
    // the last op wins and an empty vector means remove. Drained before anything else runs.
    readonly Dictionary<int, float[]> _loadPending = [];
    long _loadPendingBytes;
    long _generation;
    int _dims;
    long _persistedTimestamp;
    Guid _persistedWalFileId;
    Guid _walFileId; // the log file this index is currently bound to; stamps MakeDurable manifests
    bool _opened;
    bool _warnedOverBudget; // the over-budget warning is logged once per open (or budget change)
    long _stateId = SetRegister.NewStateId();

    public VectorIndex(SetRegister sets, string uniqueKey, string friendlyName, string folderPath,
        AIEngine? ai = null, VectorIndexOptions? options = null, Action<string>? log = null) {
        _sets = sets;
        UniqueKey = uniqueKey;
        FriendlyName = friendlyName;
        _paths = new(folderPath);
        _ai = ai;
        _options = options ?? new();
        _log = log;
        _configuredDims = _options.Dimensions ?? ai?.Settings.ModelDimensions ?? 0;
        _dims = _configuredDims;
    }
    public string UniqueKey { get; }
    public string FriendlyName { get; }
    /// <summary>The timestamp of the last durable manifest; 0 for a fresh (or reset) index, which
    /// makes the startup loader rebuild it from the whole WAL.</summary>
    public long PersistedTimestamp => _persistedTimestamp;
    // This index carries its own position in the manifest; nothing to flag on the first commit.
    public void FlagFirstCommit() { }
    /// <summary>The general memory budget (see
    /// <see cref="VectorIndexOptions.MaxMemoryBytes"/>); adjustable at runtime in both
    /// directions: lowering it drops the float mirror once the index no longer affords it, raising
    /// it rebuilds the mirror on the spot (a parallel read of the vector file, the cost of an open).</summary>
    public long MaxMemoryBytes {
        get => _options.MaxMemoryBytes;
        set {
            _lock.EnterWriteLock();
            try {
                _options.MaxMemoryBytes = value;
                _graph?.SetMemoryBudget(value);
                _warnedOverBudget = false; // a changed budget deserves a fresh warning if still over
            } finally {
                _lock.ExitWriteLock();
            }
        }
    }
    /// <summary>Approximately what the index holds in memory right now, all structures included.</summary>
    public long CurrentMemoryBytes => _graph?.MemoryBytes ?? 0;
    /// <summary>The unevictable part of <see cref="CurrentMemoryBytes"/>: the resident routing
    /// graph, the identity table and the unflushed writes — everything but the float mirror. When
    /// this exceeds <see cref="MaxMemoryBytes"/> the index is over budget by design: the graph is
    /// never traded for per-hop disk reads, and a warning is logged instead.</summary>
    public long FloorMemoryBytes => _graph?.FloorBytes ?? 0;
    /// <summary>Search effort as a fraction of <see cref="VectorIndexOptions.EfSearch"/>, see
    /// <see cref="VectorIndexOptions.Accuracy"/>.</summary>
    public float Accuracy {
        get => _options.Accuracy;
        set => _options.Accuracy = Math.Clamp(value, 0.01f, 1f);
    }
    /// <summary>Number of vectors currently in the index.</summary>
    public int Count {
        get {
            ensureOpened();
            drainLoadBufferIfAny();
            _lock.EnterReadLock();
            try { return _graph?.LiveCount ?? 0; } finally { _lock.ExitReadLock(); }
        }
    }

    // ---- writes --------------------------------------------------------------------------------

    public void Add(int nodeId, object value) => Add(nodeId, (float[])value);
    public void Add(int nodeId, float[] value) {
        value = this.EnsureCorrectDimensions(value);    
        ensureOpened();
        _lock.EnterWriteLock();
        try {
            drainLoadBufferLocked(); // buffered replay ops precede this one
            validateAdd(value);
            if (value.Length == 0) { // an empty embedding (empty source text) means nothing searchable
                _graph?.Remove(nodeId);
            } else {
                var graph = ensureGraph();
                graph.Upsert(nodeId, value);
                spillIfNeeded(graph);
                warnIfOverBudget();
            }
            _stateId = SetRegister.NewStateId();
        } finally {
            _lock.ExitWriteLock();
        }
    }
    /// <summary>Adds (or replaces, per id) a batch of vectors, linking them into the graph on every
    /// core — the fast way to load many vectors at once. Within one call the last vector given for
    /// an id wins, exactly as if they had been added one at a time; an empty vector removes the id.
    /// Same durability as <see cref="Add"/>: the WAL covers everything until the next checkpoint.</summary>
    public void AddRange(IEnumerable<(int nodeId, float[] vector)> items) {
        ArgumentNullException.ThrowIfNull(items);
        ensureOpened();
        _lock.EnterWriteLock();
        try {
            drainLoadBufferLocked();
            var batch = new Dictionary<int, float[]>();
            foreach (var (nodeId, vector) in items) {
                ArgumentNullException.ThrowIfNull(vector);
                validateAdd(vector);
                batch[nodeId] = vector; // last write per id wins, like sequential adds
            }
            applyBatchLocked(batch);
            _stateId = SetRegister.NewStateId();
        } finally {
            _lock.ExitWriteLock();
        }
    }
    public void Remove(int nodeId, object value) => Remove(nodeId, (float[])value);
    public void Remove(int nodeId, float[] value) {
        ensureOpened();
        _lock.EnterWriteLock();
        try {
            drainLoadBufferLocked();
            _graph?.Remove(nodeId);
            _stateId = SetRegister.NewStateId();
        } finally {
            _lock.ExitWriteLock();
        }
    }
    /// <summary>The startup loader's add. During a state load nothing reads the index between calls,
    /// so these are buffered and applied as parallel batches — the WAL replay after a cold start or a
    /// reset is a bulk load, and this is what makes it run on every core. Everything is drained
    /// before any other operation touches the index, and the buffered form keeps the replay's
    /// semantics: per id the last operation wins, and an empty vector is a remove.</summary>
    public void RegisterAddDuringStateLoad(int nodeId, object value) {
        var vector = (float[])value;
        ArgumentNullException.ThrowIfNull(vector);
        ensureOpened();
        _lock.EnterWriteLock();
        try {
            validateAdd(vector); // fail at the offending op, exactly like an unbuffered add
            _loadPending[nodeId] = vector;
            _loadPendingBytes += vector.Length * 4L + 32;
            if (_loadPending.Count >= 8192 || _loadPendingBytes >= 64L * 1024 * 1024) drainLoadBufferLocked();
        } finally {
            _lock.ExitWriteLock();
        }
    }
    public void RegisterRemoveDuringStateLoad(int nodeId, object value) {
        ensureOpened();
        _lock.EnterWriteLock();
        try {
            _loadPending[nodeId] = []; // an empty vector is exactly the buffered form of a remove
        } finally {
            _lock.ExitWriteLock();
        }
    }
    /// <summary>Shared validation for every path a vector arrives through: the first vector locks
    /// the dimensions, and an empty one is the "nothing searchable" marker that skips the checks.</summary>
    void validateAdd(float[] value) {
        if (value.Length == 0) return;
        if (_dims == 0) _dims = value.Length; // the first vector locks the dimensions
        else if (value.Length != _dims) throw new ArgumentException($"All vectors must have the same length. The index holds {_dims}-dimensional vectors, got {value.Length}. ");
    }
    void spillIfNeeded(Graph graph) {
        if (graph.DirtyBytes >= _options.ResolvedMemTableFlushThresholdBytes) {
            // spill to keep memory bounded during bulk loads; no manifest write, so the
            // durable position is unchanged and the WAL still covers these ops
            graph.Flush();
        }
    }
    /// <summary>Applies a deduplicated batch: removes first, then the adds in parallel chunks.
    /// Re-applying a partially applied batch is safe — an upsert replaces, a remove misses.</summary>
    void applyBatchLocked(Dictionary<int, float[]> batch) {
        if (batch.Count == 0) return;
        List<(int nodeId, float[] vector)>? adds = null;
        foreach (var (nodeId, vector) in batch) {
            if (vector.Length == 0) _graph?.Remove(nodeId);
            else (adds ??= new(batch.Count)).Add((nodeId, vector));
        }
        if (adds == null) return;
        var graph = ensureGraph();
        // Chunks sized so one chunk's unflushed records stay well inside the memtable budget — the
        // spill check only runs between chunks, and a chunk of unevictable dirty records larger than
        // the budget would leave nothing to spill.
        var recordBytes = Math.Max(1L, (long)_dims * 5 + 300); // the float vector, the int8 copy, edges
        var chunkSize = (int)Math.Clamp(_options.ResolvedMemTableFlushThresholdBytes / 2 / recordBytes, 256, 4096);
        for (var i = 0; i < adds.Count; i += chunkSize) {
            var n = Math.Min(chunkSize, adds.Count - i);
            var chunk = new (int nodeId, float[] vector)[n];
            adds.CopyTo(i, chunk, 0, n);
            graph.UpsertChunk(chunk);
            spillIfNeeded(graph);
        }
        warnIfOverBudget();
    }
    /// <summary>Logs, once per open (or budget change), that the resident graph alone has outgrown
    /// <see cref="VectorIndexOptions.MaxMemoryBytes"/>: the budget's floor is the graph, so the
    /// index runs over budget rather than trading the graph for per-hop disk reads.</summary>
    void warnIfOverBudget() {
        if (_warnedOverBudget || _graph == null) return;
        var floor = _graph.FloorBytes;
        if (floor <= _options.MaxMemoryBytes) return;
        _warnedOverBudget = true;
        _log?.Invoke($"Vector index '{FriendlyName}': the graph needs ~{floor / (1024.0 * 1024):0} MB resident, " +
            $"over the configured MaxMemoryBytes of {_options.MaxMemoryBytes / (1024.0 * 1024):0} MB. " +
            "The graph is kept in memory for search speed, so the index exceeds its budget; raise MaxMemoryBytes to cover it. ");
    }
    void drainLoadBufferLocked() {
        if (_loadPending.Count == 0) return;
        applyBatchLocked(_loadPending);
        _loadPending.Clear();
        _loadPendingBytes = 0;
    }
    /// <summary>For read paths: buffered replay ops must be visible before anything answers. The
    /// unlocked count check is safe because the buffer is only ever non-empty during a state load,
    /// which is single-threaded by the store.</summary>
    void drainLoadBufferIfAny() {
        if (_loadPending.Count == 0) return;
        _lock.EnterWriteLock();
        try {
            drainLoadBufferLocked();
            _stateId = SetRegister.NewStateId();
        } finally {
            _lock.ExitWriteLock();
        }
    }

    // ---- searches ------------------------------------------------------------------------------

    public IdSet SearchForIdSetUnranked(string value, float minimumVectorSimilarity) {
        var vector = embed(value);
        ensureOpened();
        drainLoadBufferIfAny();
        return _sets.SearchSemantic(_stateId, value, minimumVectorSimilarity, () => {
            if (vector.Length == 0) return new HashSet<int>();
            _lock.EnterReadLock();
            try {
                if (_graph == null || _graph.LiveCount == 0) return new HashSet<int>();
                validateQuery(vector);
                return _graph.SearchAbove(vector, clampMinSimilarity(minimumVectorSimilarity), effort(null, 0));
            } finally {
                _lock.ExitReadLock();
            }
        });
    }
    /// <summary>Direct vector search for every node id at or above a similarity, unranked: the vector
    /// form of <see cref="SearchForIdSetUnranked"/>, which embeds its query text and caches its result
    /// set through the store's set register. <paramref name="accuracy"/> overrides
    /// <see cref="Accuracy"/> for this search only.</summary>
    public HashSet<int> SearchAbove(float[] vector, float minCosineSimilarity, float? accuracy = null) {
        ArgumentNullException.ThrowIfNull(vector);
        ensureOpened();
        drainLoadBufferIfAny();
        _lock.EnterReadLock();
        try {
            if (_graph == null || _graph.LiveCount == 0) return [];
            validateQuery(vector);
            return _graph.SearchAbove(vector, clampMinSimilarity(minCosineSimilarity), effort(accuracy, 0));
        } finally {
            _lock.ExitReadLock();
        }
    }
    /// <summary>Text search returning the top ranked hits; mirrors the in-memory semantic index.</summary>
    public List<RawSearchHit> SearchForHitData(string value, int top, int maxHits, float minimumCosineSimilarity, out int totalHits) {
        var vector = embed(value);
        if (vector.Length == 0) {
            totalHits = 0;
            return [];
        }
        var hits = Search(vector, 0, maxHits, minimumCosineSimilarity);
        totalHits = hits.Count;
        var result = new List<RawSearchHit>(Math.Min(top, hits.Count));
        foreach (var hit in hits.Take(top)) result.Add(new() { NodeId = hit.NodeId, Score = hit.Similarity });
        return result;
    }
    /// <summary>Direct vector search: the best matches by cosine similarity, ordered best first.
    /// <paramref name="accuracy"/> overrides <see cref="Accuracy"/> for this search only.</summary>
    public List<VectorHit> Search(float[] vector, int skip, int take, float minCosineSimilarity, float? accuracy = null) {
        ArgumentNullException.ThrowIfNull(vector);
        if (skip < 0) skip = 0;
        if (take < 0) take = 0;
        ensureOpened();
        drainLoadBufferIfAny();
        _lock.EnterReadLock();
        try {
            if (take == 0 || _graph == null || _graph.LiveCount == 0) return [];
            validateQuery(vector);
            var wanted = (int)Math.Min(int.MaxValue, (long)skip + take);
            var hits = _graph.SearchRanked(vector, wanted, clampMinSimilarity(minCosineSimilarity), effort(accuracy, wanted));
            hits.Sort((a, b) => b.Similarity.CompareTo(a.Similarity));
            if (skip > 0) hits.RemoveRange(0, Math.Min(skip, hits.Count));
            if (hits.Count > take) hits.RemoveRange(take, hits.Count - take);
            return hits;
        } finally {
            _lock.ExitReadLock();
        }
    }
    public int MaxCount(string value) => 10; // same planning hint as the in-memory semantic index
    public string GetSample(string search, string sourceText) {
        // more to be done later here, mirroring the in-memory semantic index....
        return sourceText;
    }
    public string GetContextText(string search, string sourceText) {
        // more to be done later here, mirroring the in-memory semantic index....
        return sourceText;
    }
    /// <summary>The beam width one search gets: the configured effort scaled by the accuracy dial,
    /// but never narrower than the page the query asked for — a search that looked at fewer
    /// candidates than it returns hits would answer its own question badly.</summary>
    int effort(float? accuracyOverride, int wanted) {
        var accuracy = Math.Clamp(accuracyOverride ?? _options.Accuracy, 0.01f, 1f);
        var ef = (int)MathF.Ceiling(Math.Max(1, _options.EfSearch) * accuracy);
        return Math.Max(Math.Max(ef, wanted), 1);
    }
    void validateQuery(float[] vector) {
        if (vector.Length != _dims) throw new ArgumentException($"The query vector has {vector.Length} dimensions, the index holds {_dims}-dimensional vectors. ");
    }
    float[] embed(string value) {
        if (_ai == null) throw new InvalidOperationException("No AI engine was provided; text searches are not available. Use the float[] overloads instead. ");
        return _ai.GetEmbeddingsAsync([value]).Result.First();
    }
    // Loosen thresholds at the extremes so float rounding of dot products computed at exactly
    // +/-1 never excludes intended matches (parity with the in-memory index):
    static float clampMinSimilarity(float min) {
        if (min >= 1f) return 0.9999f;
        if (min <= -1f) return -1.0001f;
        return min;
    }

    // ---- persistence -----------------------------------------------------------------------------

    /// <summary>Durably persists all unflushed writes and re-points the manifest at the result,
    /// stamped with the index's position (timestamp + WAL file id). Never regresses the persisted
    /// timestamp (during replay the store may checkpoint at a position this index is already past).</summary>
    public void SaveStateForMemoryIndexes(long logTimestamp, Guid walFileId) {
        ensureOpened();
        _lock.EnterWriteLock();
        try {
            drainLoadBufferLocked(); // the manifest is about to claim the replayed position
            _walFileId = walFileId;
            if (logTimestamp <= 0) return; // nothing durable to claim yet
            if (logTimestamp < _persistedTimestamp) return;
            compactIfNeeded();
            consolidate(logTimestamp, walFileId);
            retirePendingFiles();
        } finally {
            _lock.ExitWriteLock();
        }
    }
    /// <summary>
    /// Durably persists all unflushed writes at the given log position. Called right after every
    /// successful WAL flush, so the disk index follows the log instead of waiting for a state save —
    /// and since only the records the graph actually changed are written, this is cheap at any index
    /// size. Idle flushes cost next to nothing: a clean index only advances its manifest stamp
    /// (sparing a replay at the next open), and not even that when the position is unchanged. The
    /// heavy maintenance — a compaction, which rewrites the whole index — stays on the state-save
    /// path so a WAL flush is never blocked by it.
    /// </summary>
    public void MakeDurable(long logTimestamp) {
        ensureOpened();
        _lock.EnterWriteLock();
        try {
            drainLoadBufferLocked(); // the manifest is about to claim the replayed position
            if (logTimestamp <= 0) return; // nothing durable to claim yet
            if (logTimestamp < _persistedTimestamp) return; // never regress the persisted position
            if (_graph != null && _graph.DirtyBytes > 0) {
                flushAndSync();
            } else if (logTimestamp == _persistedTimestamp && _walFileId == _persistedWalFileId) {
                return; // nothing new and the stamp is current
            }
            writeManifest(logTimestamp, _walFileId);
            retirePendingFiles();
        } finally {
            _lock.ExitWriteLock();
        }
    }
    /// <summary>Wipes the index back to empty (timestamp 0), keeping the WAL binding, so the
    /// startup loader rebuilds it from the whole log. Used by the engine's reset path.</summary>
    internal void ResetToEmpty() {
        _lock.EnterWriteLock();
        try {
            Directory.CreateDirectory(_paths.Folder);
            closeCurrentState();
            try { File.Delete(_paths.Manifest); } catch { }
            deleteStrayFiles(); // with no live generation this removes every data file
            _opened = true;
        } finally {
            _lock.ExitWriteLock();
        }
    }
    /// <summary>After a log rewrite hot-swap the store re-stamps every index. This is called right
    /// after a state save, so there is normally nothing unflushed; flush defensively since ops
    /// stamped under the old WAL id would otherwise be lost by the re-stamp.</summary>
    public void WriteNewTimestampDueToRewriteHotswap(long newTimestamp, Guid walFileId) {
        ensureOpened();
        _lock.EnterWriteLock();
        try {
            drainLoadBufferLocked();
            _walFileId = walFileId;
            flushAndSync();
            writeManifest(newTimestamp, walFileId);
            retirePendingFiles();
        } finally {
            _lock.ExitWriteLock();
        }
    }
    public void ReadStateForMemoryIndexes(Guid walFileId) {
        _lock.EnterWriteLock();
        try {
            open(walFileId);
        } finally {
            _lock.ExitWriteLock();
        }
    }
    /// <summary>Opens (or re-opens) the index from disk without a WAL requirement; for standalone
    /// use outside a data store. Called implicitly by the first operation if never called.</summary>
    public void Open() {
        _lock.EnterWriteLock();
        try {
            open(null);
        } finally {
            _lock.ExitWriteLock();
        }
    }
    /// <summary>Persists all unflushed writes under the current durable position; for standalone
    /// use outside a data store (a store uses <see cref="SaveStateForMemoryIndexes"/>).</summary>
    public void Flush() {
        ensureOpened();
        _lock.EnterWriteLock();
        try {
            drainLoadBufferLocked();
            compactIfNeeded();
            consolidate(_persistedTimestamp, _walFileId);
            retirePendingFiles();
        } finally {
            _lock.ExitWriteLock();
        }
    }
    void ensureOpened() {
        if (_opened) return;
        _lock.EnterWriteLock();
        try {
            if (!_opened) open(null);
        } finally {
            _lock.ExitWriteLock();
        }
    }
    Graph ensureGraph() {
        if (_graph != null) return _graph;
        _generation = _generation == 0 ? 1 : _generation + 1;
        _graph = Graph.Create(_paths, _generation, _dims, _options);
        return _graph;
    }
    /// <summary>The cheap checkpoint: new records to their files, changed neighbour lists appended to
    /// the edge log, everything fsynced, then the manifest claims it — including how much of the log
    /// is durable.</summary>
    void flushAndSync() {
        if (_graph == null) return;
        _graph.Flush();
        _graph.Fsync(); // the data has to be on the disk before the manifest claims it
    }
    /// <summary>
    /// The full checkpoint: the edge log's contents written into the routing file where they belong, so
    /// the log can be dropped. The order is what makes it crash-safe — the log is only discarded after
    /// a manifest that no longer claims any of it, so a crash mid-sequence either replays entries that
    /// are already applied (applying a neighbour list twice changes nothing) or ignores a log the
    /// manifest has already disowned.
    /// </summary>
    void consolidate(long timestamp, Guid walFileId) {
        if (_graph == null) {
            writeManifest(timestamp, walFileId);
            return;
        }
        _graph.FlushAndConsolidate();
        _graph.Fsync();
        writeManifest(timestamp, walFileId); // claims zero edge-log entries
        _graph.DropEdgeLog();
    }
    /// <summary>Reclaims the space of deleted records by rewriting the index into a new generation.
    /// Only worth it once deletions are a real share of the file, and only ever at a state save: it
    /// touches every record, so it must never land on the WAL-flush path.</summary>
    void compactIfNeeded() {
        var graph = _graph;
        if (graph == null) return;
        var dead = graph.DeadCount;
        if (dead < _options.CompactionMinDeadRecords) return;
        if (dead < (graph.LiveCount + dead) * _options.CompactionDeadFraction) return;
        var sw = Stopwatch.StartNew();
        _log?.Invoke($"Vector index '{FriendlyName}': compacting {graph.LiveCount} live and {dead} deleted vectors, this may take a while...");
        var retiring = graph.Paths;
        var compacted = graph.CompactTo(_generation + 1, _paths);
        _graph = compacted;
        _generation = compacted.Generation;
        graph.Dispose();
        _pendingRetire.AddRange(retiring); // deleted once the manifest stops naming them
        _log?.Invoke($"Vector index '{FriendlyName}': compaction completed in {sw.ElapsedMilliseconds}ms. ");
    }
    void writeManifest(long timestamp, Guid walFileId) {
        var graph = _graph;
        var (m, m0, maxLevels) = Graph.Layout(_options);
        new Manifest {
            WalFileId = walFileId,
            Timestamp = timestamp,
            Dimensions = _dims,
            Generation = _generation,
            NextOrdinal = graph?.NextOrdinal ?? 0,
            NextUpperSlot = graph?.NextUpperSlot ?? 0,
            LiveCount = graph?.LiveCount ?? 0,
            DeadCount = graph?.DeadCount ?? 0,
            EntryOrdinal = graph?.EntryOrdinal ?? -1,
            MaxLevel = graph?.MaxLevel ?? -1,
            Connectivity = graph?.Connectivity ?? m,
            ConnectivityLevel0 = graph?.ConnectivityLevel0 ?? m0,
            MaxLevels = graph?.MaxLevels ?? maxLevels,
            EdgeLogEntries = graph?.EdgeLogEntries ?? 0,
        }.Write(_paths.Manifest);
        _persistedTimestamp = timestamp;
        _persistedWalFileId = walFileId;
    }
    void retirePendingFiles() {
        foreach (var path in _pendingRetire) {
            try { File.Delete(path); } catch { } // a locked file becomes a stray for the next open
        }
        _pendingRetire.Clear();
    }
    /// <summary>
    /// The manifest is only trusted when it belongs to the store's WAL file (when one is required),
    /// every file it references opens cleanly, and the graph it describes was laid out with the
    /// settings in force now; otherwise the index holds data of unknown provenance (foreign log file,
    /// torn files, a different graph degree) and is reset to empty so the replay rebuilds it from
    /// timestamp 0 instead of duplicating or resurrecting vectors.
    /// </summary>
    void open(Guid? requiredWalFileId) {
        Directory.CreateDirectory(_paths.Folder);
        closeCurrentState();
        _walFileId = requiredWalFileId ?? Guid.Empty; // the log file this index is now bound to
        var m = Manifest.TryRead(_paths.Manifest);
        if (m != null && (requiredWalFileId == null || (m.WalFileId != Guid.Empty && m.WalFileId == requiredWalFileId.Value))) {
            try {
                if (_configuredDims != 0 && m.Dimensions != 0 && m.Dimensions != _configuredDims) {
                    throw new InvalidDataException($"The stored dimensions ({m.Dimensions}) do not match the configured dimensions ({_configuredDims}). ");
                }
                var (mm, m0, maxLevels) = Graph.Layout(_options);
                if (m.Dimensions != 0 && (m.Connectivity != mm || m.ConnectivityLevel0 != m0 || m.MaxLevels != maxLevels)) {
                    throw new InvalidDataException($"The stored graph is laid out for connectivity {m.Connectivity}/{m.ConnectivityLevel0} over {m.MaxLevels} layers, the configuration asks for {mm}/{m0} over {maxLevels}. ");
                }
                if (m.Dimensions != 0) _graph = Graph.Open(_paths, m, _options);
                _dims = m.Dimensions != 0 ? m.Dimensions : _configuredDims;
                _generation = m.Generation;
                _persistedTimestamp = m.Timestamp;
                _persistedWalFileId = m.WalFileId;
                if (requiredWalFileId == null) _walFileId = m.WalFileId; // standalone: adopt the stored binding
                deleteStrayFiles();
                _opened = true;
                warnIfOverBudget(); // a reopened index can already be past its budget's floor
                return;
            } catch (Exception err) {
                _log?.Invoke($"Vector index '{FriendlyName}': stored state is unusable ({err.Message}). Resetting for a rebuild from the transaction log. ");
                closeCurrentState();
            }
        }
        try { File.Delete(_paths.Manifest); } catch { }
        deleteStrayFiles(); // with no live generation this removes every data file
        _opened = true;
    }
    void closeCurrentState() {
        _graph?.Dispose();
        _graph = null;
        _pendingRetire.Clear();
        _loadPending.Clear(); // unflushed by design: the WAL covers these, a reload replays them
        _loadPendingBytes = 0;
        _generation = 0;
        _persistedTimestamp = 0;
        _persistedWalFileId = Guid.Empty;
        _dims = _configuredDims;
        _warnedOverBudget = false;
        _stateId = SetRegister.NewStateId();
    }
    void deleteStrayFiles() {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _graph?.Paths ?? []) live.Add(Path.GetFileName(path));
        foreach (var file in _paths.AllDataFiles()) {
            if (live.Contains(Path.GetFileName(file))) continue;
            try { File.Delete(file); } catch { }
        }
        foreach (var file in Directory.GetFiles(_paths.Folder, "*.tmp")) {
            try { File.Delete(file); } catch { }
        }
    }
    public long GetTotalDiskSize() {
        ensureOpened();
        _lock.EnterReadLock();
        try {
            var total = _graph?.DiskBytes ?? 0;
            try {
                if (File.Exists(_paths.Manifest)) total += new FileInfo(_paths.Manifest).Length;
            } catch { }
            return total;
        } finally {
            _lock.ExitReadLock();
        }
    }

    // ---- lifecycle -------------------------------------------------------------------------------

    public void ClearCache() { } // nothing is cached: the graph is resident, the floats mirrored or read per search
    public void CompressMemory() { }
    public void Dispose() {
        _lock.EnterWriteLock();
        try {
            // unflushed writes are discarded by design: the WAL covers them and the persisted
            // timestamp still points at the last durable manifest, so a reload replays them
            closeCurrentState();
            _opened = false;
        } finally {
            _lock.ExitWriteLock();
        }
    }

    public bool TryGetNoDimensions(out int dimensions) {
        dimensions = _dims;
        return _dims != 0;
    }
    public void LogWarning(string message) => _ai?.LogCallback?.Invoke(message);

}
