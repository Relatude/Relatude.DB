namespace Relatude.DB.DataStores.Indexes;

/// <summary>
/// Base class for indexes whose data commits atomically with their engine's timestamp — the value,
/// array and built-in word indexes backed by SQLite or the native KV store — as opposed to the
/// in-memory indexes that persist themselves through state files, and to indexes that carry their
/// own persisted timestamp (e.g. the Lucene word indexes, whose data is committed independently of
/// the engine transaction and who therefore must never borrow the engine's timestamp).
///
/// <para>It centralises the timestamp / first-commit protocol that the engine and the startup loader
/// depend on, so a concrete index only implements its real query and mutation logic:</para>
/// <list type="bullet">
///   <item><see cref="PersistedTimestamp"/> reports 0 while the index is newly created and has never
///   been committed, then the engine's timestamp afterwards. The startup loader takes the minimum of
///   these across all indexes to decide how far to replay the WAL.</item>
///   <item><see cref="FlagFirstCommit"/> is called by the engine on the first successful commit of a
///   newly created index; it flips the index out of the "just created" state.</item>
///   <item>The memory-index state-file hooks are no-ops here: a persisted index's data lives in its
///   engine, not in a state file.</item>
///   <item><see cref="WriteNewTimestampDueToRewriteHotswap"/> is a no-op: after a log rewrite/hot-swap
///   the engine updates the timestamp for every persisted index in one call
///   (<see cref="IIndexEngine.SetWalFileIdAndTimestamp"/>), so there is nothing to do per index.</item>
/// </list>
/// The remaining <see cref="IIndex"/> members are left to the concrete index, which declares the
/// specific interface it implements (<see cref="IValueIndex{T}"/> or <see cref="IWordIndex"/>).
/// </summary>
public abstract class PersistedIndexBase {
    readonly IIndexEngine _engine;
    bool _justCreated;

    protected PersistedIndexBase(IIndexEngine engine, bool justCreated) {
        _engine = engine;
        _justCreated = justCreated;
    }

    /// <summary>True until the engine confirms this index's first commit via <see cref="FlagFirstCommit"/>.</summary>
    protected bool IsJustCreated => _justCreated;

    public long PersistedTimestamp => _justCreated ? 0 : _engine.GetTimestamp();

    public void FlagFirstCommit() => _justCreated = false;

    // A persisted index does not use the memory-index state files; its engine persists its data.
    public void ReadStateForMemoryIndexes(Guid walFileId) { }
    public void SaveStateForMemoryIndexes(long logTimestamp, Guid walFileId) { }

    // The engine rewrites the timestamp for all persisted indexes in one call after a hot-swap.
    public void WriteNewTimestampDueToRewriteHotswap(long newTimestamp, Guid walFileId) { }
}
