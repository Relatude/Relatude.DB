using Relatude.DB.DataStores.Indexes.VectorIndex;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Relatude.DB.VectorIndexHNSW;

/// <summary>
/// The HNSW graph itself: a hierarchy of navigable small-world layers over the vectors, and the
/// insert, delete and search walks over it. Owns one generation of the files —
/// <see cref="HnswRecordStore"/> for the vectors and layer-0 edges, <see cref="HnswNodeTable"/> for
/// the identity and the layers above, <see cref="HnswEdgeLog"/> for the edges a cheap checkpoint has
/// not yet written into place.
///
/// <para><b>Search.</b> A query enters at the single top-layer node and descends greedily, one layer
/// at a time, each layer taking it closer to the query's neighbourhood; at layer 0 it runs a beam of
/// <c>ef</c> candidates until the beam stops improving. The work is therefore logarithmic in the
/// index size rather than proportional to it, which is where this index differs from probing a
/// fraction of the clusters: an IVF search reads a fixed <i>share</i> of the data, an HNSW search
/// reads a number of nodes that barely grows as the index does.</para>
///
/// <para><b>Scoring.</b> The walk scores candidates against int8 copies of their vectors, quantized
/// once per record at cache admission — a quarter of the memory traffic and several times the SIMD
/// throughput of the float dot. The walk only decides <i>which</i> nodes to consider; the candidates
/// it returns are re-scored against the exact float vectors before anything is returned, so a result
/// is never approximate in score, only in coverage (which <c>ef</c> controls, exactly as before).
/// Below <see cref="HnswVectorIndexOptions.MinVectorsForGraphSearch"/> the walk is skipped entirely
/// for an exact float scan, which at those sizes is both faster and exact.</para>
///
/// <para><b>Inserts.</b> Linking a node searches each layer and then rewrites the neighbour lists of
/// everything it attached to. Each edge is stored with its similarity, so attaching to a non-full
/// node writes two words and reads no vectors, and a full node rejects a poorer challenger against
/// its stored worst edge before loading anything — the selection heuristic, with its pairwise dot
/// products, only runs for challengers that can actually win. Inserts arrive one at a time through
/// <see cref="Upsert"/> or as a batch through <see cref="UpsertChunk"/>, which links its items on
/// every core: node lists are guarded by striped locks, and the searches inside tolerate the same
/// torn neighbour lists a crash can leave — a mix of valid ordinals, never garbage.</para>
///
/// <para><b>What it costs on disk.</b> A graph walk is a chain of dependent random reads — the exact
/// access pattern a disk is worst at. Two things pay for it here: the layers above 0 are resident
/// (or one small positional read in low-memory mode), and at every hop the whole neighbour list is
/// loaded concurrently (see <see cref="HnswRecordStore.Prefetch"/>), so one hop costs one read
/// latency instead of one per neighbour.</para>
/// </summary>
internal sealed class HnswGraph : IDisposable {
    readonly HnswVectorIndexOptions _options;
    readonly HnswRecordStore _store;
    readonly HnswNodeTable _nodes;
    readonly HnswEdgeLog _edges;
    readonly int _dims;
    readonly int _m;        // neighbours per layer above 0
    readonly int _m0;       // neighbours at layer 0
    readonly int _maxLevels;
    readonly double _levelScale;
    readonly Random _rnd;
    // One lock per stripe of ordinals: what makes a batch build's concurrent rewrites of neighbour
    // lists safe. A linker holds at most one stripe at a time, so there is no ordering to deadlock.
    readonly object[] _stripes = new object[256];
    // Walk and insert scratch, pooled: searches run concurrently under the index's read lock, and a
    // batch build runs one inserter per core, each renting its own set.
    readonly ConcurrentBag<HnswSearchScratch> _scratchPool = [];
    readonly ConcurrentBag<HnswInsertScratch> _insertPool = [];
    int _pooledScratches;
    int _pooledInserts;
    // quantized vectors of the routing nodes (layer >= _pinFloor), kept in memory so the descent
    // reads nothing. The arrays are shared with the record entries they came from, so pinning copies
    // nothing; concurrent because searches populate it under the index's read lock.
    readonly ConcurrentDictionary<int, QuantizedRef> _pinned = new();
    readonly object _pinLock = new();
    long _pinnedBytes;
    volatile int _pinFloor = 1;

    public long Generation { get; }
    public int Dimensions => _dims;
    public int Connectivity => _m;
    public int ConnectivityLevel0 => _m0;
    public int MaxLevels => _maxLevels;
    public int LiveCount => _nodes.LiveCount;
    public int DeadCount => _nodes.DeadCount;
    public int NextOrdinal => _nodes.NextOrdinal;
    public int NextUpperSlot => _nodes.NextUpperSlot;
    public int EntryOrdinal => _nodes.EntryOrdinal;
    public int MaxLevel => _nodes.MaxLevel;
    public long DirtyBytes => _store.DirtyBytes + _nodes.DirtyBytes;
    public long DiskBytes => _store.FileLength + _nodes.FileLengths + _edges.FileLength;
    /// <summary>Durable entries of the edge log, for the manifest to stamp.</summary>
    public int EdgeLogEntries => _edges.Entries;
    public long MaxCacheBytes {
        get => _store.MaxCacheBytes;
        set => _store.MaxCacheBytes = value;
    }
    public string[] Paths => [_store.Path, .. _nodes.Paths, _edges.Path];

