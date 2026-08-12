namespace Relatude.DB.VectorIndexHNSW;

/// <summary>Tuning knobs for <see cref="HnswVectorIndex"/>. All sizes are in bytes. The names shared
/// with <c>NativeVectorIndexOptions</c> mean the same thing, so the two disk indexes are configured
/// the same way; the graph-specific knobs replace that one's clustering settings.</summary>
public sealed class HnswVectorIndexOptions {
    /// <summary>Vector length. When null it is taken from the AI engine settings, or locked to the
    /// length of the first vector added. Every vector must have this exact length (throws if not).</summary>
    public int? Dimensions { get; set; }
    /// <summary>Trades speed for a much smaller resident footprint: the index keeps the graph on
    /// disk and routes through memory instead of caching it wholesale. The upper-layer edges are
    /// read from their file per hop rather than mirrored in memory, the record cache tracks only
    /// what it actually holds instead of carrying a slot per vector, and the three byte budgets
    /// (<see cref="MaxCacheBytes"/>, <see cref="MaxRoutingCacheBytes"/>,
    /// <see cref="MemTableFlushThresholdBytes"/>) default much lower — an explicitly set budget
    /// still wins in either mode. Searches whose working set exceeds the small cache pay a disk (or
    /// OS page cache) read per hop, and bulk loads become read-heavy, which is the price of the
    /// footprint. The files are identical in both modes, so an index written in one opens in the
    /// other; the mode is applied when the index opens.</summary>
    public bool LowMemoryMode { get; set; }
    /// <summary>Byte budget for the in-memory cache of graph records read from disk. A record is one
    /// node's vector plus its layer-0 neighbour list, which is what a search reads per hop.
    /// Null resolves to 256 MB, or 16 MB in <see cref="LowMemoryMode"/>.
    /// Adjustable at runtime through <see cref="HnswVectorIndex.MaxCacheBytes"/>.</summary>
    public long? MaxCacheBytes { get; set; }
    internal long ResolvedMaxCacheBytes => MaxCacheBytes ?? (LowMemoryMode ? 16L * 1024 * 1024 : 256L * 1024 * 1024);
    /// <summary>Search accuracy in (0..1]: a multiplier on <see cref="EfSearch"/>, so 1 is the full
    /// configured effort and lower values walk a narrower beam — proportionally faster, and more
    /// likely to miss a neighbour the walk never reached. The floor is always the number of hits the
    /// query asks for, so a search never searches less than its own page. Adjustable at runtime.
    /// <para>Note this is not the same dial as the IVF index's accuracy, which is a fraction of the
    /// clusters in the index: recall responds to it differently, so the two are comparable at matched
    /// recall rather than at matched settings.</para></summary>
    public float Accuracy { get; set; } = 1f;
    /// <summary>Verify on every add that the vector is L2-normalized and throw if it is not.
    /// Cosine similarity is computed as a plain dot product, which requires unit vectors.</summary>
    public bool ValidateNormalized { get; set; } = true;
    /// <summary>Records changed but not yet written are spilled to the graph file once they exceed
    /// this size, keeping memory usage bounded during bulk loads. Durability still comes from the
    /// WAL; spilled records are only claimed by the manifest at the next durable checkpoint.
    /// Null resolves to 64 MB, or 8 MB in <see cref="LowMemoryMode"/>.</summary>
    public long? MemTableFlushThresholdBytes { get; set; }
    internal long ResolvedMemTableFlushThresholdBytes => MemTableFlushThresholdBytes ?? (LowMemoryMode ? 8L * 1024 * 1024 : 64L * 1024 * 1024);
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
    /// <summary>Byte budget for the vectors of nodes above layer 0, kept in memory so the descent
    /// from the entry point costs no disk reads. These are the graph's routing nodes — the analogue
    /// of the IVF index's centroids — and there are only about one in <see cref="Connectivity"/> of
    /// them per layer. When the budget runs out the index stops pinning the lowest pinned layer and
    /// reads those vectors from disk like any other.
    /// Null resolves to 64 MB, or 4 MB in <see cref="LowMemoryMode"/>.</summary>
    public long? MaxRoutingCacheBytes { get; set; }
    internal long ResolvedMaxRoutingCacheBytes => MaxRoutingCacheBytes ?? (LowMemoryMode ? 4L * 1024 * 1024 : 64L * 1024 * 1024);
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
