using Relatude.DB.AI;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Indexes.VectorIndex;
using Relatude.DB.DataStores.Sets;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Relatude.DB.AI.ISV;

/// <summary>
/// A persistent, disk-based vector index for semantic search: cosine similarity over L2-normalized
/// embeddings of one fixed length (both are enforced), computed as SIMD dot products.
///
/// <para><b>Storage.</b> The index owns a folder with an atomically swapped manifest and a set of
/// immutable segment files. Writes go to an in-memory table (spilled to a segment when it grows
/// past a threshold), reads resolve against a nodeId -&gt; owning-segment map so updates and
/// deletions are simple last-write-wins; similar-sized segments are merged into a size ladder.
/// What stays permanently in memory is O(vectors) small — ids, offsets and centroids — never the
/// vector data itself.</para>
///
/// <para><b>Search.</b> Below <see cref="NativeVectorIndexOptions.MinVectorsForClustering"/> every
/// search is an exact parallel scan. Above it, vectors are k-means clustered (IVF): segments store
/// vectors grouped into per-cluster blocks, and a search ranks all centroids exactly, then probes
/// only the best <see cref="NativeVectorIndexOptions.Accuracy"/> fraction of clusters, reading just
/// those byte ranges. Probed blocks land in a byte-budgeted LRU cache
/// (<see cref="MaxCacheBytes"/>, adjustable at runtime), so hot clusters are served from memory.
/// Scored vectors are always exact full-precision floats — accuracy only controls how many
/// clusters are probed, so results are never approximate in score, only in coverage.</para>
///
/// <para><b>Durability.</b> The manifest records the live files together with the timestamp and
/// WAL file id they correspond to, and is replaced atomically after the files are fsynced. On open,
/// a missing, corrupt or foreign-WAL manifest — or any unreadable data file — resets the index to
/// empty (position 0) and the startup loader replays the missing part of the WAL; partially
/// written data never crashes the process.</para>
/// </summary>
public class NativeVectorIndex : ISemanticIndex, IDisposable {
    const long MemSegmentId = -1; // owner marker for vectors still in the memtable
    readonly SetRegister _sets;
    readonly AIEngine? _ai;
    readonly NativeVectorIndexOptions _options;
    readonly Action<string>? _log;
    readonly string _folder;
    readonly VectorBlockCache _cache;
    readonly ReaderWriterLockSlim _lock = new();
    readonly int _configuredDims;

    // mutable state, guarded by _lock:
    readonly List<VectorSegment> _segments = [];     // oldest first; newer entries win during open
    readonly Dictionary<int, float[]> _memAdds = []; // unflushed adds; always searched exactly
    readonly HashSet<int> _memDels = [];             // unflushed tombstones for persisted records
    readonly Dictionary<int, long> _live = [];       // nodeId -> owning segment id (or MemSegmentId)
    readonly List<string> _pendingRetire = [];       // files merged away, deletable after the next manifest write
    CentroidSet? _centroids;
    long _centroidGeneration;
    int _trainedAtCount;
    int _dims;
    long _memBytes;
    long _nextSegmentId = 1;
    long _persistedTimestamp;
    Guid _persistedWalFileId;
    Guid _walFileId; // the log file this index is currently bound to; stamps MakeDurable manifests
    bool _opened;
    long _stateId = SetRegister.NewStateId();