    HnswGraph(HnswRecordStore store, HnswNodeTable nodes, HnswEdgeLog edges, long generation, int dims, int m, int m0,
        int maxLevels, HnswVectorIndexOptions options) {
        _store = store;
        _nodes = nodes;
        _edges = edges;
        Generation = generation;
        _dims = dims;
        _m = m;
        _m0 = m0;
        _maxLevels = maxLevels;
        _options = options;
        _levelScale = 1.0 / Math.Log(Math.Max(2, m));
        _rnd = new Random(options.RandomSeed ?? 20260811);
        for (var i = 0; i < _stripes.Length; i++) _stripes[i] = new();
    }

    HnswSearchScratch rentScratch() {
        if (!_scratchPool.TryTake(out var s)) return new();
        Interlocked.Decrement(ref _pooledScratches);
        return s;
    }
    void returnScratch(HnswSearchScratch s) {
        if (Interlocked.Increment(ref _pooledScratches) > 16) {
            Interlocked.Decrement(ref _pooledScratches); // over the cap: let this one go to the collector
            return;
        }
        s.Trim();
        _scratchPool.Add(s);
    }
    HnswInsertScratch rentInsert() {
        if (!_insertPool.TryTake(out var ws)) ws = new();
        else Interlocked.Decrement(ref _pooledInserts);
        ws.Prepare(_m0);
        return ws;
    }
    void returnInsert(HnswInsertScratch ws) {
        ws.Search.ParallelPrefetch = true;
        if (Interlocked.Increment(ref _pooledInserts) > 32) {
            Interlocked.Decrement(ref _pooledInserts);
            return;
        }
        ws.Search.Trim();
        _insertPool.Add(ws);
    }
    object stripeOf(int ordinal) => _stripes[ordinal & (_stripes.Length - 1)];

    /// <summary>The layout parameters a set of files is written with; a change to any of them means
    /// the stored files cannot be read and the index has to be rebuilt.</summary>
    internal static (int m, int m0, int maxLevels) Layout(HnswVectorIndexOptions options) {
        var m = Math.Clamp(options.Connectivity, 4, 512);
        var m0 = options.ConnectivityLevel0 > 0 ? Math.Clamp(options.ConnectivityLevel0, m, 1024) : m * 2;
        var maxLevels = Math.Clamp(options.MaxLevels, 2, 16);
        return (m, m0, maxLevels);
    }

    public static HnswGraph Create(HnswPaths paths, long generation, int dims, HnswVectorIndexOptions options) {
        var (m, m0, maxLevels) = Layout(options);
        var store = HnswRecordStore.Create(paths.Graph(generation), generation, dims, m0, options.ResolvedMaxCacheBytes, options.LowMemoryMode);
        HnswNodeTable? nodes = null;
        try {
            nodes = HnswNodeTable.Create(paths.Nodes(generation), paths.Upper(generation), generation, m, maxLevels, options.LowMemoryMode);
            var edges = HnswEdgeLog.Create(paths.Edges(generation), generation, m0);
            return new(store, nodes, edges, generation, dims, m, m0, maxLevels, options);
        } catch {
            nodes?.Dispose();
            store.Dispose();
            throw;
        }
    }
    /// <summary>Opens the generation the manifest points at, laid out with the parameters the manifest
    /// recorded (not the current options — the caller has already checked they agree), and replays the
    /// durable part of the edge log over it.</summary>
    public static HnswGraph Open(HnswPaths paths, HnswManifest m, HnswVectorIndexOptions options) {
        var m0 = m.ConnectivityLevel0;
        var store = HnswRecordStore.Open(paths.Graph(m.Generation), m.Generation, m.Dimensions, m0, options.ResolvedMaxCacheBytes, options.LowMemoryMode, m.NextOrdinal);
        HnswNodeTable? nodes = null;
        try {
            nodes = HnswNodeTable.Open(paths.Nodes(m.Generation), paths.Upper(m.Generation), m.Generation,
                m.Connectivity, m.MaxLevels, options.LowMemoryMode, m.NextOrdinal, m.NextUpperSlot, m.EntryOrdinal, m.MaxLevel);
            var edges = HnswEdgeLog.Open(paths.Edges(m.Generation), m.Generation, m0, m.EdgeLogEntries);
            store.LoadOverlay(edges.Replay(m.EdgeLogEntries));
            return new(store, nodes, edges, m.Generation, m.Dimensions, m.Connectivity, m0, m.MaxLevels, options);
        } catch {
            nodes?.Dispose();
            store.Dispose();
            throw;
        }
    }

    // ---- writes -----------------------------------------------------------------------------------

