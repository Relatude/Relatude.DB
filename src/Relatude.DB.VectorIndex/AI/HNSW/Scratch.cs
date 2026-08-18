namespace Relatude.DB.AI.HNSW;

/// <summary>A node under consideration during a walk: its ordinal and its similarity to the query.</summary>
internal readonly record struct Candidate(int Ordinal, float Similarity);

/// <summary>
/// The mutable state one graph walk needs — the visited set, the two beam heaps and a few id lists —
/// bundled so it can be pooled. A walk allocates nothing: a search rents one of these, an insert
/// (which runs one search per layer) rents one for the whole insert. Everything here exists because
/// the walk's bookkeeping competes directly with the dot products it schedules: a few thousand
/// visited-set probes and heap moves per query add up to more than the scoring itself at small
/// dimension counts, so the structures are the flattest ones that do the job.
/// </summary>
internal sealed class HnswSearchScratch {
    public readonly VisitedStamps Stamps = new();
    public readonly PairHeap Candidates = new(); // best first: priority is the negated similarity
    public readonly PairHeap Results = new();    // worst first, capped at ef
    public readonly List<int> Neighbours = [];   // one hop's live, unvisited neighbours
    public readonly List<int> EntryPoints = [];
    public readonly List<Candidate> Found = [];  // searchLayer's output, reused call to call
    public readonly List<int> FloodWork = [];    // the range flood's stack of nodes to expand
    public readonly List<Candidate> Collected = []; // what the flood keeps for the exact re-scoring
    /// <summary>The query, quantized once per search — the form the walk scores against.</summary>
    public sbyte[] Query = [];
    public float QueryRescale;

    public void SetQuery(ReadOnlySpan<float> query) {
        if (Query.Length < query.Length) Query = new sbyte[query.Length];
        VectorMath.Quantize(query, Query, out QueryRescale);
    }
    /// <summary>Prepares the visited set for one walk over an index of <paramref name="ordinals"/>.</summary>
    public void BeginWalk(int ordinals) => Stamps.Begin(ordinals);
    /// <summary>Marks an ordinal visited; false when this walk had already seen it.</summary>
    public bool Visit(int ordinal) => Stamps.Add(ordinal);

    const int keepCapacity = 1 << 16;
    /// <summary>Give back what an unusually wide walk grew, so the pool holds working-set-sized
    /// scratch rather than the high-water mark of the widest query ever run.</summary>
    public void Trim() {
        if (Found.Capacity > keepCapacity) { Found.Clear(); Found.TrimExcess(); }
        if (EntryPoints.Capacity > keepCapacity) { EntryPoints.Clear(); EntryPoints.TrimExcess(); }
        if (FloodWork.Capacity > keepCapacity) { FloodWork.Clear(); FloodWork.TrimExcess(); }
        if (Collected.Capacity > keepCapacity) { Collected.Clear(); Collected.TrimExcess(); }
    }
}

/// <summary>The write path's scratch on top of a search scratch: the selection heuristic's sort and
/// output buffers, and the link path's candidate list. One per inserting worker — a sequential
/// insert rents one, a batch build rents one per parallel worker — so nothing an insert touches is
/// shared mutable state.</summary>
internal sealed class HnswInsertScratch {
    public readonly HnswSearchScratch Search = new();
    public readonly List<Candidate> LinkCandidates = [];
    public readonly List<Candidate> SelectSorted = [];
    public readonly List<int> SelectKept = [];
    public readonly List<int> SelectDropped = [];
    /// <summary>The candidates' records, held for the duration of one selection so the pairwise dots
    /// read stable memory; cleared afterwards so cached-mode arrays do not dodge eviction.</summary>
    public RoutingRef[] SelectRefs = [];
    public float[] SelectRescales = [];
    public int[] UpsertIds = [];
    public float[] UpsertSims = [];
    public int[] LinkIds = [];
    public float[] LinkSims = [];