    public NativeVectorIndex(SetRegister sets, string uniqueKey, string friendlyName, string folderPath,
        AIEngine? ai = null, NativeVectorIndexOptions? options = null, Action<string>? log = null) {
        _sets = sets;
        UniqueKey = uniqueKey;
        FriendlyName = friendlyName;
        _folder = folderPath;
        _ai = ai;
        _options = options ?? new();
        _log = log;
        _cache = new(_options.MaxCacheBytes);
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
    /// <summary>Byte budget of the block cache; adjustable at runtime.</summary>
    public long MaxCacheBytes {
        get => _cache.MaxBytes;
        set {
            _options.MaxCacheBytes = value;
            _cache.MaxBytes = value;
        }
    }
    /// <summary>Fraction of clusters probed per search, see <see cref="NativeVectorIndexOptions.Accuracy"/>.</summary>
    public float Accuracy {
        get => _options.Accuracy;
        set => _options.Accuracy = Math.Clamp(value, 0.01f, 1f);
    }
    /// <summary>Number of vectors currently in the index.</summary>
    public int Count {
        get {
            ensureOpened();
            _lock.EnterReadLock();
            try { return _live.Count; } finally { _lock.ExitReadLock(); }
        }
    }

    // ---- writes --------------------------------------------------------------------------------

    public void Add(int nodeId, object value) => Add(nodeId, (float[])value);
    public void Add(int nodeId, float[] value) {
        ArgumentNullException.ThrowIfNull(value);
        ensureOpened();
        _lock.EnterWriteLock();
        try {
            if (value.Length == 0) { // an empty embedding (empty source text) means nothing searchable
                removeInner(nodeId);
            } else {
                if (_dims == 0) _dims = value.Length; // the first vector locks the dimensions
                else if (value.Length != _dims) throw new ArgumentException($"All vectors must have the same length. The index holds {_dims}-dimensional vectors, got {value.Length}. ");
                if (_options.ValidateNormalized) {
                    var squared = VectorMath.Dot(value, value);
                    if (Math.Abs(squared - 1f) > 0.02f) throw new ArgumentException("Vectors must be L2-normalized (unit length); cosine similarity is computed as a dot product. ");
                }
                _memAdds[nodeId] = value;
                _memDels.Remove(nodeId); // an add supersedes any pending tombstone
                _live[nodeId] = MemSegmentId;
                _memBytes += (long)_dims * 4 + 48;
                if (_memBytes >= _options.MemTableFlushThresholdBytes) {
                    // spill to keep memory bounded during bulk loads; no manifest write, so the
                    // durable position is unchanged and the WAL still covers these ops. Files
                    // retired by the merge are kept until the manifest stops referencing them.
                    writeMemtableSegment();
                    ladderMerge();
                }
            }
            _stateId = SetRegister.NewStateId();
        } finally {
            _lock.ExitWriteLock();
        }
    }
    public void Remove(int nodeId, object value) {
        ensureOpened();
        _lock.EnterWriteLock();
        try {
            removeInner(nodeId);
            _stateId = SetRegister.NewStateId();
        } finally {
            _lock.ExitWriteLock();
        }
    }
    void removeInner(int nodeId) {
        if (_live.Remove(nodeId)) {
            _memAdds.Remove(nodeId);
            // always tombstone: even a memtable-owned vector can shadow an older persisted copy,
            // and a stray tombstone for a never-persisted id is harmless (dropped at full merges)
            _memDels.Add(nodeId);
        }
    }
    public void RegisterAddDuringStateLoad(int nodeId, object value) => Add(nodeId, value);
    public void RegisterRemoveDuringStateLoad(int nodeId, object value) => Remove(nodeId, value);

    // ---- searches ------------------------------------------------------------------------------

    public IdSet SearchForIdSetUnranked(string value, float minimumVectorSimilarity) {
        var vector = embed(value);
        ensureOpened();
        return _sets.SearchSemantic(_stateId, value, minimumVectorSimilarity, () => {
            if (vector.Length == 0) return new HashSet<int>();
            _lock.EnterReadLock();
            try {
                if (_dims == 0 || _live.Count == 0) return new HashSet<int>();
                validateQuery(vector);
                return scanIdsUnranked(vector, clampMinSimilarity(minimumVectorSimilarity), null);
            } finally {
                _lock.ExitReadLock();
            }
        });
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
        _lock.EnterReadLock();
        try {
            if (take == 0 || _dims == 0 || _live.Count == 0) return [];
            validateQuery(vector);
            var minSim = clampMinSimilarity(minCosineSimilarity);
            var target = (long)skip + take;
            var hits = target < _live.Count
                ? scanRankedTopK(vector, (int)target, minSim, accuracy)
                : scanRankedAll(vector, minSim, accuracy);
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

    readonly record struct WorkItem(VectorSegment Seg, VectorSegment.Block Block);
    /// <summary>The (segment, block) pairs a search must scan: everything when unclustered, else
    /// the blocks of the best accuracy-fraction of clusters ranked by exact centroid similarity.</summary>
    List<WorkItem> buildWork(float[] query, float? accuracyOverride) {
        var work = new List<WorkItem>();
        var centroids = _centroids;
        if (centroids == null) {
            foreach (var seg in _segments) {
                foreach (var b in seg.Blocks) work.Add(new(seg, b));
            }
        } else {
            var accuracy = Math.Clamp(accuracyOverride ?? _options.Accuracy, 0.01f, 1f);
            var k = centroids.K;
            var probes = accuracy >= 1f ? k : Math.Clamp((int)MathF.Ceiling(accuracy * k), Math.Min(k, 4), k);
            var order = centroids.RankClusters(query);
            for (var p = 0; p < probes; p++) {
                foreach (var seg in _segments) {
                    if (!seg.TryGetClusterRange(order[p], out var first, out var count)) continue;
                    for (var i = 0; i < count; i++) work.Add(new(seg, seg.Blocks[first + i]));
                }
            }
        }
        return work;
    }
    float[] blockData(WorkItem w) => _cache.GetOrLoad(w.Seg.Id, w.Block.Ordinal, () => w.Seg.ReadVectors(w.Block));
    bool useParallel(List<WorkItem> work) {
        if (work.Count < 2 || Environment.ProcessorCount <= 2) return false;
        long records = 0;
        foreach (var w in work) records += w.Block.Ids.Length;
        return records >= 4096;
    }
    List<VectorHit> scanRankedTopK(float[] query, int k, float minSim, float? accuracy) {
        var work = buildWork(query, accuracy);
        var global = new PriorityQueue<VectorHit, float>(); // min-heap: peek is the worst kept hit
        if (useParallel(work)) {
            var locals = new ConcurrentBag<PriorityQueue<VectorHit, float>>();
            Parallel.ForEach(work,
                () => new PriorityQueue<VectorHit, float>(),
                (w, _, pq) => {
                    scanBlockTopK(query, w, blockData(w), pq, minSim, k);
                    return pq;
                },
                locals.Add);
            foreach (var pq in locals) {
                while (pq.TryDequeue(out var hit, out var priority)) pushTopK(global, hit, priority, k);
            }
        } else {
            foreach (var w in work) scanBlockTopK(query, w, blockData(w), global, minSim, k);
        }
        foreach (var kv in _memAdds) { // the memtable is always scanned, exactly
            var sim = VectorMath.Dot(query, kv.Value);
            if (sim >= minSim) pushTopK(global, new(kv.Key, sim), sim, k);
        }
        var result = new List<VectorHit>(global.Count);
        while (global.TryDequeue(out var h, out _)) result.Add(h);
        return result;
    }
    void scanBlockTopK(float[] query, WorkItem w, float[] data, PriorityQueue<VectorHit, float> pq, float minSim, int k) {
        var ids = w.Block.Ids;
        var segId = w.Seg.Id;
        var dims = _dims;
        var q = query.AsSpan();
        for (var i = 0; i < ids.Length; i++) {
            if (!_live.TryGetValue(ids[i], out var owner) || owner != segId) continue; // superseded or deleted record
            var sim = VectorMath.Dot(q, data.AsSpan(i * dims, dims));
            if (sim >= minSim) pushTopK(pq, new(ids[i], sim), sim, k);
        }
    }
    static void pushTopK(PriorityQueue<VectorHit, float> pq, VectorHit hit, float priority, int k) {
        if (pq.Count < k) {
            pq.Enqueue(hit, priority);
        } else if (pq.TryPeek(out _, out var min) && priority > min) {
            pq.Dequeue();
            pq.Enqueue(hit, priority);
        }
    }
    List<VectorHit> scanRankedAll(float[] query, float minSim, float? accuracy) {
        var work = buildWork(query, accuracy);
        var result = new List<VectorHit>();
        if (useParallel(work)) {
            var locals = new ConcurrentBag<List<VectorHit>>();
            Parallel.ForEach(work,
                () => new List<VectorHit>(),
                (w, _, list) => {
                    scanBlockCollect(query, w, blockData(w), minSim, (id, sim) => list.Add(new(id, sim)));
                    return list;
                },
                locals.Add);
            foreach (var list in locals) result.AddRange(list);
        } else {
            foreach (var w in work) scanBlockCollect(query, w, blockData(w), minSim, (id, sim) => result.Add(new(id, sim)));
        }
        foreach (var kv in _memAdds) {
            var sim = VectorMath.Dot(query, kv.Value);
            if (sim >= minSim) result.Add(new(kv.Key, sim));
        }
        return result;
    }
    HashSet<int> scanIdsUnranked(float[] query, float minSim, float? accuracy) {
        var work = buildWork(query, accuracy);
        var result = new HashSet<int>();
        if (useParallel(work)) {
            var locals = new ConcurrentBag<List<int>>();
            Parallel.ForEach(work,
                () => new List<int>(),
                (w, _, list) => {
                    scanBlockCollect(query, w, blockData(w), minSim, (id, _) => list.Add(id));
                    return list;
                },
                locals.Add);
            foreach (var list in locals) {
                foreach (var id in list) result.Add(id);
            }
        } else {
            foreach (var w in work) scanBlockCollect(query, w, blockData(w), minSim, (id, _) => result.Add(id));
        }
        foreach (var kv in _memAdds) {
            if (VectorMath.Dot(query, kv.Value) >= minSim) result.Add(kv.Key);
        }
        return result;
    }
    void scanBlockCollect(float[] query, WorkItem w, float[] data, float minSim, Action<int, float> hit) {
        var ids = w.Block.Ids;
        var segId = w.Seg.Id;
        var dims = _dims;
        var q = query.AsSpan();
        for (var i = 0; i < ids.Length; i++) {
            if (!_live.TryGetValue(ids[i], out var owner) || owner != segId) continue;
            var sim = VectorMath.Dot(q, data.AsSpan(i * dims, dims));
            if (sim >= minSim) hit(ids[i], sim);
        }
    }

    // ---- flush, merge and training ---------------------------------------------------------------

    // requires the write lock; memtable flush, then the full maintenance a state save may run
    // (centroid training and full merges are too heavy for the WAL-flush path, see MakeDurable)
    void flushMemtableAndMaintain(bool allowTrain) {
        if (allowTrain && _dims != 0) {
            var live = _live.Count;
            if (live >= _options.MinVectorsForClustering &&
                (_centroids == null || live >= (double)_trainedAtCount * _options.RetrainGrowthFactor)) {
                trainAndRewriteAll();
                return;
            }
        }
        if (_memAdds.Count > 0 || _memDels.Count > 0) writeMemtableSegment();
        ladderMerge();
        capMergeIfNeeded();
    }
    void writeMemtableSegment() {
        if (_dims == 0) return;
        var n = _memAdds.Count;
        var dels = _memDels.ToArray();
        if (n == 0 && dels.Length == 0) return;
        var ids = new int[n];
        var vectors = new float[n][];
        var i = 0;
        foreach (var kv in _memAdds) {
            ids[i] = kv.Key;
            vectors[i] = kv.Value;
            i++;
        }
        var clusters = new int[n]; // all cluster 0 when unclustered
        var centroids = _centroids;
        if (centroids != null) {
            if (n >= 256) Parallel.For(0, n, j => clusters[j] = centroids.Assign(vectors[j]));
            else for (var j = 0; j < n; j++) clusters[j] = centroids.Assign(vectors[j]);
        }
        var order = new int[n];
        for (var j = 0; j < n; j++) order[j] = j;
        if (centroids != null) Array.Sort((int[])clusters.Clone(), order); // cluster-sequential appends
        var counts = new Dictionary<int, int>();
        for (var j = 0; j < n; j++) counts[clusters[j]] = counts.GetValueOrDefault(clusters[j]) + 1;
        var newId = _nextSegmentId++;
        using var writer = new VectorSegmentWriter(segmentPath(newId), newId, _dims, _centroidGeneration,
            counts.Select(kv => (kv.Key, kv.Value)).ToList(), dels);
        foreach (var j in order) writer.Append(clusters[j], ids[j], vectors[j]);
        var segment = writer.Finish();
        _segments.Add(segment);
        foreach (var id in ids) _live[id] = segment.Id;
        _memAdds.Clear();
        _memDels.Clear();
        _memBytes = 0;
    }
    void ladderMerge() {
        // size ladder: merge the newest two while they are within 2x of each other; segment sizes
        // then grow geometrically toward the old end and the count stays logarithmic. Each step is
        // bounded by the two newest segments, so this is safe on the hot WAL-flush path.
        while (_segments.Count >= 2 && _segments[^2].FileLength <= _segments[^1].FileLength * 2) {
            mergeRun(_segments.Count - 2, 2);
        }
    }
    // a full merge can rewrite the whole index, so it only runs at state saves, never at WAL flushes
    void capMergeIfNeeded() {
        if (_segments.Count >= _options.MaxSegments) mergeRun(0, _segments.Count);
    }
    /// <summary>Merges <paramref name="count"/> adjacent segments into one that takes the run's
    /// place in the recency order. Only records the live map still points at survive. Tombstones
    /// survive when they may cancel records in segments older than the run; a merge that includes
    /// the oldest segment drops them all.</summary>
    void mergeRun(int start, int count) {
        var run = _segments.GetRange(start, count);
        var runIds = run.Select(s => s.Id).ToHashSet();
        var includesOldest = start == 0;
        var counts = new Dictionary<int, int>();
        foreach (var seg in run) {
            foreach (var b in seg.Blocks) {
                var alive = 0;
                foreach (var id in b.Ids) {
                    if (_live.TryGetValue(id, out var owner) && owner == seg.Id) alive++;
                }
                if (alive > 0) counts[b.ClusterId] = counts.GetValueOrDefault(b.ClusterId) + alive;
            }
        }
        var dels = new HashSet<int>();
        if (!includesOldest) {
            foreach (var seg in run) {
                foreach (var d in seg.DeletedIds) {
                    // keep a tombstone unless its id is live within the run (the re-written add
                    // supersedes it); anything else may still shadow an older record below the run
                    if (!_live.TryGetValue(d, out var owner) || !runIds.Contains(owner)) dels.Add(d);
                }
            }
        }
        if (counts.Count == 0 && dels.Count == 0) { // everything in the run is dead
            _segments.RemoveRange(start, count);
            retire(run);
            return;
        }
        var newId = _nextSegmentId++;
        using var writer = new VectorSegmentWriter(segmentPath(newId), newId, _dims, _centroidGeneration,
            counts.Select(kv => (kv.Key, kv.Value)).ToList(), [.. dels]);
        foreach (var seg in run) {
            foreach (var b in seg.Blocks) {
                float[]? data = null; // blocks without live records are never read
                for (var i = 0; i < b.Ids.Length; i++) {
                    if (!_live.TryGetValue(b.Ids[i], out var owner) || owner != seg.Id) continue;
                    data ??= _cache.TryGet(seg.Id, b.Ordinal, out var cached) ? cached : seg.ReadVectors(b);
                    writer.Append(b.ClusterId, b.Ids[i], data.AsSpan(i * _dims, _dims));
                }
            }
        }
        var merged = writer.Finish();
        _segments.RemoveRange(start, count);
        _segments.Insert(start, merged);
        foreach (var b in merged.Blocks) {
            foreach (var id in b.Ids) _live[id] = merged.Id;
        }
        retire(run);
    }
    void retire(List<VectorSegment> segments) {
        // close handles now, delete the files only after the manifest stops referencing them
        _cache.RemoveSegments(segments.Select(s => s.Id).ToArray());
        foreach (var s in segments) {
            s.Dispose();
            _pendingRetire.Add(s.Path);
        }
    }
    /// <summary>Trains (or retrains) the cluster centroids and rewrites everything into one segment
    /// partitioned by them, so all segments always share the manifest's centroid generation.</summary>
    void trainAndRewriteAll() {
        var sw = Stopwatch.StartNew();
        var live = _live.Count;
        var k = (int)Math.Clamp(live / Math.Max(1, _options.TargetVectorsPerCluster), 16, _options.MaxClusters);
        var sampleTarget = (int)Math.Min(live, Math.Clamp((long)k * 20, 10_000, Math.Max(10_000, _options.KMeansMaxSamples)));
        _log?.Invoke($"Vector index '{FriendlyName}': clustering {live} vectors into ~{k} clusters ({sampleTarget} samples), this may take a while...");
        // sample evenly across all live vectors
        var samples = new List<float[]>(sampleTarget);
        var stride = Math.Max(1, live / sampleTarget);
        var cursor = 0L;
        foreach (var seg in _segments) {
            foreach (var b in seg.Blocks) {
                if (stride <= 3) { // dense sampling: cheaper to read whole blocks
                    float[]? data = null;
                    for (var i = 0; i < b.Ids.Length; i++) {
                        if (!_live.TryGetValue(b.Ids[i], out var owner) || owner != seg.Id) continue;
                        if (cursor++ % stride != 0) continue;
                        data ??= _cache.TryGet(seg.Id, b.Ordinal, out var cached) ? cached : seg.ReadVectors(b);
                        samples.Add(data.AsSpan(i * _dims, _dims).ToArray());
                    }
                } else { // sparse sampling: single-record positional reads
                    for (var i = 0; i < b.Ids.Length; i++) {
                        if (!_live.TryGetValue(b.Ids[i], out var owner) || owner != seg.Id) continue;
                        if (cursor++ % stride != 0) continue;
                        var v = new float[_dims];
                        seg.ReadVector(b, i, v);
                        samples.Add(v);
                    }
                }
            }
        }
        foreach (var kv in _memAdds) {
            if (cursor++ % stride == 0) samples.Add(kv.Value);
        }
        var generation = _centroidGeneration + 1;
        var centroids = CentroidSet.Train(generation, samples, k, _options.KMeansIterations, _log);
        centroids.Write(centroidsPath(generation));
        // assign every live vector to its new cluster, block by block
        var counts = new Dictionary<int, int>();
        var assignments = new List<(VectorSegment seg, VectorSegment.Block b, int[] assign)>();
        foreach (var seg in _segments) {
            foreach (var b in seg.Blocks) {
                var assign = new int[b.Ids.Length];
                var any = false;
                for (var i = 0; i < b.Ids.Length; i++) {
                    var isLive = _live.TryGetValue(b.Ids[i], out var owner) && owner == seg.Id;
                    assign[i] = isLive ? -2 : -1;
                    any |= isLive;
                }
                if (any) {
                    var data = _cache.TryGet(seg.Id, b.Ordinal, out var cached) ? cached : seg.ReadVectors(b);
                    var dims = _dims;
                    Parallel.For(0, assign.Length, i => {
                        if (assign[i] == -2) assign[i] = centroids.Assign(data.AsSpan(i * dims, dims));
                    });
                    foreach (var c in assign) {
                        if (c >= 0) counts[c] = counts.GetValueOrDefault(c) + 1;
                    }
                }
                assignments.Add((seg, b, assign));
            }
        }
        var memIds = new int[_memAdds.Count];
        var memVectors = new float[_memAdds.Count][];
        var m = 0;
        foreach (var kv in _memAdds) {
            memIds[m] = kv.Key;
            memVectors[m] = kv.Value;
            m++;
        }
        var memAssign = new int[m];
        Parallel.For(0, m, i => memAssign[i] = centroids.Assign(memVectors[i]));
        for (var i = 0; i < m; i++) counts[memAssign[i]] = counts.GetValueOrDefault(memAssign[i]) + 1;
        // rewrite everything into one segment; a full rewrite has nothing below it, so no tombstones
        var newId = _nextSegmentId++;
        using var writer = new VectorSegmentWriter(segmentPath(newId), newId, _dims, generation,
            counts.Select(kv => (kv.Key, kv.Value)).ToList(), []);
        foreach (var (seg, b, assign) in assignments) {
            float[]? data = null;
            for (var i = 0; i < assign.Length; i++) {
                if (assign[i] < 0) continue;
                data ??= _cache.TryGet(seg.Id, b.Ordinal, out var cached) ? cached : seg.ReadVectors(b);
                writer.Append(assign[i], b.Ids[i], data.AsSpan(i * _dims, _dims));
            }
        }
        for (var i = 0; i < m; i++) writer.Append(memAssign[i], memIds[i], memVectors[i]);
        var merged = writer.Finish();
        var oldSegments = _segments.ToList();
        var oldGeneration = _centroidGeneration;
        _segments.Clear();
        _segments.Add(merged);
        foreach (var id in _live.Keys) _live[id] = merged.Id;
        _memAdds.Clear();
        _memDels.Clear();
        _memBytes = 0;
        _centroids = centroids;
        _centroidGeneration = generation;
        _trainedAtCount = live;
        _cache.Clear();
        retire(oldSegments);
        if (oldGeneration != 0) _pendingRetire.Add(centroidsPath(oldGeneration));
        _log?.Invoke($"Vector index '{FriendlyName}': clustering completed in {sw.ElapsedMilliseconds}ms ({centroids.K} clusters). ");
    }

    // ---- persistence -----------------------------------------------------------------------------

    string manifestPath => Path.Combine(_folder, "manifest.bin");
    string segmentPath(long id) => Path.Combine(_folder, "seg_" + id.ToString("d16") + ".bin");
    string centroidsPath(long generation) => Path.Combine(_folder, "centroids_" + generation.ToString("d8") + ".bin");

    /// <summary>Durably persists all unflushed writes and re-points the manifest at the result,
    /// stamped with the index's position (timestamp + WAL file id). Never regresses the persisted
    /// timestamp (during replay the store may checkpoint at a position this index is already past).</summary>
    public void SaveStateForMemoryIndexes(long logTimestamp, Guid walFileId) {
        ensureOpened();
        _lock.EnterWriteLock();
        try {
            _walFileId = walFileId;
            if (logTimestamp <= 0) return; // nothing durable to claim yet
            if (logTimestamp < _persistedTimestamp) return;
            flushMemtableAndMaintain(allowTrain: true);
            writeManifest(logTimestamp, walFileId);
            retirePendingFiles();
        } finally {
            _lock.ExitWriteLock();
        }
    }
    /// <summary>
    /// Durably persists all unflushed writes as a new segment stamped at the given log position.
    /// Called right after every successful WAL flush, so the disk index follows the log instead of
    /// waiting for a state save — and since only the changes since the last flush are written, this
    /// is cheap at any index size. Idle flushes cost next to nothing: a clean index only advances
    /// its manifest stamp (sparing a replay at the next open), and not even that when the position
    /// is unchanged. Heavy maintenance (centroid training, full merges) stays on the state-save
    /// path so a WAL flush is never blocked by it; only the bounded newest-two ladder merges run here.
    /// </summary>
    public void MakeDurable(long logTimestamp) {
        ensureOpened();
        _lock.EnterWriteLock();
        try {
            if (logTimestamp <= 0) return; // nothing durable to claim yet
            if (logTimestamp < _persistedTimestamp) return; // never regress the persisted position
            if (_memAdds.Count > 0 || _memDels.Count > 0) {
                writeMemtableSegment();
                ladderMerge();
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
            closeCurrentState();
            try { File.Delete(manifestPath); } catch { }
            deleteStrayFiles(); // with no live segments this removes every data file
            _opened = true;
        } finally {
            _lock.ExitWriteLock();
        }
    }
    /// <summary>After a log rewrite hot-swap the store re-stamps every index. This is called right
    /// after a state save, so the memtable is normally empty; flush defensively since ops stamped
    /// under the old WAL id would otherwise be lost by the re-stamp.</summary>
    public void WriteNewTimestampDueToRewriteHotswap(long newTimestamp, Guid walFileId) {
        ensureOpened();
        _lock.EnterWriteLock();
        try {
            _walFileId = walFileId;
            if (_memAdds.Count > 0 || _memDels.Count > 0) {
                writeMemtableSegment();
                ladderMerge();
            }
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
            flushMemtableAndMaintain(allowTrain: true);
            writeManifest(_persistedTimestamp, _walFileId);
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
    void writeManifest(long timestamp, Guid walFileId) {
        new VectorIndexManifest {
            WalFileId = walFileId,
            Timestamp = timestamp,
            Dimensions = _dims,
            NextSegmentId = _nextSegmentId,
            CentroidGeneration = _centroidGeneration,
            TrainedAtCount = _trainedAtCount,
            SegmentIds = _segments.Select(s => s.Id).ToArray(),
        }.Write(manifestPath);
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
    /// The manifest is only trusted when it belongs to the store's WAL file (when one is required)
    /// and every file it references opens cleanly; otherwise the index holds data of unknown
    /// provenance (foreign log file, torn files) and is reset to empty so the replay rebuilds it
    /// from timestamp 0 instead of duplicating or resurrecting vectors.
    /// </summary>
    void open(Guid? requiredWalFileId) {
        Directory.CreateDirectory(_folder);
        closeCurrentState();
        _walFileId = requiredWalFileId ?? Guid.Empty; // the log file this index is now bound to
        var m = VectorIndexManifest.TryRead(manifestPath);
        if (m != null && (requiredWalFileId == null || (m.WalFileId != Guid.Empty && m.WalFileId == requiredWalFileId.Value))) {
            var segments = new List<VectorSegment>();
            try {
                if (_configuredDims != 0 && m.Dimensions != 0 && m.Dimensions != _configuredDims) {
                    throw new InvalidDataException($"The stored dimensions ({m.Dimensions}) do not match the configured dimensions ({_configuredDims}). ");
                }
                CentroidSet? centroids = null;
                if (m.CentroidGeneration != 0) {
                    centroids = CentroidSet.TryRead(centroidsPath(m.CentroidGeneration), m.CentroidGeneration, m.Dimensions)
                        ?? throw new InvalidDataException("The centroid file is missing or unreadable. ");
                }
                foreach (var id in m.SegmentIds) segments.Add(VectorSegment.Open(segmentPath(id), id, m.Dimensions, m.CentroidGeneration));
                foreach (var seg in segments) { // oldest first: newer adds override, deletions erase
                    foreach (var b in seg.Blocks) {
                        foreach (var id in b.Ids) _live[id] = seg.Id;
                    }
                    foreach (var d in seg.DeletedIds) _live.Remove(d);
                }
                _segments.AddRange(segments);
                if (m.Dimensions != 0) _dims = m.Dimensions;
                _nextSegmentId = m.NextSegmentId;
                _centroids = centroids;
                _centroidGeneration = m.CentroidGeneration;
                _trainedAtCount = m.TrainedAtCount;
                _persistedTimestamp = m.Timestamp;
                _persistedWalFileId = m.WalFileId;
                if (requiredWalFileId == null) _walFileId = m.WalFileId; // standalone: adopt the stored binding
                deleteStrayFiles();
                _opened = true;
                return;
            } catch (Exception err) {
                _log?.Invoke($"Vector index '{FriendlyName}': stored state is unusable ({err.Message}). Resetting for a rebuild from the transaction log. ");
                foreach (var s in segments) s.Dispose();
                closeCurrentState();
            }
        }
        try { File.Delete(manifestPath); } catch { }
        deleteStrayFiles(); // with no live segments this removes every data file
        _opened = true;
    }
    void closeCurrentState() {
        foreach (var s in _segments) s.Dispose();
        _segments.Clear();
        _live.Clear();
        _memAdds.Clear();
        _memDels.Clear();
        _memBytes = 0;
        _pendingRetire.Clear();
        _cache.Clear();
        _centroids = null;
        _centroidGeneration = 0;
        _trainedAtCount = 0;
        _nextSegmentId = 1;
        _persistedTimestamp = 0;
        _persistedWalFileId = Guid.Empty;
        _dims = _configuredDims;
        _stateId = SetRegister.NewStateId();
    }
    void deleteStrayFiles() {
        var live = _segments.Select(s => Path.GetFileName(s.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_centroidGeneration != 0) live.Add(Path.GetFileName(centroidsPath(_centroidGeneration)));
        foreach (var file in Directory.GetFiles(_folder, "seg_*.bin").Concat(Directory.GetFiles(_folder, "centroids_*.bin"))) {
            if (!live.Contains(Path.GetFileName(file))) {
                try { File.Delete(file); } catch { }
            }
        }
        foreach (var file in Directory.GetFiles(_folder, "*.tmp")) {
            try { File.Delete(file); } catch { }
        }
    }
    public long GetTotalDiskSize() {
        ensureOpened();
        _lock.EnterReadLock();
        try {
            long total = 0;
            foreach (var s in _segments) total += s.FileLength;
            try {
                if (File.Exists(manifestPath)) total += new FileInfo(manifestPath).Length;
                if (_centroidGeneration != 0 && File.Exists(centroidsPath(_centroidGeneration))) total += new FileInfo(centroidsPath(_centroidGeneration)).Length;
            } catch { }
            return total;
        } finally {
            _lock.ExitReadLock();
        }
    }

    // ---- lifecycle -------------------------------------------------------------------------------

    public void ClearCache() => _cache.Clear();
    public void CompressMemory() { }
    public void Dispose() {
        _lock.EnterWriteLock();
        try {
            // an unflushed memtable is discarded by design: the WAL covers it and the persisted
            // timestamp still points at the last durable manifest, so a reload replays it
            closeCurrentState();
            _opened = false;
        } finally {
            _lock.ExitWriteLock();
        }
    }
}