    /// <summary>Adds a vector, replacing any vector already held under the node id. A replacement is
    /// a delete followed by an insert: HNSW has no in-place move, and re-linking a node whose vector
    /// changed is the same work as inserting it.</summary>
    public void Upsert(int nodeId, ReadOnlySpan<float> vector) {
        if (_nodes.TryGetOrdinal(nodeId, out var existing)) _nodes.Kill(existing);
        var entry = _nodes.EntryOrdinal;
        var topLevel = _nodes.MaxLevel;
        var level = randomLevel();
        var ordinal = _nodes.Allocate(nodeId, level);
        _store.Allocate(ordinal, vector);
        if (entry >= 0 && entry != ordinal && _nodes.IsLive(entry)) {
            var ws = rentInsert();
            try {
                linkInto(ordinal, level, entry, topLevel, ws);
            } finally {
                returnInsert(ws);
            }
        }
        _nodes.PromoteEntry(ordinal); // only now that it is linked can a search enter through it
    }
    /// <summary>
    /// Adds a batch of vectors, linking them on every core. The caller guarantees distinct node ids
    /// within the batch (an <see cref="Upsert"/>-style replace still works across batches and against
    /// existing ids). Allocation is sequential — every item gets its ordinal, record and level first,
    /// so all of them are valid, scoreable nodes — then the linking, which is where all the time goes,
    /// runs in parallel: each worker searches the graph as it is, and the striped locks serialize
    /// rewrites of any one node's list. A search overlapping a rewrite can see a torn list; its
    /// entries are still valid ordinals, which is the same tolerance the files already have for a
    /// crash-torn write. Entry-point promotion happens after the whole chunk, in order, so a search
    /// never enters through a node that is not linked yet.
    /// <para>The graph a batch builds differs run to run in edge choice (the levels stay
    /// deterministic — they are drawn sequentially); recall is statistically the same, but bit-for-bit
    /// reproducibility of the files needs sequential adds.</para>
    /// </summary>
    public void UpsertChunk((int nodeId, float[] vector)[] items) {
        if (items.Length == 0) return;
        var ordinals = new int[items.Length];
        var levels = new int[items.Length];
        for (var i = 0; i < items.Length; i++) {
            if (_nodes.TryGetOrdinal(items[i].nodeId, out var existing)) _nodes.Kill(existing);
            levels[i] = randomLevel();
            ordinals[i] = _nodes.Allocate(items[i].nodeId, levels[i]);
            _store.Allocate(ordinals[i], items[i].vector);
        }
        // Snapshot after allocation: if the graph was empty (or the batch replaced the entry node),
        // the entry is now one of the batch's own nodes, which the loop below skips linking.
        var entry = _nodes.EntryOrdinal;
        var topLevel = _nodes.MaxLevel;
        var workers = buildParallelism();
        if (items.Length < 4 || workers <= 1) {
            var ws = rentInsert();
            try {
                for (var i = 0; i < items.Length; i++) {
                    if (ordinals[i] != entry) linkInto(ordinals[i], levels[i], entry, topLevel, ws);
                }
            } finally {
                returnInsert(ws);
            }
        } else {
            Parallel.For(0, items.Length, new ParallelOptions { MaxDegreeOfParallelism = workers },
                () => {
                    var ws = rentInsert();
                    ws.Search.ParallelPrefetch = false; // every core is an inserter already
                    return ws;
                },
                (i, _, ws) => {
                    if (ordinals[i] != entry) linkInto(ordinals[i], levels[i], entry, topLevel, ws);
                    return ws;
                },
                returnInsert);
        }
        for (var i = 0; i < items.Length; i++) _nodes.PromoteEntry(ordinals[i]);
    }
    /// <summary>How many linkers a batch may run: every worker walks a beam whose working set is
    /// several megabytes of records, and a worker whose records keep getting evicted by the other
    /// workers does not run slower — it stops progressing at all, every probe a file read behind one
    /// shared admission lock. So the cache budget decides: one worker per 16 MB of it, which in
    /// low-memory mode's default is a sequential build (raise <see cref="MaxCacheBytes"/> for the
    /// duration of a bulk load to buy the parallelism back — it is adjustable at runtime). The
    /// default mode's budget affords every core.</summary>
    int buildParallelism() =>
        (int)Math.Clamp(_store.MaxCacheBytes / (16L * 1024 * 1024), 1, Environment.ProcessorCount);
    /// <summary>Links one freshly allocated node into the graph: the descent, the per-layer beam
    /// search, the neighbour selection and the back-edges. Runs concurrently with other linkers
    /// during a batch — everything mutable it touches is either its own scratch or stripe-locked.</summary>
    void linkInto(int ordinal, int level, int entry, int topLevel, HnswInsertScratch ws) {
        if (entry < 0 || !_nodes.IsLive(entry)) return; // the first live node has nothing to link to
        var own = _store.Get(ordinal); // freshly allocated: resident and pinned dirty
        var qq = own.Q;
        var qr = own.Rescale;
        var s = ws.Search;
        var best = entry;
        if (topLevel > level) { // descend greedily to the new node's own top layer
            var bestSim = quantSim(qq, qr, entry);
            for (var l = topLevel; l > level; l--) (best, bestSim) = greedyOnLayer(qq, qr, l, best, bestSim, s);
        }
        var entryPoints = s.EntryPoints;
        entryPoints.Clear();
        entryPoints.Add(best);
        for (var l = Math.Min(topLevel, level); l >= 0; l--) {
            var found = searchLayer(qq, qr, l, Math.Max(_options.EfConstruction, 1), entryPoints, s);
            var max = l == 0 ? _m0 : _m;
            var count = selectNeighbours(found, max, ordinal, ws.UpsertIds, ws.UpsertSims, ws);
            lock (stripeOf(ordinal)) { // a concurrent linker may be appending a back-edge to us
                setNeighbours(ordinal, l, ws.UpsertIds.AsSpan(0, count), ws.UpsertSims.AsSpan(0, count));
            }
            for (var i = 0; i < count; i++) link(ws.UpsertIds[i], ws.UpsertSims[i], l, ordinal, max, ws);
            entryPoints.Clear();
            foreach (var c in found) entryPoints.Add(c.Ordinal); // the whole beam seeds the next layer
            if (entryPoints.Count == 0) entryPoints.Add(entry);
        }
    }
    public bool Remove(int nodeId) {
        if (!_nodes.TryGetOrdinal(nodeId, out var ordinal)) return false;
        // The record and the in-edges pointing at it stay until a compaction; every traversal skips
        // dead ordinals, so the graph answers correctly in the meantime and a delete stays O(1).
        _nodes.Kill(ordinal);
        _pinned.TryRemove(ordinal, out _);
        return true;
    }
    int randomLevel() {
        var r = _rnd.NextDouble();
        if (r <= 0) r = double.Epsilon;
        return Math.Clamp((int)(-Math.Log(r) * _levelScale), 0, _maxLevels - 1);
    }
    void setNeighbours(int ordinal, int level, ReadOnlySpan<int> ids, ReadOnlySpan<float> sims) {
        if (level == 0) _store.SetNeighbours(ordinal, ids, sims);
        else _nodes.SetUpperNeighbours(ordinal, level, ids, sims);
    }
    /// <summary>Adds the reverse edge from a chosen neighbour back to the new node,
    /// <paramref name="simToNew"/> being their (symmetric) similarity, already computed by the
    /// selection that chose the neighbour. The stored edge similarities carry the cost here: a
    /// non-full node appends without reading a single vector, and a full one rejects a challenger no
    /// better than its worst edge the same way. Only a challenger that can win pays for the
    /// re-selection heuristic — which is what keeps a well-connected node from collecting a crowd of
    /// mutually similar edges and losing its long-range ones.</summary>
    void link(int neighbour, float simToNew, int level, int newOrdinal, int max, HnswInsertScratch ws) {
        Span<int> upperBuffer = stackalloc int[_nodes.UpperWords];
        lock (stripeOf(neighbour)) {
            scoped ReadOnlySpan<int> ids;
            scoped ReadOnlySpan<float> sims;
            if (level == 0) {
                var record = _store.Get(neighbour).Record;
                ids = _store.Neighbours(record);
                sims = _store.NeighbourSims(record);
            } else {
                var count0 = _nodes.UpperEdges(neighbour, level, upperBuffer);
                ids = upperBuffer.Slice(1, count0);
                sims = MemoryMarshal.Cast<int, float>(upperBuffer.Slice(1 + _m, count0));
            }
            if (ids.Length < max) {
                ids.CopyTo(ws.LinkIds);
                sims.CopyTo(ws.LinkSims);
                ws.LinkIds[ids.Length] = newOrdinal;
                ws.LinkSims[ids.Length] = simToNew;
                setNeighbours(neighbour, level, ws.LinkIds.AsSpan(0, ids.Length + 1), ws.LinkSims.AsSpan(0, ids.Length + 1));
                return;
            }
            var worst = float.MaxValue;
            foreach (var sim in sims) {
                if (sim < worst) worst = sim;
            }
            if (simToNew <= worst) return; // cannot beat the worst edge: nothing to re-select
            var candidates = ws.LinkCandidates;
            candidates.Clear();
            for (var i = 0; i < ids.Length; i++) {
                if (_nodes.IsLive(ids[i])) candidates.Add(new(ids[i], sims[i]));
            }
            candidates.Add(new(newOrdinal, simToNew));
            var count = selectNeighbours(candidates, max, neighbour, ws.LinkIds, ws.LinkSims, ws);
            setNeighbours(neighbour, level, ws.LinkIds.AsSpan(0, count), ws.LinkSims.AsSpan(0, count));
        }
    }
    /// <summary>
    /// HNSW's neighbour selection heuristic: take candidates best first, but keep one only when it is
    /// closer to the query than to anything already kept. That drops a candidate whose region of the
    /// space is already covered and keeps the diverse, longer-range edges the layers need to stay
    /// navigable — picking the nearest ones instead makes a graph that is locally dense and globally
    /// disconnected. Slots left over are filled with the best of the dropped candidates rather than
    /// leaving a node under-connected. The pairwise comparisons run on the quantized vectors, like
    /// every other routing decision.
    /// </summary>
    int selectNeighbours(List<Candidate> candidates, int max, int self, int[] outIds, float[] outSims, HnswInsertScratch ws) {
        var sorted = ws.SelectSorted;
        sorted.Clear();
        foreach (var c in candidates) {
            if (c.Ordinal != self) sorted.Add(c);
        }
        sorted.Sort(static (a, b) => b.Similarity.CompareTo(a.Similarity));
        if (ws.SelectVectors.Length < sorted.Count) ws.SelectVectors = new QuantizedRef[Math.Max(64, sorted.Count * 2)];
        var q = ws.SelectVectors;
        for (var i = 0; i < sorted.Count; i++) q[i] = scoringOf(sorted[i].Ordinal);
        var selected = 0;
        var kept = ws.SelectKept; // indexes into sorted/q, so nothing is copied
        var dropped = ws.SelectDropped;
        kept.Clear();
        dropped.Clear();
        for (var i = 0; i < sorted.Count && selected < max; i++) {
            var keep = true;
            foreach (var k in kept) {
                var pairwise = VectorMath.DotQ(q[i].Q, q[k].Q) * q[i].Rescale * q[k].Rescale;
                if (pairwise >= sorted[i].Similarity) {
                    keep = false;
                    break;
                }
            }
            if (keep) {
                outIds[selected] = sorted[i].Ordinal;
                outSims[selected] = sorted[i].Similarity;
                selected++;
                kept.Add(i);
            } else if (dropped.Count < max) {
                dropped.Add(i);
            }
        }
        for (var i = 0; i < dropped.Count && selected < max; i++) {
            outIds[selected] = sorted[dropped[i]].Ordinal;
            outSims[selected] = sorted[dropped[i]].Similarity;
            selected++;
        }
        Array.Clear(q, 0, sorted.Count); // held arrays must not outlive the call, or they dodge eviction
        return selected;
    }

