using Relatude.DB.DataStores.Indexes;

namespace VectorIndexBenchmarks.Engines;

/// <summary>
/// One benchmarked semantic index, driven the way the data store drives it. A semantic index is
/// not part of the index-engine transaction protocol: writes are visible as soon as they are made,
/// and durability comes from the memory-index state protocol
/// (<see cref="IIndex.SaveStateForMemoryIndexes"/>), which the store schedules periodically.
/// The disk index additionally follows every WAL flush with a delta flush, which the in-memory one
/// has no equivalent for — that difference is measured rather than hidden.
/// </summary>
public interface IBenchVectorIndex : IDisposable {
    /// <summary>The index under test, driven only through the interface the data store uses, so
    /// both implementations answer the exact same calls.</summary>
    ISemanticIndex Index { get; }
    /// <summary>Write the durable state at a state save: a full state file for the in-memory index,
    /// a segment flush plus manifest swap (and any due maintenance) for the disk index.</summary>
    void SaveState(long timestamp);
    /// <summary>The post-WAL-flush durability hook, when the implementation has one: the disk index
    /// persists just the delta since the last flush. False for an index whose durability is only
    /// the periodic state save — the store replays the WAL for anything newer.</summary>
    bool SupportsIncrementalDurability { get; }
    void MakeDurable(long timestamp);
    long DiskBytes { get; }
}
