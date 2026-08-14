namespace Relatude.DB.VectorIndex.HNSW;

/// <summary>Tuning knobs for <see cref="HnswVectorIndex"/>. All sizes are in bytes.
///
/// <para>One knob decides where the index sits between speed and footprint.
/// <see cref="MaxMemoryBytes"/> is the general budget: roughly how much memory the whole index may
/// use, everything included — the resident graph, the mirrored vectors, the caches and the
/// unflushed writes. Under it the index picks its own residency: with room for everything it keeps
/// the float vectors in memory too and a search never touches the disk; with room for only the
/// routing graph (the int8 vectors and the edges, about a quarter of the float data) it walks in
/// memory and reads floats only to re-score the final candidates; with less than that — or a budget
/// at or below <see cref="LowMemoryThresholdBytes"/>, which says footprint is the point — the graph
/// stays on disk outright and is read through a small cache.</para></summary>
public sealed class HnswVectorIndexOptions {
    /// <summary>Vector length. When null it is taken from the AI engine settings, or locked to the
    /// length of the first vector added. Every vector must have this exact length (throws if not).</summary>
    public int? Dimensions { get; set; }
    /// <summary>A <see cref="MaxMemoryBytes"/> at or below this trades speed for a minimal resident
    /// footprint: the graph stays on disk from the start — even while it would still fit — and is
    /// read through a small byte-budgeted cache, with the upper layers read from their file per hop.
    /// Searches whose working set exceeds the small cache pay a disk (or OS page cache) read per
    /// hop, and bulk loads become read-heavy, which is the price of the footprint. The files are
    /// identical on both sides of the threshold, so an index written under one budget opens under
    /// any other; residency is applied when the index opens.</summary>
    public const long LowMemoryThresholdBytes = 32L * 1024 * 1024;
    internal bool LowMemoryMode => MaxMemoryBytes <= LowMemoryThresholdBytes;
    /// <summary>Approximately how much memory the index may use, all in: the resident routing graph,
    /// the mirrored float vectors, the identity table, the caches and the unflushed writes. The index
    /// spends it in order of what memory buys most: the routing graph first (int8 vectors and edges —
    /// what every search walks), the float vectors second (what re-scoring and exact scans read).
    /// What does not fit stays on disk; at or below <see cref="LowMemoryThresholdBytes"/> the graph
    /// stays on disk outright, read through a small cache. Defaults to 100 MB. A budget is a target
    /// rather than a hard wall — dirty state that is not yet flushed cannot be evicted — and it is
    /// adjustable at runtime through <see cref="HnswVectorIndex.MaxMemoryBytes"/>.</summary>
    public long MaxMemoryBytes { get; set; } = 100L * 1024 * 1024;
    /// <summary>The most threads the index may use for its parallel work: batch builds, exact scans,
    /// mirror loading at open, re-scoring reads and prefetch fan-outs. Null uses every core. A single
    /// search walk is inherently sequential (each hop depends on the last), so this bounds the
    /// index's own fan-outs, not the number of concurrent searches callers may run.</summary>
    public int? MaxThreads { get; set; }
    internal int ResolvedMaxThreads => Math.Clamp(MaxThreads ?? Environment.ProcessorCount, 1, 512);
    /// <summary>Search accuracy in (0..1]: a multiplier on <see cref="EfSearch"/>, so 1 is the full
    /// configured effort and lower values walk a narrower beam — proportionally faster, and more
    /// likely to miss a neighbour the walk never reached. The floor is always the number of hits the
    /// query asks for, so a search never searches less than its own page. Adjustable at runtime.</summary>
    public float Accuracy { get; set; } = 1f;
    /// <summary>Verify on every add that the vector is L2-normalized and throw if it is not.
    /// Cosine similarity is computed as a plain dot product, which requires unit vectors.</summary>
    public bool ValidateNormalized { get; set; } = true;
    /// <summary>Unflushed writes are spilled to the files once they exceed this size, keeping the
    /// unevictable dirty set bounded during bulk loads. Durability still comes from the WAL; spilled
    /// records are only claimed by the manifest at the next durable checkpoint. Null resolves to a
    /// quarter of <see cref="MaxMemoryBytes"/>, capped at 64 MB.</summary>
    public long? MemTableFlushThresholdBytes { get; set; }
    internal long ResolvedMemTableFlushThresholdBytes =>
        MemTableFlushThresholdBytes ?? Math.Min(64L * 1024 * 1024, Math.Max(1024 * 1024, MaxMemoryBytes / 4));
    /// <summary>Below this many vectors every search is an exact scan of the whole index, which at
    /// these sizes is faster than walking a graph — and exact. The graph is still built and
    /// maintained below it, so crossing the threshold needs no rebuild.</summary>
    public int MinVectorsForGraphSearch { get; set; } = 1_024;
    /// <summary>Graph degree above layer 0: how many neighbours a node keeps per layer. Higher is
    /// better recall, a bigger index and slower inserts. 16 is the usual default.</summary>
    public int Connectivity { get; set; } = 16;
    /// <summary>Graph degree at layer 0, where the search does its real work. 0 means twice
    /// <see cref="Connectivity"/>, which is the standard choice.</summary>
    public int ConnectivityLevel0 { get; set; }
    /// <summary>Build effort: the beam width used while looking for a new node's neighbours. Higher
    /// builds a better graph (better recall at the same search effort) and indexes more slowly.</summary>
    public int EfConstruction { get; set; } = 128;
    /// <summary>Search effort: the beam width of a search at layer 0, before
    /// <see cref="Accuracy"/> scales it. This is the main recall dial.</summary>
    public int EfSearch { get; set; } = 64;
    /// <summary>Upper bound on the number of graph layers. Layer occupancy falls by a factor
    /// <see cref="Connectivity"/> per layer, so 8 layers cover any index that fits on a disk — and a
    /// node only pays for the layers it occupies, so a generous bound costs nothing.</summary>
    public int MaxLevels { get; set; } = 8;
    /// <summary>Deleted records are only reclaimed by a compaction, which rewrites the whole index.
    /// It runs at a state save once dead records are this share of the file (and there are at least
    /// <see cref="CompactionMinDeadRecords"/> of them), never on the WAL-flush path.</summary>
    public float CompactionDeadFraction { get; set; } = 0.25f;
    /// <summary>Floor on the number of dead records a compaction is worth rewriting the index for.</summary>
    public int CompactionMinDeadRecords { get; set; } = 4_096;
    /// <summary>Seed for the layer assignment of new nodes. Null uses a fixed seed, so a given
    /// order of sequential adds always produces the same graph. Batched adds
    /// (<see cref="HnswVectorIndex.AddRange"/>, and the WAL replay that uses it) keep the layer
    /// assignment deterministic but link in parallel, so their edge choice can differ run to run —
    /// statistically equivalent graphs, not bit-identical files.</summary>
    public int? RandomSeed { get; set; }
}
