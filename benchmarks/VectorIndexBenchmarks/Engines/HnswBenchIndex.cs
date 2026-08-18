using Relatude.DB.AI;
using Relatude.DB.AI.HNSW;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;

namespace VectorIndexBenchmarks.Engines;

/// <summary>
/// The Relatude HNSW index (<see cref="VectorIndex"/>): the graph a search walks — int8 vectors
/// and neighbour lists — always lives in flat, prefetch-friendly memory; the general budget
/// (<c>MaxMemoryBytes</c>) decides whether the float vectors are mirrored too or read from their
/// own file only for exact re-scoring.
///
/// <para>Reading it next to USearch at matched dials (<c>--hnsw-m</c>, <c>--hnsw-ef-add</c>,
/// <c>--hnsw-ef</c>) shows what a persistent, WAL-bound index gives up (or does not) against a pure
/// in-memory library.</para>
/// </summary>
public sealed class HnswBenchIndex : SemanticBenchIndex {
    readonly VectorIndex _index;
    readonly string _dir;

    public HnswBenchIndex(string dir, Guid walId, AIEngine ai, VectorIndexOptions options) {
        Directory.CreateDirectory(dir);
        _dir = dir;
        // A disabled set cache (size 0), for the same reason as the other Relatude indexes: the
        // filter phase must reach the index on every call.
        _index = new VectorIndex(new SetRegister(0), "bench", "bench", dir, ai, options);
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
