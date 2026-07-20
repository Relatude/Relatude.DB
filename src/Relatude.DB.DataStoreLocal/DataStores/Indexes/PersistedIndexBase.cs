namespace Relatude.DB.DataStores.Indexes;

/// <summary>
/// Base class for indexes whose data lives in an <see cref="IPersistedIndexStore"/> (value and word
/// indexes backed by SQLite, the native KV store, Lucene, …) — as opposed to the in-memory indexes
/// that persist themselves through state files.
///
/// <para>It centralises the timestamp / first-commit protocol that the store and the startup loader
/// depend on, so a concrete index only implements its real query and mutation logic:</para>
/// <list type="bullet">
///   <item><see cref="PersistedTimestamp"/> reports 0 while the index is newly created and has never
///   been committed, then the store's timestamp afterwards. The startup loader takes the minimum of
///   these across all indexes to decide how far to replay the WAL.</item>
///   <item><see cref="FlagFirstCommit"/> is called by the store on the first successful commit of a
///   newly created index; it flips the index out of the "just created" state.</item>
///   <item>The memory-index state-file hooks are no-ops here: a persisted index's data lives in its
///   store, not in a state file.</item>
///   <item><see cref="WriteNewTimestampDueToRewriteHotswap"/> is a no-op: after a log rewrite/hot-swap
///   the store updates the timestamp for every persisted index in one call
///   (<see cref="IPersistedIndexStore.SetWalFileIdAndTimestamp"/>), so there is nothing to do per index.</item>
/// </list>
/// The remaining <see cref="IIndex"/> members are left to the concrete index, which declares the
/// specific interface it implements (<see cref="IValueIndex{T}"/> or <see cref="IWordIndex"/>).
/// </summary>
public abstract class PersistedIndexBase {
    readonly IPersistedIndexStore _store;
    bool _justCreated;

    protected PersistedIndexBase(IPersistedIndexStore store, bool justCreated) {
        _store = store;
        _justCreated = justCreated;
    }

    /// <summary>True until the store confirms this index's first commit via <see cref="FlagFirstCommit"/>.</summary>
    protected bool IsJustCreated => _justCreated;

    public long PersistedTimestamp => _justCreated ? 0 : _store.GetTimestamp();

    public void FlagFirstCommit() => _justCreated = false;

    // A persisted index does not use the memory-index state files; its store persists its data.
    public void ReadStateForMemoryIndexes(Guid walFileId) { }
    public void SaveStateForMemoryIndexes(long logTimestamp, Guid walFileId) { }

    // The store rewrites the timestamp for all persisted indexes in one call after a hot-swap.
    public void WriteNewTimestampDueToRewriteHotswap(long newTimestamp, Guid walFileId) { }
}