    // ---- searches ----------------------------------------------------------------------------------

    /// <summary>The best candidates for a query, unordered and not yet paged — at least
    /// <paramref name="wanted"/> of them where the index has that many above
    /// <paramref name="minSim"/>, scored exactly. Exact below
    /// <see cref="HnswVectorIndexOptions.MinVectorsForGraphSearch"/>, a graph walk above it.</summary>
    public List<VectorHit> SearchRanked(float[] query, int wanted, float minSim, int ef) {
        if (_nodes.LiveCount == 0 || wanted <= 0) return [];
        var width = Math.Max(ef, wanted);
        if (tooWideToWalk(width)) return scanAll(query, minSim);
        var s = rentScratch();
        try {
            s.SetQuery(query);
            var hits = new List<VectorHit>();
            foreach (var c in beamSearch(width, s)) {
                var sim = exactSim(query, c.Ordinal); // the walk found it; the floats score it
                if (sim >= minSim) hits.Add(new(_nodes.NodeIdOf(c.Ordinal), sim));
            }
            return hits;
        } finally {
            returnScratch(s);
        }
    }
    /// <summary>
    /// Every node id at or above a similarity, unranked — the query a semantic filter asks. A graph
    /// answers "the nearest k", so this asks for a k wide enough to contain the answer: run the beam,
    /// and if every candidate in it cleared the threshold then there are more above the threshold than
    /// the beam could hold, so widen it and go again. It stops when the beam comes back with something
    /// below the threshold, which means it has seen the edge of the above-threshold region.
    /// </summary>
    public HashSet<int> SearchAbove(float[] query, float minSim, int ef) {
        var ids = new HashSet<int>();
        if (_nodes.LiveCount == 0) return ids;
        var s = rentScratch();
        try {
            s.SetQuery(query);
            for (var width = Math.Max(ef, 64); ; width = width > int.MaxValue / 4 ? int.MaxValue : width * 4) {
                if (tooWideToWalk(width)) { // the walk would score more vectors than the index holds
                    foreach (var hit in scanAll(query, minSim)) ids.Add(hit.NodeId);
                    return ids;
                }
                var found = beamSearch(width, s);
                var allAbove = true;
                foreach (var c in found) {
                    if (exactSim(query, c.Ordinal) >= minSim) ids.Add(_nodes.NodeIdOf(c.Ordinal));
                    else allAbove = false;
                }
                // a full beam entirely above the floor means the floor is past its edge: widen
                if (found.Count >= width && allAbove) continue;
                return ids;
            }
        } finally {
            returnScratch(s);
        }
    }
    float exactSim(float[] query, int ordinal) => VectorMath.Dot(query, _store.Vector(_store.Get(ordinal).Record));
    /// <summary>
    /// True when an exact scan is the cheaper way to answer a beam this wide — and it often is, because
    /// the two are not merely different amounts of the same work. A scan does one dot product per vector
    /// and nothing else, on every core at once. A walk is a sequential chain, and around each candidate's
    /// dot product it pays a visited-set probe, two heap operations and a residency lookup. So a scan
    /// costs <c>vectors × dims / cores</c> against a walk's <c>width × (dims + bookkeeping)</c>, and
    /// which wins turns on the dimension count: at 128 dimensions the bookkeeping dwarfs the arithmetic
    /// and the scan stays ahead into the hundreds of thousands of vectors, while at 1536 the walk takes
    /// over in the tens of thousands. Preferring the scan where it is faster is free accuracy — it is
    /// also the exact answer. Always true below
    /// <see cref="HnswVectorIndexOptions.MinVectorsForGraphSearch"/>, where the index is small enough
    /// that the scan is the better answer whatever the query asks for.
    /// <para>The constant is a calibration rather than a law: the per-candidate bookkeeping, expressed
    /// in units of one dimension's worth of dot product.</para>
    /// </summary>
    const int walkOverheadInDims = 700;
    bool tooWideToWalk(int width) {
        if (_nodes.LiveCount < _options.MinVectorsForGraphSearch) return true;
        var walkCost = (long)width * (_dims + walkOverheadInDims);
        var scanCost = (long)_nodes.LiveCount * _dims / Math.Max(1, Environment.ProcessorCount);
        return scanCost <= walkCost;
    }
    /// <summary>Descends to layer 0 and explores a beam of <paramref name="width"/> candidates there,
    /// scoring against the scratch's quantized query. Returns them worst first.</summary>
    List<Candidate> beamSearch(int width, HnswSearchScratch s) {
        var entryPoints = descend(s);
        if (entryPoints.Count == 0) {
            s.Found.Clear();
            return s.Found;
        }
        return searchLayer(s.Query, s.QueryRescale, 0, width, entryPoints, s);
    }
    /// <summary>Walks down from the entry point to layer 0 with a beam of one, which is where a graph
    /// search spends its logarithmic budget and — because the routing vectors are pinned — no IO.</summary>
    List<int> descend(HnswSearchScratch s) {
        var entryPoints = s.EntryPoints;
        entryPoints.Clear();
        var entry = _nodes.EntryOrdinal;
        if (entry < 0 || !_nodes.IsLive(entry)) return entryPoints;
        var best = entry;
        var top = _nodes.MaxLevel;
        if (top >= 1) {
            var bestSim = quantSim(s.Query, s.QueryRescale, entry);
            for (var l = top; l >= 1; l--) (best, bestSim) = greedyOnLayer(s.Query, s.QueryRescale, l, best, bestSim, s);
        }
        entryPoints.Add(best);
        return entryPoints;
    }
    /// <summary>A beam of one on an upper layer: move to the best neighbour for as long as one
    /// improves on where we stand. No beam, no visited set — a strictly improving walk cannot cycle,
    /// so the layers above 0 cost a handful of dot products and no bookkeeping at all.</summary>
    (int best, float bestSim) greedyOnLayer(sbyte[] qq, float qr, int level, int current, float currentSim,
        HnswSearchScratch s) {
        Span<int> upperBuffer = stackalloc int[_nodes.UpperWords];
        while (true) {
            var moved = false;
            var neighbours = _nodes.UpperNeighbours(current, level, upperBuffer);
            s.Neighbours.Clear();
            s.ToLoad.Clear();
            foreach (var n in neighbours) {
                if (n < 0 || n >= _nodes.NextOrdinal || !_nodes.IsLive(n)) continue;
                s.Neighbours.Add(n);
                if (!_store.IsResident(n) && !_pinned.ContainsKey(n)) s.ToLoad.Add(n);
            }
            if (s.ParallelPrefetch) _store.Prefetch(s.ToLoad);
            foreach (var n in s.Neighbours) {
                var sim = quantSim(qq, qr, n);
                if (sim > currentSim) {
                    currentSim = sim;
                    current = n;
                    moved = true;
                }
            }
            if (!moved) return (current, currentSim);
        }
    }
    /// <summary>
    /// HNSW's SEARCH-LAYER: expand the most promising unexpanded candidate until the best one left is
    /// worse than the worst result kept, holding at most <paramref name="ef"/> results. Returns them
    /// worst first, so the last entry is the best. The returned list is the scratch's — consumed
    /// before the next searchLayer call on the same scratch, never kept.
    /// </summary>
    List<Candidate> searchLayer(sbyte[] qq, float qr, int level, int ef, List<int> entryPoints,
        HnswSearchScratch s) {
        var candidates = s.Candidates; // best first: priority is the negated similarity
        var results = s.Results;       // worst first, capped at ef
        candidates.Clear();
        results.Clear();
        s.Visited.Clear();
        Span<int> upperBuffer = stackalloc int[_nodes.UpperWords];
        foreach (var ep in entryPoints) {
            if (!_nodes.IsLive(ep) || !s.Visited.Add(ep)) continue;
            var sim = quantSim(qq, qr, ep);
            candidates.Push(-sim, ep);
            results.Push(sim, ep);
        }
        while (results.Count > ef) results.Pop(out _, out _);
        while (candidates.TryPeek(out var negated, out var current)) {
            if (results.Count >= ef && results.TryPeek(out var worst, out _) && -negated < worst) break;
            candidates.Pop(out _, out _);
            var neighbours = level == 0
                ? _store.Neighbours(_store.Get(current).Record)
                : _nodes.UpperNeighbours(current, level, upperBuffer);
            collectUnvisited(neighbours, s);
            if (s.ParallelPrefetch) _store.Prefetch(s.ToLoad); // one read latency for the whole hop
            foreach (var n in s.Neighbours) {
                var sim = quantSim(qq, qr, n);
                if (results.Count < ef) {
                    candidates.Push(-sim, n);
                    results.Push(sim, n);
                } else if (results.TryPeek(out var limit, out _) && sim > limit) {
                    candidates.Push(-sim, n);
                    results.ReplaceTop(sim, n);
                }
            }
        }
        var output = s.Found;
        output.Clear();
        while (results.Count > 0) {
            results.Pop(out var sim, out var ordinal);
            output.Add(new(ordinal, sim));
        }
        return output;
    }
    /// <summary>The live, unvisited neighbours of a node, and the subset of them whose vector is not
    /// already in memory. Neighbour ordinals are validated rather than trusted: a write torn by a
    /// crash — or overlapped by a batch build's concurrent linker — can leave a slot holding a stale
    /// id, and dropping it costs one edge.</summary>
    void collectUnvisited(ReadOnlySpan<int> neighbours, HnswSearchScratch s) {
        s.Neighbours.Clear();
        s.ToLoad.Clear();
        foreach (var n in neighbours) {
            if (n < 0 || n >= _nodes.NextOrdinal) continue;
            if (!_nodes.IsLive(n)) continue;
            if (!s.Visited.Add(n)) continue;
            s.Neighbours.Add(n);
            if (!_store.IsResident(n) && (_nodes.LevelOf(n) == 0 || !_pinned.ContainsKey(n))) s.ToLoad.Add(n);
        }
    }
    /// <summary>
    /// An exact scan of every live record. Used below the graph-search threshold and wherever a walk
    /// would cover more of the index than a scan would (see <see cref="tooWideToWalk"/>). It runs over
    /// ordinal ranges in parallel, and per range it either scores straight out of the records already
    /// in memory or — when some of them are not — reads the whole range in one sequential go and scores
    /// out of that buffer. Always the float vectors: a scan's answer is exact by contract.
    /// </summary>
    List<VectorHit> scanAll(float[] query, float minSim) {
        var count = _nodes.NextOrdinal;
        var result = new List<VectorHit>();
        if (count == 0) return result;
        var onDisk = (int)Math.Min(count, _store.RecordCapacity);
        var words = _store.Words;
        var perChunk = Math.Clamp(256 * 1024 / _store.StrideBytes, 1, 2048);
        var chunks = new List<int>((count + perChunk - 1) / perChunk);
        for (var start = 0; start < count; start += perChunk) chunks.Add(start);
        var locals = new ConcurrentBag<List<VectorHit>>();
        if (chunks.Count > 1 && Environment.ProcessorCount > 2) {
            // read buffers come from the pool: a scan is a hot path that would otherwise allocate a
            // buffer per worker per query, and the GC pressure of that costs more than the scan
            Parallel.ForEach(chunks,
                () => new ScanBuffer(perChunk * words),
                (start, _, buffer) => {
                    scanChunk(query, minSim, start, Math.Min(perChunk, count - start), onDisk, words, buffer);
                    return buffer;
                },
                buffer => {
                    locals.Add(buffer.Hits);
                    buffer.Release();
                });
        } else {
            var buffer = new ScanBuffer(perChunk * words);
            foreach (var start in chunks) scanChunk(query, minSim, start, Math.Min(perChunk, count - start), onDisk, words, buffer);
            locals.Add(buffer.Hits);
            buffer.Release();
        }
        foreach (var hits in locals) result.AddRange(hits);
        return result;
    }
    sealed class ScanBuffer {
        public readonly float[] Records;
        public readonly List<VectorHit> Hits = [];
        public ScanBuffer(int floats) => Records = ArrayPool<float>.Shared.Rent(floats);
        public void Release() => ArrayPool<float>.Shared.Return(Records);
    }
    void scanChunk(float[] query, float minSim, int firstOrdinal, int count, int onDisk, int words, ScanBuffer buffer) {
        if (_options.LowMemoryMode) {
            // The cache is small and keyed, so probing it per record would cost more than it saves.
            // Stream the flushed range straight from the file — a record's vector region is never
            // stale there, only its edges can be — and take just the unflushed tail from memory,
            // where being unflushed pins it.
            var fileValid = Math.Min((long)onDisk - firstOrdinal, (long)_store.FirstUnflushedOrdinal - firstOrdinal);
            var readCount = (int)Math.Clamp(fileValid, 0, count);
            if (readCount > 0) _store.ReadRange(firstOrdinal, readCount, buffer.Records);
            for (var i = 0; i < count; i++) {
                var ordinal = firstOrdinal + i;
                if (!_nodes.IsLive(ordinal)) continue;
                float sim;
                if (i < readCount) sim = VectorMath.Dot(query, buffer.Records.AsSpan(i * words, _dims));
                else if (_store.TryPeek(ordinal, out var record)) sim = VectorMath.Dot(query, _store.Vector(record));
                else continue; // neither in memory nor written yet: not reachable for a live node
                if (sim >= minSim) buffer.Hits.Add(new(_nodes.NodeIdOf(ordinal), sim));
            }
            return;
        }
        var readFrom = 0;
        for (var i = 0; i < count; i++) {
            var ordinal = firstOrdinal + i;
            if (_nodes.IsLive(ordinal) && !_store.TryPeek(ordinal, out _)) {
                readFrom = Math.Max(0, Math.Min(count, onDisk - firstOrdinal));
                break;
            }
        }
        if (readFrom > 0) _store.ReadRange(firstOrdinal, readFrom, buffer.Records);
        for (var i = 0; i < count; i++) {
            var ordinal = firstOrdinal + i;
            if (!_nodes.IsLive(ordinal)) continue;
            float sim;
            if (_store.TryPeek(ordinal, out var record)) sim = VectorMath.Dot(query, _store.Vector(record));
            else if (i < readFrom) sim = VectorMath.Dot(query, buffer.Records.AsSpan(i * words, _dims));
            else continue; // neither in memory nor written yet: not reachable for a live node
            if (sim >= minSim) buffer.Hits.Add(new(_nodes.NodeIdOf(ordinal), sim));
        }
    }

