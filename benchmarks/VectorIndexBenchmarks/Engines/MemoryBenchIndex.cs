using Relatude.DB.AI;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;

namespace VectorIndexBenchmarks.Engines;

/// <summary>
/// The built-in in-memory semantic index (<c>MemorySemanticIndex</c> over
/// <c>FlatMemoryVectorIndex</c>) — the reference implementation, and what a store without a
/// persisted semantic index engine runs. Every vector is kept on the managed heap and every search
/// is an exact SIMD scan of all of them, so it is always accurate and its footprint is the data
/// itself. Durability is one state file containing every vector, written by
/// <see cref="IIndex.SaveStateForMemoryIndexes"/>, which the store schedules periodically rather
/// than per transaction.
/// </summary>
public sealed class MemoryBenchIndex : IBenchVectorIndex {
    readonly IIOProvider _io;
    readonly string _dir;

    public MemoryBenchIndex(string dir, Guid walId, AIEngine ai) {
        Directory.CreateDirectory(dir);
        _dir = dir;
        _io = new IOProviderDisk(dir);
        // A disabled set cache (size 0): the unranked filter phase must reach the index on every
        // call, not be answered by the SetRegister, which is not what this benchmark is measuring.
        Index = new MemorySemanticIndex(new SetRegister(0), "bench", "bench", _io, new FileKeyUtility(null), ai, _ => { });
        Index.ReadStateForMemoryIndexes(walId);
    }
    public ISemanticIndex Index { get; }
    public bool SupportsIncrementalDurability => false;
    public void SaveState(long timestamp) => Index.SaveStateForMemoryIndexes(timestamp, Harness.Engines.WalFileId);
    public void MakeDurable(long timestamp) => throw new NotSupportedException();
    public long DiskBytes => Harness.Engines.FolderBytes(_dir);
    public void Dispose() {
        Index.Dispose();
        _io.CloseAllOpenStreams();
    }
}
