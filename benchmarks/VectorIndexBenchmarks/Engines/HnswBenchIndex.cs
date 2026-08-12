using Relatude.DB.AI;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.VectorIndexHNSW;

namespace VectorIndexBenchmarks.Engines;

/// <summary>
/// The disk-based HNSW index (<see cref="HnswVectorIndex"/>): the same storage discipline as
/// <see cref="NativeBenchIndex"/> — vectors in a file, only the routing structure in memory, a
/// byte-budgeted cache over what it reads, and a per-WAL-flush durability hook — but it finds the
/// neighbours by walking a proximity graph instead of by probing the nearest clusters.
///
/// <para>It is the interesting row of this table: USearch already shows what an HNSW graph does when
/// the whole thing is in memory, and the IVF index shows what disk residency costs an index that
/// reads a fixed share of the data. This one is the combination — a graph whose vectors live on disk —
/// so reading it next to those two separates the algorithm from where the data sits.</para>
///
/// <para><b>Dials.</b> Its effort is <c>EfSearch</c>, not a fraction of the index, so it takes the
/// harness's HNSW options (<c>--hnsw-m</c>, <c>--hnsw-ef-add</c>, <c>--hnsw-ef</c>) rather than
/// <c>--accuracy</c> — the same numbers USearch gets, which makes those two directly comparable at
/// matched settings and comparable to the IVF index at matched recall.</para>
/// </summary>
public sealed class HnswBenchIndex : SemanticBenchIndex {
    readonly HnswVectorIndex _index;
    readonly string _dir;

    public HnswBenchIndex(string dir, Guid walId, AIEngine ai, HnswVectorIndexOptions options) {
        Directory.CreateDirectory(dir);
        _dir = dir;
        // A disabled set cache (size 0), for the same reason as the other Relatude indexes: the
        // filter phase must reach the index on every call.
        _index = new HnswVectorIndex(new SetRegister(0), "bench", "bench", dir, ai, options);
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