    // ---- the routing vectors kept in memory ----------------------------------------------------------

    /// <summary>The similarity the walk routes by: the int8 dot of the query's and the node's
    /// quantized vectors, brought back to float space by their scales.</summary>
    float quantSim(sbyte[] qq, float qr, int ordinal) {
        var q = scoringOf(ordinal);
        return VectorMath.DotQ(qq, q.Q) * qr * q.Rescale;
    }
    /// <summary>A node's scoring form, from the pinned routing set when it is one of them, else from
    /// the record store — which also pins it when it belongs in the routing set. The arrays are the
    /// record entry's own; pinning shares them rather than copying, and a pinned array stays valid
    /// even after the entry it came from is evicted.</summary>
    QuantizedRef scoringOf(int ordinal) {
        var level = _nodes.LevelOf(ordinal);
        if (level > 0 && _pinned.TryGetValue(ordinal, out var routing)) return routing;
        var entry = _store.Get(ordinal);
        var q = new QuantizedRef(entry.Q, entry.Rescale);
        var floor = _pinFloor;
        if (level >= floor && floor < _maxLevels) pin(ordinal, level, q);
        return q;
    }
    void pin(int ordinal, int level, QuantizedRef q) {
        var bytes = (long)_dims + 64; // the shared int8 array and the map entry
        if (Interlocked.Read(ref _pinnedBytes) + bytes > _options.ResolvedMaxRoutingCacheBytes) {
            raisePinFloor();
            if (level < _pinFloor) return; // this node is no longer worth pinning
        }
        if (_pinned.TryAdd(ordinal, q)) Interlocked.Add(ref _pinnedBytes, bytes);
    }
    /// <summary>Out of budget: stop pinning the lowest pinned layer and release what it held. The
    /// floor only ever rises, so this settles instead of thrashing, and the layers that matter most
    /// for routing — the ones nearest the entry point — are the ones that stay.</summary>
    void raisePinFloor() {
        lock (_pinLock) {
            var bytes = (long)_dims + 64;
            if (Interlocked.Read(ref _pinnedBytes) + bytes <= _options.ResolvedMaxRoutingCacheBytes) return;
            if (_pinFloor >= _maxLevels) return;
            _pinFloor++;
            long freed = 0;
            foreach (var ordinal in _pinned.Keys) {
                if (_nodes.LevelOf(ordinal) >= _pinFloor) continue;
                if (_pinned.TryRemove(ordinal, out _)) freed += bytes;
            }
            Interlocked.Add(ref _pinnedBytes, -freed);
        }
    }

