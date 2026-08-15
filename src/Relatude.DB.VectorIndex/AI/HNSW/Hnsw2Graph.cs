using Relatude.DB.DataStores.Indexes.VectorIndex;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Relatude.DB.AI.HNSW;

/// <summary>
/// The HNSW graph itself: a hierarchy of navigable small-world layers over the vectors, and the
/// insert, delete and search walks over it. Owns one generation of the files —
/// <see cref="Hnsw2FloatStore"/> for the float vectors, <see cref="Hnsw2RoutingStore"/> for the int8
/// vectors and layer-0 edges, <see cref="Hnsw2NodeTable"/> for the identity and the layers above,
/// <see cref="Hnsw2EdgeLog"/> for the edges a cheap checkpoint has not yet written into place.
///
/// <para><b>Search.</b> A query enters at the single top-layer node and descends greedily, one layer
/// at a time; at layer 0 it runs a beam of <c>ef</c> candidates until the beam stops improving. The
/// walk scores candidates against int8 vectors — a quarter of the memory traffic, several times the
/// SIMD throughput — and the candidates it returns are re-scored against the float vectors before
/// anything is returned, so a result is never approximate in score, only in coverage (which
/// <c>ef</c> controls). With the graph resident the walk is pure memory: no residency checks, no
/// hashing, and the next candidates' vectors are prefetched while the current one is scored. Below
/// <see cref="HnswVectorIndexOptions.MinVectorsForGraphSearch"/> — or wherever a scan would be
/// cheaper than the walk — every search is an exact parallel scan instead.</para>
///
/// <para><b>Inserts.</b> Linking a node searches each layer and then rewrites the neighbour lists of
/// everything it attached to. Each edge is stored with its similarity, so attaching to a non-full
/// node writes two words and reads no vectors, and a full node rejects a poorer challenger against
/// its stored worst edge before loading anything — the selection heuristic, with its pairwise dot
/// products, only runs for challengers that can actually win. Inserts arrive one at a time through
/// <see cref="Upsert"/> or as a batch through <see cref="UpsertChunk"/>, which links its items on
/// every core (bounded by <see cref="HnswVectorIndexOptions.MaxThreads"/>): node lists are guarded
/// by striped locks, and the searches inside tolerate the same torn neighbour lists a crash can
/// leave — a mix of valid ordinals, never garbage.</para>
///
/// <para><b>Memory.</b> The graph spends <see cref="HnswVectorIndexOptions.MaxMemoryBytes"/> in
/// order of what memory buys most: the routing graph first, the float mirror second. An index that
/// outgrows the float mirror drops it mid-run and re-scores from the file instead; an index whose
/// routing graph would not fit at open (or one whose budget is at or below
/// <see cref="HnswVectorIndexOptions.LowMemoryThresholdBytes"/>) runs with the graph on disk, read
/// through a small cache — and because a routing record is a quarter of a float record, that cache
/// holds four times the nodes per byte that caching the floats would.</para>
/// </summary>
internal sealed class Hnsw2Graph : IDisposable {
    readonly HnswVectorIndexOptions _options;
    readonly Hnsw2FloatStore _floats;
    readonly Hnsw2RoutingStore _routing;
    readonly Hnsw2NodeTable _nodes;
    readonly Hnsw2EdgeLog _edges;
    readonly int _dims;
    readonly int _m;        // neighbours per layer above 0
    readonly int _m0;       // neighbours at layer 0
    readonly int _maxLevels;
    readonly int _threads;
    readonly double _levelScale;
    readonly Random _rnd;
    // One lock per stripe of ordinals: what makes a batch build's concurrent rewrites of neighbour
    // lists safe. A linker holds at most one stripe at a time, so there is no ordering to deadlock.
    readonly object[] _stripes = new object[256];
    // Walk and insert scratch, pooled: searches run concurrently under the index's read lock, and a
    // batch build runs one inserter per core, each renting its own set.
    readonly ConcurrentBag<Hnsw2SearchScratch> _scratchPool = [];
    readonly ConcurrentBag<Hnsw2InsertScratch> _insertPool = [];
    int _pooledScratches;
    int _pooledInserts;
    long _memoryBudget;

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
    public long DirtyBytes => _routing.DirtyBytes + _nodes.DirtyBytes + _floats.UnflushedBytes;
    public long DiskBytes => _floats.FileLength + _routing.FileLength + _nodes.FileLengths + _edges.FileLength;
    /// <summary>Durable entries of the edge log, for the manifest to stamp.</summary>
    public int EdgeLogEntries => _edges.Entries;
    /// <summary>Approximately what the graph holds in memory right now, all structures included.</summary>
    public long MemoryBytes => _routing.ResidentBytes + _floats.MirrorBytes + _floats.TailBytes + _nodes.ResidentBytes;
    /// <summary>Whether the routing graph is resident in memory (else it is read through a cache).</summary>
    public bool GraphResident => _routing.Resident;
    public string[] Paths => [_floats.Path, _routing.Path, .. _nodes.Paths, _edges.Path];

