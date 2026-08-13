using Relatude.DB.AI;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.VectorIndexHNSW2;

namespace VectorIndexBenchmarks.Engines;

/// <summary>
/// The second-generation HNSW index (<see cref="Hnsw2VectorIndex"/>): the same graph algorithm and
/// the same durability protocol as <see cref="HnswBenchIndex"/>, rebuilt around where the data sits.
/// The graph a search walks — int8 vectors and neighbour lists — lives in flat, prefetch-friendly
/// memory whenever the general budget (<c>MaxMemoryBytes</c>) allows, with the float vectors
/// mirrored too when they fit and read from their own file only for exact re-scoring when they do
/// not; in <c>LowMemoryMode</c> the graph stays on disk behind a small cache of routing records a
/// quarter the size of float records.
///
/// <para>Reading it next to <see cref="HnswBenchIndex"/> shows what the layout is worth at matched
/// settings: same algorithm, same dials (<c>--hnsw-m</c>, <c>--hnsw-ef-add</c>, <c>--hnsw-ef</c>),
/// different memory discipline. Next to USearch it shows what a persistent, WAL-bound index gives up
/// (or does not) against a pure in-memory library.</para>
/// </summary>
public sealed class Hnsw2BenchIndex : SemanticBenchIndex {
    readonly Hnsw2VectorIndex _index;
    readonly string _dir;

    public Hnsw2BenchIndex(string dir, Guid walId, AIEngine ai, Hnsw2VectorIndexOptions options) {
        Directory.CreateDirectory(dir);
        _dir = dir;
        // A disabled set cache (size 0), for the same reason as the other Relatude indexes: the
        // filter phase must reach the index on every call.
        _index = new Hnsw2VectorIndex(new SetRegister(0), "bench", "bench", dir, ai, options);
        _index.ReadStateForMemoryIndexes(walId);
    }
    protected override ISemanticIndex Index => _index;
    public override Features Supported => Features.UnrankedFilter | Features.IncrementalDurability;
    /// <summary>The parallel bulk path a store's initial ingest or WAL replay uses.</summary>
    public override void AddBatch(IReadOnlyList<(int nodeId, float[] vector)> items) => _index.AddRange(items);
    public override void SaveState(long timestamp) => _index.SaveStateForMemoryIndexes(timestamp, Harness.Engines.WalFileId);
    public override void MakeDurable(long timestamp) => _index.MakeDurable(timestamp);
    public override long DiskBytes => Harness.Engines.FolderBytes(_dir);
    public override void Dispose() => _index.Dispose();
}