    /// <summary>Sizes the fixed buffers for a graph's layer-0 degree; cheap when already sized.</summary>
    public void Prepare(int m0) {
        if (UpsertIds.Length >= m0 + 1) return;
        UpsertIds = new int[m0 + 1];
        UpsertSims = new float[m0 + 1];
        LinkIds = new int[m0 + 1];
        LinkSims = new float[m0 + 1];
    }
}

/// <summary>
/// The "have I scored this node already" set of one walk, as a byte stamp per ordinal: one read and
/// one write per probe, no hashing, no probing. Starting a walk is a generation bump rather than a
/// clear, so it stays O(1) whatever the index size; the stamp is one byte, so the generation wraps
/// every 255 walks and only then is the array actually cleared — a megabyte of memset per million
/// vectors, amortised to a few kilobytes a query. Costs one byte per ordinal per pooled scratch.
/// </summary>
internal sealed class VisitedStamps {
    byte[] _stamps = [];
    byte _generation;

    /// <summary>Starts a walk over an index of <paramref name="capacity"/> ordinals. Everything
    /// stamped by an earlier walk reads as unvisited from here on.</summary>
    public void Begin(int capacity) {
        if (_stamps.Length < capacity) {
            _stamps = new byte[Math.Max(capacity + capacity / 4, 1024)]; // headroom, so growth is rare
            _generation = 0;
        }
        if (_generation == byte.MaxValue) {
            Array.Clear(_stamps);
            _generation = 0;
        }
        _generation++;
    }
    /// <summary>Marks an ordinal visited, returning false if this walk had already seen it. An
    /// ordinal outside the stamped range counts as seen, so it is never walked — <see cref="Begin"/>
    /// is always called with the index's ordinal count, so that can only be a stale neighbour id.</summary>
    public bool Add(int ordinal) {
        if ((uint)ordinal >= (uint)_stamps.Length) return false;
        if (_stamps[ordinal] == _generation) return false;
        _stamps[ordinal] = _generation;
        return true;
    }
}

/// <summary>
/// A flat binary min-heap of (priority, ordinal) pairs — what the walk's two beams are made of.
/// Two parallel arrays instead of a <see cref="PriorityQueue{TElement, TPriority}"/>: reusable
/// without reallocation, and with <see cref="ReplaceTop"/> for the beam's commonest move — a new
/// candidate displacing the worst kept one — as a single sift instead of an enqueue and a dequeue.
/// </summary>
internal sealed class PairHeap {
    float[] _prio = new float[256];
    int[] _item = new int[256];
    int _count;

    public int Count => _count;
    public void Clear() => _count = 0;
    public bool TryPeek(out float prio, out int item) {
        if (_count == 0) {
            prio = 0;
            item = 0;
            return false;
        }
        prio = _prio[0];
        item = _item[0];
        return true;
    }
    public void Push(float prio, int item) {
        if (_count == _prio.Length) {
            Array.Resize(ref _prio, _count * 2);
            Array.Resize(ref _item, _count * 2);
        }
        var i = _count++;
        while (i > 0) {
            var parent = (i - 1) >> 1;
            if (_prio[parent] <= prio) break;
            _prio[i] = _prio[parent];
            _item[i] = _item[parent];
            i = parent;
        }
        _prio[i] = prio;
        _item[i] = item;
    }
    public void Pop(out float prio, out int item) {
        prio = _prio[0];
        item = _item[0];
        var last = --_count;
        if (last > 0) siftDown(_prio[last], _item[last]);
    }
    /// <summary>Replaces the root, keeping the size: one sift-down instead of a pop and a push.</summary>
    public void ReplaceTop(float prio, int item) => siftDown(prio, item);
    void siftDown(float prio, int item) {
        var count = _count;
        var i = 0;
        while (true) {
            var child = i * 2 + 1;
            if (child >= count) break;
            var right = child + 1;
            if (right < count && _prio[right] < _prio[child]) child = right;
            if (_prio[child] >= prio) break;
            _prio[i] = _prio[child];
            _item[i] = _item[child];
            i = child;
        }
        _prio[i] = prio;
        _item[i] = item;
    }
}