    Hnsw2Graph(Hnsw2FloatStore floats, Hnsw2RoutingStore routing, Hnsw2NodeTable nodes, Hnsw2EdgeLog edges,
        long generation, int dims, int m, int m0, int maxLevels, HnswVectorIndexOptions options) {
        _floats = floats;
        _routing = routing;
        _nodes = nodes;
        _edges = edges;
        Generation = generation;
        _dims = dims;
        _m = m;
        _m0 = m0;
        _maxLevels = maxLevels;
        _options = options;
        _threads = Math.Min(options.ResolvedMaxThreads, Environment.ProcessorCount);
        _memoryBudget = options.MaxMemoryBytes;
        _levelScale = 1.0 / Math.Log(Math.Max(2, m));
        _rnd = new Random(options.RandomSeed ?? 20260813);
        for (var i = 0; i < _stripes.Length; i++) _stripes[i] = new();
    }

    Hnsw2SearchScratch rentScratch() {
        if (!_scratchPool.TryTake(out var s)) return new();
        Interlocked.Decrement(ref _pooledScratches);
        return s;
    }
    void returnScratch(Hnsw2SearchScratch s) {
        if (Interlocked.Increment(ref _pooledScratches) > 16) {
            Interlocked.Decrement(ref _pooledScratches); // over the cap: let this one go to the collector
            return;
        }
        s.Trim();
        _scratchPool.Add(s);
    }
    Hnsw2InsertScratch rentInsert() {
        if (!_insertPool.TryTake(out var ws)) ws = new();
        else Interlocked.Decrement(ref _pooledInserts);
        ws.Prepare(_m0);
        return ws;
    }
    void returnInsert(Hnsw2InsertScratch ws) {
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
    /// <summary>What one vector costs in the resident graph: its routing record, its identity words
    /// and its share of the upper layers and the id map. The budget arithmetic's unit.</summary>
    static long graphBytesPerVector(int dims, int m, int m0) {
        var routingStride = dims + ((4 - (dims & 3)) & 3) + 4 + 4 * (1 + 2 * m0);
        var upperShare = (long)(1 + 2 * m) * 4 / Math.Max(2, m - 1); // ~1/(m-1) of nodes reach layer 1+
        return routingStride + 12 + 16 + upperShare;
    }
    /// <summary>Decides where an index of <paramref name="vectors"/> vectors sits in memory under the
    /// options' budget: the routing graph resident when it fits (never with a budget at or below
    /// <see cref="HnswVectorIndexOptions.LowMemoryThresholdBytes"/> — a budget that small says
    /// footprint is the point, and a resident graph cannot be evicted once it grows past it), the
    /// float vectors mirrored too when the whole thing fits.</summary>
    static (bool resident, bool mirrorFloats) residency(HnswVectorIndexOptions options, int vectors, int dims, int m, int m0) {
        if (options.LowMemoryMode) return (false, false);
        var budget = options.MaxMemoryBytes;
        var core = (long)vectors * graphBytesPerVector(dims, m, m0);
        if (core > budget) return (false, false);
        return (true, core + (long)vectors * dims * 4 <= budget);
    }
    /// <summary>Cached mode's eviction budget: most of the general budget, leaving a slice for the
    /// identity table, the pending writes and the scratch.</summary>
    static long cacheBudgetOf(long memoryBudget) => Math.Max(2L * 1024 * 1024, memoryBudget * 3 / 4);

    public static Hnsw2Graph Create(Hnsw2Paths paths, long generation, int dims, HnswVectorIndexOptions options) {
        var (m, m0, maxLevels) = Layout(options);
        var (resident, mirror) = residency(options, 0, dims, m, m0);
        var floats = Hnsw2FloatStore.Create(paths.Vectors(generation), generation, dims, mirror);
        Hnsw2RoutingStore? routing = null;
        Hnsw2NodeTable? nodes = null;
        try {
            routing = Hnsw2RoutingStore.Create(paths.Routing(generation), generation, dims, m0, resident, cacheBudgetOf(options.MaxMemoryBytes));
            nodes = Hnsw2NodeTable.Create(paths.Nodes(generation), paths.Upper(generation), generation, m, maxLevels, upperOnDisk: !resident);
            var edges = Hnsw2EdgeLog.Create(paths.Edges(generation), generation, m0);
            return new(floats, routing, nodes, edges, generation, dims, m, m0, maxLevels, options);
        } catch {
            nodes?.Dispose();
            routing?.Dispose();
            floats.Dispose();
            throw;
        }
    }
    /// <summary>Opens the generation the manifest points at, laid out with the parameters the manifest
    /// recorded (not the current options — the caller has already checked they agree), and replays the
    /// durable part of the edge log over it.</summary>
    public static Hnsw2Graph Open(Hnsw2Paths paths, Hnsw2Manifest m, HnswVectorIndexOptions options) {
        var m0 = m.ConnectivityLevel0;
        var threads = Math.Min(options.ResolvedMaxThreads, Environment.ProcessorCount);
        var (resident, mirror) = residency(options, m.NextOrdinal, m.Dimensions, m.Connectivity, m0);
        var floats = Hnsw2FloatStore.Open(paths.Vectors(m.Generation), m.Generation, m.Dimensions, mirror, m.NextOrdinal, threads);
        Hnsw2RoutingStore? routing = null;
        Hnsw2NodeTable? nodes = null;
        try {
            routing = Hnsw2RoutingStore.Open(paths.Routing(m.Generation), m.Generation, m.Dimensions, m0, resident,
                cacheBudgetOf(options.MaxMemoryBytes), m.NextOrdinal, threads);
            nodes = Hnsw2NodeTable.Open(paths.Nodes(m.Generation), paths.Upper(m.Generation), m.Generation,
                m.Connectivity, m.MaxLevels, upperOnDisk: !resident, m.NextOrdinal, m.NextUpperSlot, m.EntryOrdinal, m.MaxLevel);
            var edges = Hnsw2EdgeLog.Open(paths.Edges(m.Generation), m.Generation, m0, m.EdgeLogEntries);
            routing.LoadOverlay(edges.Replay(m.EdgeLogEntries));
            return new(floats, routing, nodes, edges, m.Generation, m.Dimensions, m.Connectivity, m0, m.MaxLevels, options);
        } catch {
            nodes?.Dispose();
            routing?.Dispose();
            floats.Dispose();
            throw;
        }
    }

    /// <summary>Applies a changed memory budget at runtime: the cached mode's eviction budget follows
    /// it, and a float mirror the index no longer affords is dropped. What a smaller budget cannot do
    /// is un-mirror the resident routing graph mid-run — that residency is decided when the index
    /// opens, and documented as such.</summary>
    public void SetMemoryBudget(long budget) {
        _memoryBudget = budget;
        _routing.MaxCacheBytes = cacheBudgetOf(budget);
        enforceBudget();
    }
    /// <summary>Drops the float mirror once the index has outgrown the budget; called after
    /// allocations. Re-scoring then reads the vector file, which is the designed degradation.</summary>
    void enforceBudget() {
        if (_floats.Mirrored && MemoryBytes > _memoryBudget) _floats.DropMirror();
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
        _floats.Allocate(ordinal, vector);
        _routing.Allocate(ordinal, vector);
        if (entry >= 0 && entry != ordinal && _nodes.IsLive(entry)) {
            var ws = rentInsert();
            try {
                linkInto(ordinal, level, entry, topLevel, ws);
            } finally {
                returnInsert(ws);
            }
        }
        _nodes.PromoteEntry(ordinal); // only now that it is linked can a search enter through it
        enforceBudget();
    }
    /// <summary>
    /// Adds a batch of vectors, linking them on every core. The caller guarantees distinct node ids
    /// within the batch (an <see cref="Upsert"/>-style replace still works across batches and against
    /// existing ids). Allocation is sequential — every item gets its ordinal, records and level first,
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
            _floats.Allocate(ordinals[i], items[i].vector);
            _routing.Allocate(ordinals[i], items[i].vector);
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
        enforceBudget();
    }
    /// <summary>How many linkers a batch may run. With the graph resident, one per core up to
    /// <see cref="HnswVectorIndexOptions.MaxThreads"/>. With the graph cached, every worker walks a
    /// beam whose working set is several megabytes of records, and a worker whose records keep
    /// getting evicted by the other workers does not run slower — it stops progressing at all — so
    /// the cache budget decides: one worker per 16 MB of it (raise
    /// <see cref="HnswVectorIndex.MaxMemoryBytes"/> for the duration of a bulk load to buy the
    /// parallelism back — it is adjustable at runtime).</summary>
    int buildParallelism() {
        if (_routing.Resident) return _threads;
        return (int)Math.Clamp(_routing.MaxCacheBytes / (16L * 1024 * 1024), 1, _threads);
    }
    /// <summary>Links one freshly allocated node into the graph: the descent, the per-layer beam
    /// search, the neighbour selection and the back-edges. Runs concurrently with other linkers
    /// during a batch — everything mutable it touches is either its own scratch or stripe-locked.</summary>
    void linkInto(int ordinal, int level, int entry, int topLevel, Hnsw2InsertScratch ws) {
        if (entry < 0 || !_nodes.IsLive(entry)) return; // the first live node has nothing to link to
        var own = _routing.Get(ordinal); // freshly allocated: always in memory
        var qq = _routing.Q(own);
        var qr = _routing.Rescale(own);
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
        return true;
    }
    int randomLevel() {
        var r = _rnd.NextDouble();
        if (r <= 0) r = double.Epsilon;
        return Math.Clamp((int)(-Math.Log(r) * _levelScale), 0, _maxLevels - 1);
    }
    void setNeighbours(int ordinal, int level, ReadOnlySpan<int> ids, ReadOnlySpan<float> sims) {
        if (level == 0) _routing.SetNeighbours(ordinal, ids, sims);
        else _nodes.SetUpperNeighbours(ordinal, level, ids, sims);
    }
    /// <summary>Adds the reverse edge from a chosen neighbour back to the new node,
    /// <paramref name="simToNew"/> being their (symmetric) similarity, already computed by the
    /// selection that chose the neighbour. The stored edge similarities carry the cost here: a
    /// non-full node appends without reading a single vector, and a full one rejects a challenger no
    /// better than its worst edge the same way. Only a challenger that can win pays for the
    /// re-selection heuristic — which is what keeps a well-connected node from collecting a crowd of
    /// mutually similar edges and losing its long-range ones.</summary>
    void link(int neighbour, float simToNew, int level, int newOrdinal, int max, Hnsw2InsertScratch ws) {
        Span<int> upperBuffer = stackalloc int[_nodes.UpperWords];
        lock (stripeOf(neighbour)) {
            scoped ReadOnlySpan<int> ids;
            scoped ReadOnlySpan<float> sims;
            if (level == 0) {
                var record = _routing.Get(neighbour);
                ids = _routing.NeighbourIds(record);
                sims = _routing.NeighbourSims(record);
            } else {
                var count0 = _nodes.UpperEdges(neighbour, level, upperBuffer);
                ids = upperBuffer.Slice(1, count0);
                sims = MemoryMarshal.Cast<int, float>(upperBuffer.Slice(1 + _m, count0));
            }
            if (ids.Length < max) {
                ids.CopyTo(ws.LinkIds); // copied before the write below touches the same memory
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
    /// leaving a node under-connected. The pairwise comparisons run on the int8 vectors, like every
    /// other routing decision.
    /// </summary>
    int selectNeighbours(List<Candidate> candidates, int max, int self, int[] outIds, float[] outSims, Hnsw2InsertScratch ws) {
        var sorted = ws.SelectSorted;
        sorted.Clear();
        foreach (var c in candidates) {
            if (c.Ordinal != self) sorted.Add(c);
        }
        sorted.Sort(static (a, b) => b.Similarity.CompareTo(a.Similarity));
        if (ws.SelectRefs.Length < sorted.Count) {
            ws.SelectRefs = new RoutingRef[Math.Max(64, sorted.Count * 2)];
            ws.SelectRescales = new float[ws.SelectRefs.Length];
        }
        var refs = ws.SelectRefs;
        var rescales = ws.SelectRescales;
        for (var i = 0; i < sorted.Count; i++) {
            refs[i] = _routing.Get(sorted[i].Ordinal);
            rescales[i] = _routing.Rescale(refs[i]);
        }
        var selected = 0;
        var kept = ws.SelectKept; // indexes into sorted/refs, so nothing is copied
        var dropped = ws.SelectDropped;
        kept.Clear();
        dropped.Clear();
        for (var i = 0; i < sorted.Count && selected < max; i++) {
            var keep = true;
            foreach (var k in kept) {
                var pairwise = VectorMath.DotQ(_routing.Q(refs[i]), _routing.Q(refs[k])) * rescales[i] * rescales[k];
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
        Array.Clear(refs, 0, sorted.Count); // held arrays must not outlive the call, or they dodge eviction
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
            var found = beamSearch(width, s);
            rerankExact(query, found); // the walk found them; the floats score them
            var hits = new List<VectorHit>();
            foreach (var c in found) {
                if (c.Similarity >= minSim) hits.Add(new(_nodes.NodeIdOf(c.Ordinal), c.Similarity));
            }
            return hits;
        } finally {
            returnScratch(s);
        }
    }
    /// <summary>
    /// Every node id at or above a similarity, unranked — the query a semantic filter asks. A graph
    /// answers "the nearest k", so a set needs three moves on top of it, each covering a failure mode
    /// of the others:
    /// <list type="bullet">
    /// <item><b>A widening beam</b> finds the answer region and its stragglers: a beam explores
    /// best-first through below-floor territory too, which matters because the selection heuristic
    /// prunes redundant edges inside dense regions — some above-floor members hang off the region by
    /// below-floor detours no flood with a small margin can cross. The beam widens (×4) while the
    /// above-floor candidates crowd more than half of it, so the answer always ends up with
    /// headroom.</item>
    /// <item><b>The spill</b> keeps every candidate any beam round scored at or above the floor (less
    /// a margin absorbing the int8 routing error): a beam <i>drops</i> what falls below its own worst
    /// kept candidate, and a dropped node is already marked visited — without the spill it would be
    /// lost to the walk entirely, and it is exactly the boundary members that get dropped.</item>
    /// <item><b>One flood pass</b> then expands everything collected, sweeping in directly adjacent
    /// above-floor nodes the final beam never entered — and everything is re-scored against the float
    /// vectors <i>once</i>, at the end, so membership is always decided on full precision and the
    /// re-scoring reads (a disk fan-out when the vectors are not mirrored) are paid one time, not per
    /// widening round.</item>
    /// </list>
    /// A query whose answer is a large share of the index — or has no real floor at all — runs off
    /// the top of the widening ladder into <see cref="tooWideToWalk"/> and is answered by the exact
    /// parallel scan instead, which is both faster and exact there.
    /// </summary>
    public HashSet<int> SearchAbove(float[] query, float minSim, int ef) {
        var ids = new HashSet<int>();
        if (_nodes.LiveCount == 0) return ids;
        var s = rentScratch();
        try {
            s.SetQuery(query);
            var floodFloor = minSim - rangeFloodMargin;
            var collected = s.Collected; // everything scored at or above the shell floor, for one exact re-scoring
            List<Candidate> found;
            var width = Math.Max(ef, 64);
            while (true) {
                if (tooWideToWalk(width)) { // off the ladder: the scan is cheaper, and exact
                    foreach (var hit in scanAll(query, minSim)) ids.Add(hit.NodeId);
                    return ids;
                }
                collected.Clear(); // each round walks fresh; its spill covers everything narrower rounds saw
                found = beamSearch(width, s, floodFloor, collected);
                var above = 0;
                foreach (var c in found) {
                    if (c.Similarity >= minSim) above++;
                }
                // Widen until the above-floor set stops crowding the beam (or the beam no longer
                // fills, which means the index is exhausted): a beam that is mostly answer has no
                // headroom, and will have missed above-floor nodes just outside what it kept.
                if (found.Count < width || above <= width / 2) break;
                width = width > int.MaxValue / 4 ? int.MaxValue : width * 4;
            }
            // The flood: expand everything collected, collecting (and expanding) every unvisited
            // neighbour still at or above the shell floor. Re-expanding what the beam already
            // expanded is harmless — those neighbours are visited, so it costs one edge-list read.
            var work = s.FloodWork;
            work.Clear();
            foreach (var c in collected) work.Add(c.Ordinal);
            // What the flood may score before an exact scan would have been the cheaper tool; going
            // past this means the answer region rivals the index, and the scan answers that exactly.
            var overhead = _routing.Resident ? 250 : 700;
            var budget = Math.Max(4L * width, (long)_nodes.LiveCount * _dims / Math.Max(1, _threads) / (_dims + overhead));
            long scored = 0;
            var resident = _routing.Resident;
            while (work.Count > 0) {
                var current = work[^1];
                work.RemoveAt(work.Count - 1);
                var neighbours = _routing.NeighbourIds(_routing.Get(current));
                collectUnvisited(neighbours, s);
                if (!resident && s.ParallelPrefetch) _routing.Prefetch(s.ToLoad, _threads);
                scored += s.Neighbours.Count;
                foreach (var n in s.Neighbours) {
                    var sim = quantSim(s.Query, s.QueryRescale, n);
                    if (sim < floodFloor) continue; // outside the shell: neither kept nor expanded
                    collected.Add(new(n, sim));
                    work.Add(n);
                }
                if (scored > budget) {
                    foreach (var hit in scanAll(query, minSim)) ids.Add(hit.NodeId);
                    return ids;
                }
            }
            rerankExact(query, collected); // the one exact pass; everything above was routing-space
            foreach (var c in collected) {
                if (c.Similarity >= minSim) ids.Add(_nodes.NodeIdOf(c.Ordinal));
            }
            return ids;
        } finally {
            returnScratch(s);
        }
    }
    /// <summary>How far below the floor the spill and the flood reach — enough to absorb the int8
    /// routing error many times over (measured well under 0.005 on unit vectors) and to cross a thin
    /// below-floor gap between two above-floor regions. Everything collected is re-scored exactly,
    /// so the margin costs shell expansions, never wrong answers.</summary>
    const float rangeFloodMargin = 0.01f;
    /// <summary>Replaces every candidate's routing similarity with the exact float one — the numbers a
    /// caller sees are always full precision. With the vectors in memory this is a tight loop of SIMD
    /// dots; with them on disk the reads fan out over the cores, so the price of the smaller footprint
    /// is paid once per search, in parallel, rather than per hop.</summary>
    void rerankExact(float[] query, List<Candidate> found) {
        List<int>? misses = null; // indexes of candidates whose vector is not in memory
        for (var i = 0; i < found.Count; i++) {
            if (_floats.TryPeek(found[i].Ordinal, out var vector)) {
                found[i] = new(found[i].Ordinal, VectorMath.Dot(query, vector));
            } else {
                (misses ??= []).Add(i);
            }
        }
        if (misses == null) return;
        if (misses.Count >= 8 && _threads > 1) {
            Parallel.For(0, misses.Count, new ParallelOptions { MaxDegreeOfParallelism = _threads },
                () => new float[_dims],
                (k, _, buffer) => {
                    var at = misses[k];
                    var c = found[at];
                    found[at] = new(c.Ordinal, _floats.ExactSim(query, c.Ordinal, buffer)); // distinct indexes: no two workers share one
                    return buffer;
                },
                static _ => { });
        } else {
            var buffer = ArrayPool<float>.Shared.Rent(_dims);
            foreach (var at in misses) {
                var c = found[at];
                found[at] = new(c.Ordinal, _floats.ExactSim(query, c.Ordinal, buffer));
            }
            ArrayPool<float>.Shared.Return(buffer);
        }
    }
    /// <summary>
    /// True when an exact scan is the cheaper way to answer a beam this wide — and it often is, because
    /// the two are not merely different amounts of the same work. A scan does one dot product per vector
    /// and nothing else, on every core at once. A walk is a sequential chain, and around each candidate's
    /// dot product it pays its bookkeeping. So a scan costs <c>vectors × dims / cores</c> against a
    /// walk's <c>width × (dims + overhead)</c>; the overhead constant is a calibration expressed in
    /// units of one dimension's worth of dot product, and it is smaller with the graph resident (no
    /// residency checks, no hashing, prefetched memory) than with it cached. Preferring the scan where
    /// it is faster is free accuracy — it is also the exact answer. Always true below
    /// <see cref="HnswVectorIndexOptions.MinVectorsForGraphSearch"/>.
    /// </summary>
    bool tooWideToWalk(int width) {
        if (_nodes.LiveCount < _options.MinVectorsForGraphSearch) return true;
        // The overhead constant is per candidate, in units of one dimension's worth of dot product.
        // Resident graphs pay almost nothing around the dot (no residency checks, prefetched
        // memory); cached graphs pay cache probes and, cold, a disk read — and a wide cold walk
        // also re-scores its whole beam from the vector file, so past this width the sequential
        // scan is genuinely the faster answer there, exactly as it prices out here.
        var overhead = _routing.Resident ? 250 : 700;
        var walkCost = (long)width * (_dims + overhead);
        var scanCost = (long)_nodes.LiveCount * _dims / Math.Max(1, _threads);
        return scanCost <= walkCost;
    }
    /// <summary>Descends to layer 0 and explores a beam of <paramref name="width"/> candidates there,
    /// scoring against the scratch's quantized query. Returns them worst first. The optional spill
    /// (see <see cref="searchLayer"/>) receives everything the layer-0 walk scores at or above
    /// <paramref name="spillFloor"/>, kept or dropped.</summary>
    List<Candidate> beamSearch(int width, Hnsw2SearchScratch s, float spillFloor = float.PositiveInfinity, List<Candidate>? spill = null) {
        var entryPoints = descend(s);
        if (entryPoints.Count == 0) {
            s.Found.Clear();
            return s.Found;
        }
        return searchLayer(s.Query, s.QueryRescale, 0, width, entryPoints, s, spillFloor, spill);
    }
    /// <summary>Walks down from the entry point to layer 0 with a beam of one, which is where a graph
    /// search spends its logarithmic budget.</summary>
    List<int> descend(Hnsw2SearchScratch s) {
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
    (int best, float bestSim) greedyOnLayer(ReadOnlySpan<sbyte> qq, float qr, int level, int current, float currentSim,
        Hnsw2SearchScratch s) {
        Span<int> upperBuffer = stackalloc int[_nodes.UpperWords];
        var resident = _routing.Resident;
        while (true) {
            var moved = false;
            var neighbours = _nodes.UpperNeighbours(current, level, upperBuffer);
            s.Neighbours.Clear();
            s.ToLoad.Clear();
            foreach (var n in neighbours) {
                if (n < 0 || n >= _nodes.NextOrdinal || !_nodes.IsLive(n)) continue;
                s.Neighbours.Add(n);
                if (resident) _routing.PrefetchQ(n);
                else if (!_routing.IsResident(n)) s.ToLoad.Add(n);
            }
            if (!resident && s.ParallelPrefetch) _routing.Prefetch(s.ToLoad, _threads);
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
    /// <para><paramref name="spill"/>, when given, receives <i>every</i> candidate scored at or above
    /// <paramref name="spillFloor"/> — including the ones the beam drops. The range flood needs that:
    /// a dropped candidate is already marked visited, so without the spill it would be lost to both
    /// the beam and the flood, and it is exactly the boundary members of the answer that get dropped.</para>
    /// </summary>
    List<Candidate> searchLayer(ReadOnlySpan<sbyte> qq, float qr, int level, int ef, List<int> entryPoints,
        Hnsw2SearchScratch s, float spillFloor = float.PositiveInfinity, List<Candidate>? spill = null) {
        var candidates = s.Candidates; // best first: priority is the negated similarity
        var results = s.Results;       // worst first, capped at ef
        candidates.Clear();
        results.Clear();
        s.BeginWalk(_nodes.NextOrdinal, useStamps: _routing.Resident);
        Span<int> upperBuffer = stackalloc int[_nodes.UpperWords];
        foreach (var ep in entryPoints) {
            if (!_nodes.IsLive(ep) || !s.Visit(ep)) continue;
            var sim = quantSim(qq, qr, ep);
            if (sim >= spillFloor) spill!.Add(new(ep, sim));
            candidates.Push(-sim, ep);
            results.Push(sim, ep);
        }
        while (results.Count > ef) results.Pop(out _, out _);
        while (candidates.TryPeek(out var negated, out var current)) {
            if (results.Count >= ef && results.TryPeek(out var worst, out _) && -negated < worst) break;
            candidates.Pop(out _, out _);
            var neighbours = level == 0
                ? _routing.NeighbourIds(_routing.Get(current))
                : _nodes.UpperNeighbours(current, level, upperBuffer);
            collectUnvisited(neighbours, s);
            if (!_routing.Resident && s.ParallelPrefetch) _routing.Prefetch(s.ToLoad, _threads); // one read latency for the whole hop
            foreach (var n in s.Neighbours) {
                var sim = quantSim(qq, qr, n);
                if (sim >= spillFloor) spill!.Add(new(n, sim));
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
    /// <summary>The live, unvisited neighbours of a node. Neighbour ordinals are validated rather
    /// than trusted: a write torn by a crash — or overlapped by a batch build's concurrent linker —
    /// can leave a slot holding a stale id, and dropping it costs one edge. With the graph resident
    /// each accepted neighbour's vector is prefetched here, so by the time the scoring loop reaches
    /// it the memory is already on its way; with the graph cached the non-resident ones are collected
    /// for one parallel read instead.</summary>
    void collectUnvisited(ReadOnlySpan<int> neighbours, Hnsw2SearchScratch s) {
        s.Neighbours.Clear();
        s.ToLoad.Clear();
        var resident = _routing.Resident;
        foreach (var n in neighbours) {
            if (n < 0 || n >= _nodes.NextOrdinal) continue;
            if (!_nodes.IsLive(n)) continue;
            if (!s.Visit(n)) continue;
            s.Neighbours.Add(n);
            if (resident) _routing.PrefetchQ(n);
            else if (!_routing.IsResident(n)) s.ToLoad.Add(n);
        }
    }
    /// <summary>The similarity the walk routes by: the int8 dot of the query's and the node's
    /// quantized vectors, brought back to float space by their scales.</summary>
    float quantSim(ReadOnlySpan<sbyte> qq, float qr, int ordinal) {
        var r = _routing.Get(ordinal);
        return VectorMath.DotQ(qq, _routing.Q(r)) * qr * _routing.Rescale(r);
    }
    /// <summary>
    /// An exact scan of every live record, always over the float vectors: a scan's answer is exact by
    /// contract. Used below the graph-search threshold and wherever a walk would cover more of the
    /// index than a scan would (see <see cref="tooWideToWalk"/>). It runs over ordinal ranges in
    /// parallel; per range it scores straight out of memory when the vectors are there, and otherwise
    /// reads the flushed part of the range in one sequential go and takes the unflushed tail from
    /// memory, where being unflushed pins it.
    /// </summary>
    List<VectorHit> scanAll(float[] query, float minSim) {
        var count = _nodes.NextOrdinal;
        var result = new List<VectorHit>();
        if (count == 0) return result;
        var perChunk = Math.Clamp(1024 * 1024 / (_dims * 4), 16, 4096);
        var chunks = new List<int>((count + perChunk - 1) / perChunk);
        for (var start = 0; start < count; start += perChunk) chunks.Add(start);
        var locals = new ConcurrentBag<List<VectorHit>>();
        if (chunks.Count > 1 && _threads > 1) {
            // read buffers come from the pool: a scan is a hot path that would otherwise allocate a
            // buffer per worker per query, and the GC pressure of that costs more than the scan
            Parallel.ForEach(chunks, new ParallelOptions { MaxDegreeOfParallelism = _threads },
                () => new ScanBuffer(perChunk * _dims),
                (start, _, buffer) => {
                    scanChunk(query, minSim, start, Math.Min(perChunk, count - start), buffer);
                    return buffer;
                },
                buffer => {
                    locals.Add(buffer.Hits);
                    buffer.Release();
                });
        } else {
            var buffer = new ScanBuffer(perChunk * _dims);
            foreach (var start in chunks) scanChunk(query, minSim, start, Math.Min(perChunk, count - start), buffer);
            locals.Add(buffer.Hits);
            buffer.Release();
        }
        foreach (var hits in locals) result.AddRange(hits);
        return result;
    }
    sealed class ScanBuffer {
        public readonly float[] Vectors;
        public readonly List<VectorHit> Hits = [];
        public ScanBuffer(int floats) => Vectors = ArrayPool<float>.Shared.Rent(floats);
        public void Release() => ArrayPool<float>.Shared.Return(Vectors);
    }
    void scanChunk(float[] query, float minSim, int firstOrdinal, int count, ScanBuffer buffer) {
        // what this range can read from the file: everything flushed — the memory (mirror or tail)
        // covers the rest, and with the mirror on, everything
        var readCount = 0;
        if (!_floats.Mirrored) {
            var fileValid = Math.Min(_floats.RecordCapacity, _floats.FirstUnflushedOrdinal) - firstOrdinal;
            readCount = (int)Math.Clamp(fileValid, 0, count);
            if (readCount > 0) _floats.ReadRange(firstOrdinal, readCount, buffer.Vectors);
        }
        for (var i = 0; i < count; i++) {
            var ordinal = firstOrdinal + i;
            if (!_nodes.IsLive(ordinal)) continue;
            float sim;
            if (_floats.TryPeek(ordinal, out var vector)) sim = VectorMath.Dot(query, vector);
            else if (i < readCount) sim = VectorMath.Dot(query, buffer.Vectors.AsSpan(i * _dims, _dims));
            else continue; // neither in memory nor written yet: not reachable for a live node
            if (sim >= minSim) buffer.Hits.Add(new(_nodes.NodeIdOf(ordinal), sim));
        }
    }

    // ---- persistence and compaction ------------------------------------------------------------------

    /// <summary>The cheap checkpoint, for the WAL-flush path: new vectors and records to their files
    /// as sequential writes, changed edge regions appended to the edge log.</summary>
    public void Flush() {
        _floats.FlushAppended();
        _routing.FlushDirty(_edges);
        _nodes.FlushDirty();
    }
    /// <summary>The full checkpoint, for the state-save path: everything written into the files it
    /// belongs in, so the edge log holds nothing the routing file is missing and can be dropped by
    /// <see cref="DropEdgeLog"/> once a manifest no longer claims it.</summary>
    public void FlushAndConsolidate() {
        _floats.FlushAppended();
        _routing.FlushDirty(null);
        _routing.ConsolidateBehind();
        _nodes.FlushDirty();
        _edges.Disown(); // its edges are in the routing file now, so the next manifest claims none
    }
    public void Fsync() {
        _floats.Fsync();
        _routing.Fsync();
        _nodes.Fsync();
        _edges.Fsync();
    }
    /// <summary>Reclaims the edge log's space. Only safe after a manifest claiming no entries has been
    /// written, which is why the index does it as the last step of a state save.</summary>
    public void DropEdgeLog() => _edges.TruncateFile();
    public void ClearCaches() => _routing.ClearCache();
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
    public Hnsw2Graph CompactTo(long newGeneration, Hnsw2Paths paths) {
        var count = _nodes.NextOrdinal;
        var remap = new int[count];
        var next = 0;
        for (var o = 0; o < count; o++) remap[o] = _nodes.IsLive(o) ? next++ : -1;
        var target = Create(paths, newGeneration, _dims, _options);
        try {
            var idBuffer = new int[Math.Max(_m0, _m)];
            var simBuffer = new float[Math.Max(_m0, _m)];
            var upperBuffer = new int[_nodes.UpperWords];
            var vectorBuffer = new float[_dims];
            for (var o = 0; o < count; o++) {
                if (remap[o] < 0) continue;
                var level = _nodes.LevelOf(o);
                var newOrdinal = target._nodes.Allocate(_nodes.NodeIdOf(o), level);
                // the remap was built by the same ascending walk, so these must agree; if they ever
                // stopped agreeing every rewritten neighbour list would point at the wrong nodes
                if (newOrdinal != remap[o]) throw new InvalidOperationException("The compaction remap does not match the allocation order. ");
                var vector = _floats.Read(o, vectorBuffer);
                target._floats.Allocate(newOrdinal, vector);
                target._routing.Allocate(newOrdinal, vector);
                var source = _routing.Get(o);
                var n = remapped(_routing.NeighbourIds(source), _routing.NeighbourSims(source), remap, idBuffer, simBuffer);
                target._routing.SetNeighbours(newOrdinal, idBuffer.AsSpan(0, n), simBuffer.AsSpan(0, n));
                for (var l = 1; l <= level; l++) {
                    var upperCount = _nodes.UpperEdges(o, l, upperBuffer);
                    n = remapped(upperBuffer.AsSpan(1, upperCount),
                        MemoryMarshal.Cast<int, float>(upperBuffer.AsSpan(1 + _m, upperCount)),
                        remap, idBuffer, simBuffer);
                    target._nodes.SetUpperNeighbours(newOrdinal, l, idBuffer.AsSpan(0, n), simBuffer.AsSpan(0, n));
                }
                if (target.DirtyBytes >= _options.ResolvedMemTableFlushThresholdBytes) {
                    target._floats.FlushAppended();
                    target._routing.FlushDirty(null);
                    target._nodes.FlushDirty(); // keeps the pending upper lists bounded with the graph on disk
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
        _floats.Dispose();
        _routing.Dispose();
        _nodes.Dispose();
        _edges.Dispose();
    }
}
