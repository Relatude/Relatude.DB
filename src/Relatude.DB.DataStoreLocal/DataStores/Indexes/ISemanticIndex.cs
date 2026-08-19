using Relatude.DB.DataStores.Sets;

namespace Relatude.DB.DataStores.Indexes;

/// <summary>
/// A semantic (vector similarity) index over one float-array property. Extends <see cref="IIndex"/>:
/// implementations are driven exactly like the other property indexes — adds/removes per
/// transaction, the memory-index state protocol at startup and state saves, and WAL replay gated by
/// <see cref="IIndex.PersistedTimestamp"/> — whether the vectors live in memory
/// (<see cref="MemorySemanticIndex"/>) or on disk (the native vector index engine).
/// </summary>
public interface ISemanticIndex : IIndex {
    int MaxCount(string value);
    /// <summary>All node ids whose vector is at least this similar to the embedded search text.</summary>
    IdSet SearchForIdSetUnranked(string value, float minimumVectorSimilarity);
    /// <summary>The top ranked hits for the embedded search text; <paramref name="totalHits"/> is
    /// the number of hits evaluated (bounded by <paramref name="maxHits"/>).</summary>
    List<RawSearchHit> SearchForHitData(string value, int top, int maxHits, float minimumCosineSimilarity, out int totalHits);
    /// <summary>A display sample of the source text for a search; currently the full text.</summary>
    string GetSample(string search, string sourceText);
    /// <summary>The context to hand an LLM for a question over the source text; currently the full text.</summary>
    string GetContextText(string search, string sourceText);
    void Add(int nodeId, float[] vector);
    void Remove(int nodeId, float[] vector);
    /// <summary>
    ///  returns the number of dimensions of the vectors in this index, or false if the index is empty and has no dimensions yet.
    /// </summary>
    /// <param name="dimensions"></param>
    /// <returns></returns>
    bool TryGetNoDimensions(out int dimensions);
    void LogWarning(string message);
}
public static class SemanticIndexExtensions {
    static HashSet<string> _hasWarned = new();
    public static float[] EnsureCorrectDimensions(this ISemanticIndex index, float[] vector) {
        if (!index.TryGetNoDimensions(out var dimensions)) return vector; // no dimensions yet, so any vector is valid
        if (vector.Length != dimensions) {
            var resized = new float[dimensions];
            for ( int i = 0; i < dimensions; i++) {
                resized[i] = 1;
            }
            if (_hasWarned.Add(index.FriendlyName)) {
                index.LogWarning($"WARNING: Vector of length {vector.Length} was resized to {dimensions} dimensions for index {index.FriendlyName}. Please reindex all vectors for proper search results.");
            }
            return resized;
        }
        return vector;
    }
}
