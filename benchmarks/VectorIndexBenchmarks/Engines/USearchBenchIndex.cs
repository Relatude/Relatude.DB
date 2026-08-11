using Cloud.Unum.USearch;

namespace VectorIndexBenchmarks.Engines;

/// <summary>
/// USearch (unum-cloud), through its official <c>Cloud.Unum.USearch</c> binding: an HNSW graph
/// index in native memory. The graph is the other main answer to approximate nearest-neighbour
/// search — where the Relatude disk index clusters vectors and probes the nearest clusters (IVF),
/// this one walks a navigable small-world graph — so it is the most direct algorithmic comparison
/// available in process.
///
/// <para><b>What it does not do.</b> HNSW answers "the best k", not "everything above a
/// similarity" — there is no unranked path, so the filter phase is skipped rather than emulated
/// with a k of the whole index, which would measure a query no one would write. It also has no
/// incremental durability: <see cref="USearchIndex.Save"/> writes the entire index, like the
/// in-memory Relatude index's state file.</para>
///
/// <para><b>Accuracy.</b> There is no single accuracy dial; recall is bought with
/// <c>connectivity</c> (graph degree), <c>expansionAdd</c> (build effort) and
/// <c>expansionSearch</c> (search effort). The last is the closest analogue to the disk index's
/// accuracy fraction, so it is the one exposed as an option.</para>
///
/// <para><b>Memory.</b> The graph and the vectors live in native memory, so USearch's footprint
/// shows up in the working set and never in the managed-heap columns. Its vectors are stored at
/// the configured quantization (float32 here, matching everyone else). USearch can also memory-map
/// an index from disk rather than load it — the mode closest to what the Relatude disk index does —
/// but this harness measures its footprint before the reopen, so that mode is not what is timed
/// here.</para>
/// </summary>
public sealed class USearchBenchIndex : IBenchVectorIndex {
    readonly USearchIndex _index;
    readonly string _path;

    public USearchBenchIndex(string dir, int dimensions, ulong connectivity, ulong expansionAdd, ulong expansionSearch) {
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "usearch.bin");
        // Cosine over the same unit vectors everyone else gets. Reopening is the file constructor;
        // a fresh run starts from the configured parameters.
        _index = File.Exists(_path)
            ? new USearchIndex(_path)
            : new USearchIndex(MetricKind.Cos, ScalarKind.Float32, (ulong)dimensions, connectivity, expansionAdd, expansionSearch);
    }

    public Features Supported => Features.None;

    public void Add(int nodeId, float[] vector) => _index.Add((ulong)nodeId, vector);
    /// <summary>USearch refuses a duplicate key outright (the binding throws), so replacing a
    /// vector means removing the key first. That is what the store's node update does anyway.</summary>
    public void Update(int nodeId, float[] vector) {
        _index.Remove((ulong)nodeId);
        _index.Add((ulong)nodeId, vector);
    }
    public void Remove(int nodeId) => _index.Remove((ulong)nodeId);

    public IReadOnlyList<int> SearchRanked(in BenchQuery query, int top, int maxHits, float minSimilarity) {
        // Evaluate maxHits and keep the best top, the same instruction every other implementation
        // gets. USearch returns cosine distance, which for unit vectors is 1 - similarity.
        var found = _index.Search(query.Vector, maxHits, out var keys, out var distances);
        var ids = new List<int>(Math.Min(top, found));
        for (var i = 0; i < found && ids.Count < top; i++) {
            if (1f - distances[i] < minSimilarity) break; // results come back best first
            ids.Add((int)keys[i]);
        }
        return ids;
    }

    public IBenchIdSet SearchIds(in BenchQuery query, float minSimilarity)
        => throw new NotSupportedException("USearch has no unranked threshold query. ");

    /// <summary>Writes the whole index file — USearch has no delta.</summary>
    public void SaveState(long timestamp) => _index.Save(_path);
    public void MakeDurable(long timestamp) => throw new NotSupportedException();
    public long DiskBytes => Harness.Engines.FolderBytes(Path.GetDirectoryName(_path)!);
    public void Dispose() => _index.Dispose();
}
