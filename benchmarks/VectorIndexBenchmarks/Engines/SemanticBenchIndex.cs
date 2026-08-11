using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;

namespace VectorIndexBenchmarks.Engines;

/// <summary>
/// Shared adapter for the two Relatude implementations. Both are driven only through
/// <see cref="ISemanticIndex"/> — the interface the data store uses — so what is measured is the
/// production path, including the fact that a semantic search arrives as text the index embeds
/// itself. The benchmark's AI engine answers that embedding from a warm cache (a dictionary lookup
/// on a two-character string), which is nothing against a search measured in milliseconds.
/// </summary>
public abstract class SemanticBenchIndex : IBenchVectorIndex {
    protected abstract ISemanticIndex Index { get; }
    public abstract Features Supported { get; }
    public abstract long DiskBytes { get; }

    // Add replaces on both Relatude implementations, so an update is the same call.
    public void Add(int nodeId, float[] vector) => Index.Add(nodeId, vector);
    public void Update(int nodeId, float[] vector) => Index.Add(nodeId, vector);
    // Neither implementation reads the value on remove; the id is what identifies the vector.
    public void Remove(int nodeId) => Index.Remove(nodeId, null!);

    public IReadOnlyList<int> SearchRanked(in BenchQuery query, int top, int maxHits, float minSimilarity) {
        var hits = Index.SearchForHitData(query.Text, top, maxHits, minSimilarity, out _);
        var ids = new int[hits.Count];
        for (var i = 0; i < hits.Count; i++) ids[i] = hits[i].NodeId;
        return ids;
    }

    public IBenchIdSet SearchIds(in BenchQuery query, float minSimilarity)
        => new RelatudeIdSet(Index.SearchForIdSetUnranked(query.Text, minSimilarity));

    public abstract void SaveState(long timestamp);
    public virtual void MakeDurable(long timestamp) => throw new NotSupportedException();
    public abstract void Dispose();

    sealed class RelatudeIdSet(IdSet set) : IBenchIdSet {
        public int Count => set.Count;
        public bool Has(int nodeId) => set.Has(nodeId);
    }
}
