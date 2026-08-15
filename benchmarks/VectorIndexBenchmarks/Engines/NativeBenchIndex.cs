using Relatude.DB.AI;
using Relatude.DB.AI.ISV;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;

namespace VectorIndexBenchmarks.Engines;

/// <summary>
/// The disk-based vector index (<see cref="NativeVectorIndex"/>): vectors live in immutable
/// segment files, only ids, offsets and centroids stay in memory, and a search probes the
/// <see cref="NativeVectorIndexOptions.Accuracy"/> fraction of clusters nearest the query, serving
/// hot blocks from a byte-budgeted cache. It is the only implementation here with a per-WAL-flush
/// durability hook: <see cref="NativeVectorIndex.MakeDurable"/> writes just the delta.
/// </summary>
public sealed class NativeBenchIndex : SemanticBenchIndex {
    readonly NativeVectorIndex _index;
    readonly string _dir;

    public NativeBenchIndex(string dir, Guid walId, AIEngine ai, NativeVectorIndexOptions options) {
        Directory.CreateDirectory(dir);
        _dir = dir;
        // A disabled set cache (size 0), for the same reason as the in-memory index: the filter
        // phase must reach the index on every call.
        _index = new NativeVectorIndex(new SetRegister(0), "bench", "bench", dir, ai, options);
        _index.ReadStateForMemoryIndexes(walId);
    }
    protected override ISemanticIndex Index => _index;
    public override Features Supported => Features.UnrankedFilter | Features.IncrementalDurability;
    public override void SaveState(long timestamp) => _index.SaveStateForMemoryIndexes(timestamp, Harness.Engines.WalFileId);
    public override void MakeDurable(long timestamp) => _index.MakeDurable(timestamp);
    public override long DiskBytes => Harness.Engines.FolderBytes(_dir);
    public override void Dispose() => _index.Dispose();
}
