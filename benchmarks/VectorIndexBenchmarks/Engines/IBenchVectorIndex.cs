namespace VectorIndexBenchmarks.Engines;

/// <summary>Capabilities that are not part of every implementation. A phase measuring one of these
/// is skipped (and printed as "n/a") for implementations that do not have it, rather than timing a
/// call that throws or quietly answers a different question.</summary>
[Flags]
public enum Features {
    None = 0,
    /// <summary>An unranked "every vector at or above this similarity" query, which is what a
    /// semantic WhereSearch filter needs. A pure top-k library has no equivalent.</summary>
    UnrankedFilter = 1,
    /// <summary>A durability hook the store can call after every WAL flush, cheaper than a full
    /// state save because it writes only the delta.</summary>
    IncrementalDurability = 2,
}

/// <summary>One query, in both the forms an implementation might want it in: the text a semantic
/// index embeds itself, and the vector a library takes directly. They are the same query — the
/// benchmark's AI engine maps the text back to exactly this vector.</summary>
public readonly record struct BenchQuery(string Text, float[] Vector);

/// <summary>An unranked result set: all this benchmark asks of one is how big it is and whether a
/// given id is in it, which every implementation can answer without materializing a list.</summary>
public interface IBenchIdSet {
    int Count { get; }
    bool Has(int nodeId);
}

/// <summary>
/// One benchmarked vector index. Deliberately below <c>ISemanticIndex</c>: a third-party library
/// cannot implement Relatude's interface, so the comparison is defined in terms of what every
/// vector index actually does. The two Relatude implementations are still driven through
/// <c>ISemanticIndex</c> underneath, so what is measured for them remains the production path.
///
/// <para><see cref="Add"/> and <see cref="Update"/> are separate because the implementations
/// differ exactly there: Relatude's indexes replace on add, USearch refuses a duplicate key, and
/// a vec0 table rejects INSERT OR REPLACE but accepts an UPDATE. A data store always knows which
/// of the two it is doing, so letting each implementation use its own best statement measures the
/// library rather than the adapter.</para>
/// </summary>
public interface IBenchVectorIndex : IDisposable {
    Features Supported { get; }
    /// <summary>Index a vector under an id that is not in the index yet.</summary>
    void Add(int nodeId, float[] vector);
    /// <summary>Index a batch of new ids. Implementations with a bulk or parallel build path
    /// override this; the default adds one at a time, which is what a per-op engine does anyway.</summary>
    void AddBatch(IReadOnlyList<(int nodeId, float[] vector)> items) {
        foreach (var (nodeId, vector) in items) Add(nodeId, vector);
    }
    /// <summary>Replace the vector of an id that is already in the index.</summary>
    void Update(int nodeId, float[] vector);
    void Remove(int nodeId);
    /// <summary>The best <paramref name="top"/> ids, best first, having evaluated at most
    /// <paramref name="maxHits"/> and discarding anything below <paramref name="minSimilarity"/>.</summary>
    IReadOnlyList<int> SearchRanked(in BenchQuery query, int top, int maxHits, float minSimilarity);
    /// <summary>Every id at or above <paramref name="minSimilarity"/>, unranked. Only called when
    /// <see cref="Features.UnrankedFilter"/> is supported.</summary>
    IBenchIdSet SearchIds(in BenchQuery query, float minSimilarity);
    /// <summary>Write the durable state at a state save — for most of these implementations the
    /// whole index, for the Relatude disk index a segment flush and a manifest swap.</summary>
    void SaveState(long timestamp);
    /// <summary>The post-WAL-flush hook, when the implementation has one. Only called when
    /// <see cref="Features.IncrementalDurability"/> is supported.</summary>
    void MakeDurable(long timestamp);
    long DiskBytes { get; }
}

/// <summary>An id set backed by a plain hash set, for the implementations that produce one.</summary>
public sealed class HashBenchIdSet(HashSet<int> ids) : IBenchIdSet {
    public int Count => ids.Count;
    public bool Has(int nodeId) => ids.Contains(nodeId);
}
