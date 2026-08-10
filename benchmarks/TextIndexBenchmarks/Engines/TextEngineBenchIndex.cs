using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;

namespace TextIndexBenchmarks.Engines;

/// <summary>
/// Any <see cref="ITextIndexEngine"/> (Lucene, SQLite FTS5, the built-in disk index), driven
/// through the same lifecycle the data store uses: one begin/commit per transaction, and
/// <see cref="IIndexEngine.MakeDurable"/> as the durability checkpoint after a WAL flush.
/// The engine hands out the index already wrapped in <see cref="OptimizedWordIndex"/>.
/// </summary>
public sealed class TextEngineBenchIndex : IBenchWordIndex {
    readonly ITextIndexEngine _engine;
    public TextEngineBenchIndex(ITextIndexEngine engine, Guid walId, Features supported, WordIndexOptions options) {
        _engine = engine;
        // a fresh engine adopts the log id; a reopened one already carries it (a mismatch would
        // reset the index, which is exactly what the store's BindToWalFile does)
        if (_engine.GetWalFileId() == Guid.Empty) _engine.SetWalFileId(walId);
        // cache disabled (size 0): the set cache would answer repeated unranked searches from the
        // SetRegister instead of the index, which is not what this benchmark is measuring
        Index = _engine.OpenWordIndex(new SetRegister(0), "bench", "bench", options);
        Supported = supported;
    }
    public IWordIndex Index { get; }
    public Features Supported { get; }
    public void Begin() => _engine.BeginTransaction();
    public void Commit(long timestamp) => _engine.CommitTransaction(timestamp);
    public void Persist(long timestamp) => _engine.MakeDurable();
    public long DiskBytes => _engine.GetTotalDiskSpace();
    public void Dispose() => _engine.Dispose();
}
