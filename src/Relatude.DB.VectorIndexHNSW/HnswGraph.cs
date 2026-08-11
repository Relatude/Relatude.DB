using Relatude.DB.DataStores.Indexes.VectorIndex;
using System.Buffers;
using System.Collections.Concurrent;

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
/// <para><b>What it costs on disk.</b> Those nodes are visited one after another and each visit needs
/// the node's vector, so a graph walk is a chain of dependent random reads — the exact access pattern
/// a disk is worst at. Two things pay for it here: the layers above 0 are in memory, so the descent
/// never touches the disk, and at every hop the whole neighbour list is loaded <i>concurrently</i>
/// (see <see cref="HnswRecordStore.Prefetch"/>), so one hop costs one read latency instead of one per
/// neighbour.</para>
///
/// <para><b>Accuracy.</b> Scored vectors are always exact full-precision floats — <c>ef</c> only
/// controls how much of the graph the walk covers, so a result is never approximate in score, only in
/// coverage. Below <see cref="HnswVectorIndexOptions.MinVectorsForGraphSearch"/> the walk is skipped
/// entirely for an exact scan, which at those sizes is both faster and exact.</para>
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
    // vectors of the routing nodes (layer >= _pinFloor), kept in memory so the descent reads nothing.
    // Concurrent because searches populate it under the index's read lock, which allows several.
    readonly ConcurrentDictionary<int, float[]> _pinned = new();
    readonly object _pinLock = new();
    // the per-walk scratch, pooled: concurrent searches each need their own, and none of it is worth
    // reallocating per query
    readonly ConcurrentBag<Walk> _walks = [];
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
    }

    /// <summary>Everything one walk needs beyond the graph itself: the visited stamps, the two heaps of
    /// SEARCH-LAYER and the neighbour scratch. Pooled and reused, so a query allocates none of it.</summary>
    sealed class Walk {
        public readonly VisitedStamps Visited = new();
        public readonly PriorityQueue<int, float> Candidates = new(); // best first: priority is the negated similarity
        public readonly PriorityQueue<int, float> Results = new();    // worst first, capped at the beam width
        public readonly List<int> Neighbours = [];  // the live, unvisited neighbours of the node being expanded
        public readonly List<int> ToLoad = [];      // the subset of those whose vector is not already in memory
        public readonly List<int> EntryPoints = [];
        public readonly List<Candidate> Output = [];
    }
    // A walk's visited stamps are one byte per vector, so the pool has to be bounded: a burst of
    // concurrent searches would otherwise leave the index holding a stamp array per search in it
    // forever. Past the bound a walk is simply dropped and the next one allocates.
    int _pooledWalks;
    Walk rentWalk() {
        if (!_walks.TryTake(out var walk)) return new();
        Interlocked.Decrement(ref _pooledWalks);
        return walk;
    }
    void returnWalk(Walk walk) {
        if (Interlocked.Increment(ref _pooledWalks) > Math.Max(4, Environment.ProcessorCount)) {
            Interlocked.Decrement(ref _pooledWalks);
            return;
        }
        _walks.Add(walk);
    }

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
        var store = HnswRecordStore.Create(paths.Graph(generation), generation, dims, m0, options.MaxCacheBytes);
        HnswNodeTable? nodes = null;
        try {
            nodes = HnswNodeTable.Create(paths.Nodes(generation), paths.Upper(generation), generation, m, maxLevels);
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
        var store = HnswRecordStore.Open(paths.Graph(m.Generation), m.Generation, m.Dimensions, m0, options.MaxCacheBytes, m.NextOrdinal);
        HnswNodeTable? nodes = null;
        try {
            nodes = HnswNodeTable.Open(paths.Nodes(m.Generation), paths.Upper(m.Generation), m.Generation,
                m.Connectivity, m.MaxLevels, m.NextOrdinal, m.NextUpperSlot, m.EntryOrdinal, m.MaxLevel);
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
        var record = _store.Allocate(ordinal);
        vector.CopyTo(record.AsSpan(0, _dims));
        if (entry < 0 || !_nodes.IsLive(entry)) return; // the first live node has nothing to link to
        var query = record.AsSpan(0, _dims); // stable: a dirty record is never moved or reloaded
        var walk = rentWalk();
        try {
            var current = entry;
            for (var l = topLevel; l > level; l--) current = greedyDescend(query, current, l, walk);
            walk.EntryPoints.Clear();
            walk.EntryPoints.Add(current);
            for (var l = Math.Min(topLevel, level); l >= 0; l--) {
                var found = searchLayer(query, l, Math.Max(_options.EfConstruction, 1), walk.EntryPoints, walk);
                var max = l == 0 ? _m0 : _m;
                var selected = selectNeighbours(found, max, ordinal);
                setNeighbours(ordinal, l, selected);
                walk.EntryPoints.Clear();
                foreach (var c in found) walk.EntryPoints.Add(c.Ordinal); // the whole beam seeds the next layer
                if (walk.EntryPoints.Count == 0) walk.EntryPoints.Add(entry);
                linkBack(selected, l, ordinal, record, max, walk); // after the beam is copied out of Output
            }
        } finally {
            returnWalk(walk);
        }
        _nodes.PromoteEntry(ordinal); // only now that it is linked can a search enter through it
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
    void setNeighbours(int ordinal, int level, ReadOnlySpan<int> neighbours) {
        if (level == 0) _store.SetNeighbours(ordinal, neighbours);
        else _nodes.SetUpperNeighbours(ordinal, level, neighbours);
    }
    /// <summary>
    /// Adds the reverse edge from every chosen neighbour back to the new node.
    ///
    /// <para>At layer 0 this is where most of an insert's time goes: there are twice as many neighbours
    /// as on the layers above and each full one re-runs the O(max²) selection heuristic. It is also
    /// perfectly parallel — the chosen neighbours are distinct, each rewrites only its own list, and
    /// everything any of them reads (its own current list, its neighbours' vectors, the new node's
    /// vector) is untouched by the others. So the layer-0 pass runs concurrently and produces exactly
    /// the graph the sequential pass would: the outcome of one link does not depend on the order the
    /// others ran in.</para>
    /// </summary>
    void linkBack(int[] selected, int level, int newOrdinal, float[] newVector, int max, Walk walk) {
        if (level != 0 || selected.Length < 4 || Environment.ProcessorCount <= 2) {
            foreach (var n in selected) link(n, level, newOrdinal, newVector, max);
            return;
        }
        walk.ToLoad.Clear();
        var missing = 0;
        foreach (var n in selected) {
            walk.ToLoad.Add(n);
            if (!_store.IsResident(n)) missing++;
        }
        _store.Prefetch(walk.ToLoad, missing); // their records are the first thing every worker wants
        Parallel.ForEach(selected, n => link(n, 0, newOrdinal, newVector, max));
    }
    /// <summary>One reverse edge. When the neighbour's list is already full its edges are re-selected
    /// with the same heuristic, scored against the neighbour itself — which is what keeps a
    /// well-connected node from collecting a crowd of mutually similar edges and losing its long-range
    /// ones. Called concurrently for layer 0, so it touches nothing shared and unsynchronized.</summary>
    void link(int neighbour, int level, int newOrdinal, float[] newVector, int max) {
        var existing = level == 0 ? _store.Neighbours(_store.Get(neighbour)) : _nodes.UpperNeighbours(neighbour, level);
        if (existing.Length < max) {
            var appended = new int[existing.Length + 1]; // the cheap branch, and rare once the graph is warm
            existing.CopyTo(appended);
            appended[existing.Length] = newOrdinal;
            setNeighbours(neighbour, level, appended);
            return;
        }
        var self = vectorRef(neighbour);
        var candidates = new List<Candidate>(max + 1);
        foreach (var e in existing) {
            if (!_nodes.IsLive(e)) continue;
            candidates.Add(new(e, VectorMath.Dot(self.AsSpan(0, _dims), vectorOf(e))));
        }
        candidates.Add(new(newOrdinal, VectorMath.Dot(self.AsSpan(0, _dims), newVector.AsSpan(0, _dims))));
        setNeighbours(neighbour, level, selectNeighbours(candidates, max, neighbour));
    }
    /// <summary>
    /// HNSW's neighbour selection heuristic: take candidates best first, but keep one only when it is
    /// closer to the query than to anything already kept. That drops a candidate whose region of the
    /// space is already covered and keeps the diverse, longer-range edges the layers need to stay
    /// navigable — picking the nearest ones instead makes a graph that is locally dense and globally
    /// disconnected. Slots left over are filled with the best of the dropped candidates rather than
    /// leaving a node under-connected.
    ///
    /// <para>The candidates' vectors are resolved once up front. This runs O(max²) dot products and is
    /// the most expensive part of an insert, so looking each vector up again per comparison — a cache
    /// lookup under a lock every time — would cost more than the arithmetic.</para>
    /// </summary>
    int[] selectNeighbours(List<Candidate> candidates, int max, int self) {
        var sorted = new List<Candidate>(candidates.Count);
        foreach (var c in candidates) {
            if (c.Ordinal != self) sorted.Add(c);
        }
        // With no more candidates than slots the heuristic cannot drop anything: what it declines to
        // select it puts straight back to fill the free slots, so the result is every candidate either
        // way and the O(max²) dot products would only be deciding an order the graph never reads.
        if (sorted.Count <= max) {
            var all = new int[sorted.Count];
            for (var i = 0; i < all.Length; i++) all[i] = sorted[i].Ordinal;
            return all;
        }
        sorted.Sort(static (a, b) => b.Similarity.CompareTo(a.Similarity));
        var arrays = new float[sorted.Count][];
        for (var i = 0; i < sorted.Count; i++) arrays[i] = vectorRef(sorted[i].Ordinal);
        var selected = new List<int>(max);
        var kept = new List<int>(max); // indexes into sorted/arrays, so no vector is copied
        var dropped = new List<int>();
        for (var i = 0; i < sorted.Count && selected.Count < max; i++) {
            var keep = true;
            var candidate = arrays[i].AsSpan(0, _dims);
            foreach (var k in kept) {
                if (VectorMath.Dot(candidate, arrays[k].AsSpan(0, _dims)) >= sorted[i].Similarity) {
                    keep = false;
                    break;
                }
            }
            if (keep) {
                selected.Add(sorted[i].Ordinal);
                kept.Add(i);
            } else if (dropped.Count < max) {
                dropped.Add(sorted[i].Ordinal);
            }
        }
        for (var i = 0; i < dropped.Count && selected.Count < max; i++) selected.Add(dropped[i]);
        return [.. selected];
    }

    // ---- searches ----------------------------------------------------------------------------------

    readonly record struct Candidate(int Ordinal, float Similarity);

    /// <summary>The best candidates for a query, unordered and not yet paged — at least
    /// <paramref name="wanted"/> of them where the index has that many above
    /// <paramref name="minSim"/>. Exact below
    /// <see cref="HnswVectorIndexOptions.MinVectorsForGraphSearch"/>, a graph walk above it.</summary>
    public List<VectorHit> SearchRanked(float[] query, int wanted, float minSim, int ef) {
        if (_nodes.LiveCount == 0 || wanted <= 0) return [];
        var width = Math.Max(ef, wanted);
        if (tooWideToWalk(width)) return scanAll(query, minSim);
        var hits = new List<VectorHit>();
        var walk = rentWalk();
        try {
            foreach (var c in beamSearch(query, width, walk)) {
                if (c.Similarity >= minSim) hits.Add(new(_nodes.NodeIdOf(c.Ordinal), c.Similarity));
            }
        } finally {
            returnWalk(walk);
        }
        return hits;
    }
    /// <summary>
    /// Every node id at or above a similarity, unranked — the query a semantic filter asks. A graph
    /// answers "the nearest k", so this asks for a k wide enough to contain the answer: run the beam,
    /// and widen it while the answer looks like it is pressing against the edge of it.
    ///
    /// <para>"Pressing against the edge" is the part that matters for recall. Stopping as soon as the
    /// beam's worst candidate falls below the threshold would leave a beam barely wider than the answer
    /// it has to contain, and a graph walk at <c>ef</c> only just larger than <c>k</c> is precisely where
    /// HNSW's recall is weakest — measurably so: it cost two points of filter recall. So the beam has to
    /// come back at least a quarter empty before it is believed.</para>
    /// </summary>
    public HashSet<int> SearchAbove(float[] query, float minSim, int ef) {
        var ids = new HashSet<int>();
        if (_nodes.LiveCount == 0) return ids;
        var walk = rentWalk();
        try {
            for (var width = Math.Max(ef, 64); ; width = width > int.MaxValue / 4 ? int.MaxValue : width * 4) {
                if (tooWideToWalk(width)) { // the walk would score more vectors than the index holds
                    foreach (var hit in scanAll(query, minSim)) ids.Add(hit.NodeId);
                    return ids;
                }
                var found = beamSearch(query, width, walk); // worst first
                var above = 0;
                foreach (var c in found) {
                    if (c.Similarity >= minSim) above++;
                }
                if (found.Count >= width && above * 4 > width * 3) continue; // too full to trust
                foreach (var c in found) {
                    if (c.Similarity >= minSim) ids.Add(_nodes.NodeIdOf(c.Ordinal));
                }
                return ids;
            }
        } finally {
            returnWalk(walk);
        }
    }
    /// <summary>
    /// True when an exact scan is the cheaper way to answer a beam this wide — and it often is, because
    /// the two are not merely different amounts of the same work. A scan does one dot product per vector
    /// and nothing else, on every core at once. A walk is a sequential chain, and around each candidate's
    /// dot product it pays a visited-set probe, two heap operations and a residency lookup. So a scan
    /// costs <c>vectors × dims / cores</c> against a walk's <c>width × (dims + bookkeeping)</c>, and
    /// which wins turns on the dimension count: at 128 dimensions the per-candidate costs rival the
    /// arithmetic and the scan stays ahead into the hundreds of thousands of vectors, while at 1536 the
    /// walk takes over in the tens of thousands. Preferring the scan where it is faster is free accuracy —
    /// it is also the exact answer. Always true below
    /// <see cref="HnswVectorIndexOptions.MinVectorsForGraphSearch"/>, where the index is small enough
    /// that the scan is the better answer whatever the query asks for.
    /// <para>The constant is a calibration rather than a law: the per-candidate bookkeeping, expressed
    /// in units of one dimension's worth of dot product. It is deliberately on the generous side, so a
    /// close call goes to the scan — which is the exact answer, and so the cheaper mistake to make.</para>
    /// </summary>
    const int walkOverheadInDims = 700;
    bool tooWideToWalk(int width) {
        if (_nodes.LiveCount < _options.MinVectorsForGraphSearch) return true;
        var walkCost = (long)width * (_dims + walkOverheadInDims);
        var scanCost = (long)_nodes.LiveCount * _dims / Math.Max(1, Environment.ProcessorCount);
        return scanCost <= walkCost;
    }
    /// <summary>Descends to layer 0 and explores a beam of <paramref name="width"/> candidates there.
    /// Returns them worst first, so the last entry is the best — in the walk's own buffer, so the caller
    /// must be done with them before it releases the walk.</summary>
    List<Candidate> beamSearch(ReadOnlySpan<float> query, int width, Walk walk) {
        var entry = _nodes.EntryOrdinal;
        if (entry < 0 || !_nodes.IsLive(entry)) {
            walk.Output.Clear();
            return walk.Output;
        }
        for (var l = _nodes.MaxLevel; l >= 1; l--) entry = greedyDescend(query, entry, l, walk);
        walk.EntryPoints.Clear();
        walk.EntryPoints.Add(entry);
        return searchLayer(query, 0, width, walk.EntryPoints, walk);
    }
    /// <summary>
    /// One layer of the descent: step to the best neighbour for as long as any of them is closer to the
    /// query than where we stand. This is SEARCH-LAYER with a beam of one written out — a beam of one
    /// needs no heaps, and because every step strictly improves on the last it needs no visited set
    /// either. Which matters because the descent runs once per layer per query and its work is otherwise
    /// trivial: the layers above 0 keep their edges in memory and their vectors pinned, so all that was
    /// left to pay was the bookkeeping.
    /// </summary>
    int greedyDescend(ReadOnlySpan<float> query, int entry, int level, Walk walk) {
        var current = entry;
        var best = VectorMath.Dot(query, vectorOf(current));
        while (true) {
            walk.Neighbours.Clear();
            walk.ToLoad.Clear();
            var missing = 0;
            foreach (var n in _nodes.UpperNeighbours(current, level)) {
                if (n < 0 || n >= _nodes.NextOrdinal || !_nodes.IsLive(n)) continue;
                walk.Neighbours.Add(n);
                if (_nodes.LevelOf(n) == 0 || !_pinned.ContainsKey(n)) {
                    walk.ToLoad.Add(n);
                    if (!_store.IsResident(n)) missing++;
                }
            }
            _store.Prefetch(walk.ToLoad, missing);
            var next = -1;
            foreach (var n in walk.Neighbours) {
                var sim = VectorMath.Dot(query, vectorOf(n));
                if (sim <= best) continue;
                best = sim;
                next = n;
            }
            if (next < 0) return current;
            current = next;
        }
    }
    /// <summary>
    /// HNSW's SEARCH-LAYER: expand the most promising unexpanded candidate until the best one left is
    /// worse than the worst result kept, holding at most <paramref name="ef"/> results. Returns them
    /// worst first, so the last entry is the best.
    /// </summary>
    List<Candidate> searchLayer(ReadOnlySpan<float> query, int level, int ef, List<int> entryPoints, Walk walk) {
        walk.Visited.Begin(_nodes.NextOrdinal);
        var candidates = walk.Candidates;
        var results = walk.Results;
        candidates.Clear();
        results.Clear();
        // grown once per pooled walk rather than once per query; a beam of ef keeps ef results and tends
        // to hold a similar number of unexpanded candidates
        results.EnsureCapacity(Math.Min(ef, 4096) + 1);
        candidates.EnsureCapacity(Math.Min(ef, 4096) + 1);
        foreach (var ep in entryPoints) {
            if (!_nodes.IsLive(ep) || !walk.Visited.Add(ep)) continue;
            var sim = VectorMath.Dot(query, vectorOf(ep));
            candidates.Enqueue(ep, -sim);
            results.Enqueue(ep, sim);
        }
        while (results.Count > ef) results.Dequeue();
        var worst = results.Count >= ef && results.TryPeek(out _, out var initial) ? initial : float.NegativeInfinity;
        while (candidates.TryPeek(out var current, out var negated)) {
            if (results.Count >= ef && -negated < worst) break;
            candidates.Dequeue();
            var neighbours = level == 0
                ? _store.Neighbours(_store.Get(current))
                : _nodes.UpperNeighbours(current, level);
            var missing = collectUnvisited(neighbours, walk);
            _store.Prefetch(walk.ToLoad, missing); // one read latency for the whole neighbour list, not one each
            foreach (var n in walk.Neighbours) {
                var sim = VectorMath.Dot(query, vectorOf(n));
                if (results.Count < ef) {
                    candidates.Enqueue(n, -sim);
                    results.Enqueue(n, sim);
                    if (results.Count >= ef && results.TryPeek(out _, out var filled)) worst = filled;
                } else if (sim > worst) {
                    candidates.Enqueue(n, -sim);
                    results.Enqueue(n, sim);
                    results.Dequeue();
                    if (results.TryPeek(out _, out var raised)) worst = raised;
                }
            }
        }
        var output = walk.Output;
        output.Clear();
        while (results.TryDequeue(out var ordinal, out var sim)) output.Add(new(ordinal, sim));
        return output;
    }
    /// <summary>The live, unvisited neighbours of a node into the walk's scratch, returning how many of
    /// them would have to come off the disk. Neighbour ordinals are validated rather than trusted: a
    /// write torn by a crash can leave a slot holding a stale id, and dropping it costs one edge.</summary>
    int collectUnvisited(ReadOnlySpan<int> neighbours, Walk walk) {
        walk.Neighbours.Clear();
        walk.ToLoad.Clear();
        var missing = 0;
        foreach (var n in neighbours) {
            if (n < 0 || n >= _nodes.NextOrdinal) continue;
            if (!_nodes.IsLive(n)) continue;
            if (!walk.Visited.Add(n)) continue;
            walk.Neighbours.Add(n);
            if (_nodes.LevelOf(n) == 0 || !_pinned.ContainsKey(n)) {
                walk.ToLoad.Add(n);
                if (!_store.IsResident(n)) missing++;
            }
        }
        return missing;
    }
    /// <summary>
    /// An exact scan of every live record. Used below the graph-search threshold and wherever a walk
    /// would cover more of the index than a scan would (see <see cref="tooWideToWalk"/>). It runs over
    /// ordinal ranges in parallel, and per range it either scores straight out of the records already
    /// in memory or — when some of them are not — reads the whole range in one sequential go and scores
    /// out of that buffer. Which is to say: a small index scans at memory speed, and a large one scans
    /// at the disk's sequential speed rather than pulling itself through the residency table.
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

    /// <summary>A node's vector, from the pinned routing set when it is one of them, else from the
    /// record store — which also pins it when it belongs in the routing set.</summary>
    ReadOnlySpan<float> vectorOf(int ordinal) => vectorRef(ordinal).AsSpan(0, _dims);
    /// <summary>The array a node's vector is at the head of — a pinned copy or the whole record, which
    /// both start with it. The neighbour selection heuristic holds a hundred of these at a time and
    /// copying each one out would allocate more per insert than the rest of the insert put together,
    /// while the arrays themselves are stable: a record is only mutated in its neighbour slots, and a
    /// reference to one keeps it alive even if the residency table drops it.</summary>
    float[] vectorRef(int ordinal) {
        var level = _nodes.LevelOf(ordinal);
        if (level > 0 && _pinned.TryGetValue(ordinal, out var routing)) return routing;
        var record = _store.Get(ordinal);
        var floor = _pinFloor;
        if (level >= floor && floor < _maxLevels) pin(ordinal, level, _store.Vector(record));
        return record;
    }
    void pin(int ordinal, int level, ReadOnlySpan<float> vector) {
        var bytes = (long)_dims * 4 + 64;
        if (Interlocked.Read(ref _pinnedBytes) + bytes > _options.MaxRoutingCacheBytes) {
            raisePinFloor();
            if (level < _pinFloor) return; // this node is no longer worth pinning
        }
        if (_pinned.TryAdd(ordinal, vector.ToArray())) Interlocked.Add(ref _pinnedBytes, bytes);
    }
    /// <summary>Out of budget: stop pinning the lowest pinned layer and release what it held. The
    /// floor only ever rises, so this settles instead of thrashing, and the layers that matter most
    /// for routing — the ones nearest the entry point — are the ones that stay.</summary>
    void raisePinFloor() {
        lock (_pinLock) {
            if (Interlocked.Read(ref _pinnedBytes) + (long)_dims * 4 + 64 <= _options.MaxRoutingCacheBytes) return;
            if (_pinFloor >= _maxLevels) return;
            _pinFloor++;
            long freed = 0;
            foreach (var ordinal in _pinned.Keys) {
                if (_nodes.LevelOf(ordinal) >= _pinFloor) continue;
                if (_pinned.TryRemove(ordinal, out var vector)) freed += (long)vector.Length * 4 + 64;
            }
            Interlocked.Add(ref _pinnedBytes, -freed);
        }
    }

    // ---- persistence and compaction ------------------------------------------------------------------

    /// <summary>The cheap checkpoint, for the WAL-flush path: new records to the graph file, changed
    /// neighbour lists appended to the edge log.</summary>
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
        while (_walks.TryTake(out _)) Interlocked.Decrement(ref _pooledWalks); // their stamp arrays too
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
            var buffer = new int[Math.Max(_m0, _m)];
            for (var o = 0; o < count; o++) {
                if (remap[o] < 0) continue;
                var level = _nodes.LevelOf(o);
                var newOrdinal = target._nodes.Allocate(_nodes.NodeIdOf(o), level);
                // the remap was built by the same ascending walk, so these must agree; if they ever
                // stopped agreeing every rewritten neighbour list would point at the wrong nodes
                if (newOrdinal != remap[o]) throw new InvalidOperationException("The compaction remap does not match the allocation order. ");
                var source = _store.Get(o);
                _store.Vector(source).CopyTo(target._store.Allocate(newOrdinal).AsSpan(0, _dims));
                target._store.SetNeighbours(newOrdinal, remapped(_store.Neighbours(source), remap, buffer));
                for (var l = 1; l <= level; l++) {
                    target._nodes.SetUpperNeighbours(newOrdinal, l, remapped(_nodes.UpperNeighbours(o, l), remap, buffer));
                }
                if (target._store.DirtyBytes >= _options.MemTableFlushThresholdBytes) target._store.FlushDirty(null);
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
    static ReadOnlySpan<int> remapped(ReadOnlySpan<int> neighbours, int[] remap, int[] buffer) {
        var n = 0;
        foreach (var neighbour in neighbours) {
            if (n == buffer.Length) break;
            if (neighbour < 0 || neighbour >= remap.Length) continue;
            var mapped = remap[neighbour];
            if (mapped >= 0) buffer[n++] = mapped;
        }
        return buffer.AsSpan(0, n);
    }
    public void Dispose() {
        _pinned.Clear();
        _store.Dispose();
        _nodes.Dispose();
        _edges.Dispose();
    }
}