    // ---- persistence and compaction ------------------------------------------------------------------

    /// <summary>The cheap checkpoint, for the WAL-flush path: new records to the graph file, changed
    /// edge regions appended to the edge log.</summary>
    public void Flush() {
        _store.FlushDirty(_edges);
        _nodes.FlushDirty();
    }
    /// <summary>The full checkpoint, for the state-save path: everything written into the files it
    /// belongs in, so the edge log holds nothing the graph file is missing and can be dropped by
    /// <see cref="DropEdgeLog"/> once a manifest no longer claims it.</summary>
    public void FlushAndConsolidate() {
        _store.FlushDirty(null);
        _store.ConsolidateBehind();
        _nodes.FlushDirty();
        _edges.Disown(); // its edges are in the graph file now, so the next manifest claims none
    }
    public void Fsync() {
        _store.Fsync();
        _nodes.Fsync();
        _edges.Fsync();
    }
    /// <summary>Reclaims the edge log's space. Only safe after a manifest claiming no entries has been
    /// written, which is why the index does it as the last step of a state save.</summary>
    public void DropEdgeLog() => _edges.TruncateFile();
    public void ClearCaches() {
        _store.ClearCache();
        _pinned.Clear();
        Interlocked.Exchange(ref _pinnedBytes, 0);
    }
    /// <summary>
    /// Rewrites the index into a new generation of files with the dead records dropped and the
    /// surviving ones renumbered — the graph structure is carried over, not rebuilt, so this costs one
    /// sequential pass rather than a re-insert of everything. Edges to dead nodes are dropped on the
    /// way, which is also what stops a long-running index from carrying its deletions forever.
    /// <para>What it does not do is re-link: a node whose neighbours were all deleted keeps its now
    /// shorter edge list rather than getting new ones, so a graph that has seen very heavy deletion can
    /// lose reachability to some nodes and with it a little recall. That is the standard trade HNSW
    /// makes for an O(1) delete, and the way back from it is a rebuild rather than a compaction.</para>
    /// </summary>
    public HnswGraph CompactTo(long newGeneration, HnswPaths paths) {
        var count = _nodes.NextOrdinal;
        var remap = new int[count];
        var next = 0;
        for (var o = 0; o < count; o++) remap[o] = _nodes.IsLive(o) ? next++ : -1;
        var target = Create(paths, newGeneration, _dims, _options);
        try {
            var idBuffer = new int[Math.Max(_m0, _m)];
            var simBuffer = new float[Math.Max(_m0, _m)];
            var upperBuffer = new int[_nodes.UpperWords];
            for (var o = 0; o < count; o++) {
                if (remap[o] < 0) continue;
                var level = _nodes.LevelOf(o);
                var newOrdinal = target._nodes.Allocate(_nodes.NodeIdOf(o), level);
                // the remap was built by the same ascending walk, so these must agree; if they ever
                // stopped agreeing every rewritten neighbour list would point at the wrong nodes
                if (newOrdinal != remap[o]) throw new InvalidOperationException("The compaction remap does not match the allocation order. ");
                var source = _store.Get(o).Record;
                target._store.Allocate(newOrdinal, _store.Vector(source));
                var n = remapped(_store.Neighbours(source), _store.NeighbourSims(source), remap, idBuffer, simBuffer);
                target._store.SetNeighbours(newOrdinal, idBuffer.AsSpan(0, n), simBuffer.AsSpan(0, n));
                for (var l = 1; l <= level; l++) {
                    var upperCount = _nodes.UpperEdges(o, l, upperBuffer);
                    n = remapped(upperBuffer.AsSpan(1, upperCount),
                        MemoryMarshal.Cast<int, float>(upperBuffer.AsSpan(1 + _m, upperCount)),
                        remap, idBuffer, simBuffer);
                    target._nodes.SetUpperNeighbours(newOrdinal, l, idBuffer.AsSpan(0, n), simBuffer.AsSpan(0, n));
                }
                if (target._store.DirtyBytes + target._nodes.DirtyBytes >= _options.ResolvedMemTableFlushThresholdBytes) {
                    target._store.FlushDirty(null);
                    target._nodes.FlushDirty(); // keeps the pending upper lists bounded in low-memory mode
                }
            }
            target._nodes.RecomputeEntry();
            target.FlushAndConsolidate(); // a fresh generation is written complete: its edge log stays empty
            target.Fsync();
            return target;
        } catch {
            target.Dispose();
            foreach (var path in paths.Generation(newGeneration)) {
                try { File.Delete(path); } catch { }
            }
            throw;
        }
    }
    static int remapped(ReadOnlySpan<int> ids, ReadOnlySpan<float> sims, int[] remap, int[] idBuffer, float[] simBuffer) {
        var n = 0;
        for (var i = 0; i < ids.Length; i++) {
            if (n == idBuffer.Length) break;
            var id = ids[i];
            if (id < 0 || id >= remap.Length) continue;
            var mapped = remap[id];
            if (mapped < 0) continue;
            idBuffer[n] = mapped;
            simBuffer[n] = i < sims.Length ? sims[i] : 0;
            n++;
        }
        return n;
    }
    public void Dispose() {
        _pinned.Clear();
        _store.Dispose();
        _nodes.Dispose();
        _edges.Dispose();
    }
}
