using Relatude.DB.Common;

namespace Relatude.DB.DataStores.Indexes;

/// <summary>
/// Base class for <see cref="IIndexEngine"/> implementations. It owns the cross-cutting rules that
/// every engine must get right but that the interface alone does not express, so a concrete engine
/// only has to implement the storage primitives at the bottom of this file.
///
/// <para>What the base owns:</para>
/// <list type="bullet">
///   <item>The single-writer transaction guard (no nesting; commit/rollback require an active
///   transaction; best-effort cleanup on unknown errors).</item>
///   <item>The "first commit" protocol: an index created this session reports
///   <see cref="IIndex.PersistedTimestamp"/> 0 until its first successful commit, at which point
///   <see cref="IIndex.FlagFirstCommit"/> is called. The startup loader reads the minimum
///   persisted timestamp across all indexes to decide how far back to replay the WAL, so missing
///   this call causes silent full replays (or, worse, an index that claims to be current when it
///   is not). The base guarantees the call happens; engines never think about it. Engines whose
///   indexes carry their own persisted timestamps (e.g. Lucene commit data) simply never register
///   an index as just-created.</item>
///   <item>The add/remove queue lifecycle of the <see cref="OptimizedValueIndex{T}"/> /
///   <see cref="OptimizedWordIndex"/> wrappers the engine hands out. A queued remove only exists in
///   memory: it must be executed against the backend before every commit (otherwise the commit
///   durably records a timestamp that covers a remove it does not contain, and the later lazy
///   dequeue would mutate the backend outside any transaction). It must also be executed on
///   rollback: non-transactional backends (e.g. Lucene) are not covered by
///   <see cref="RollbackTransactionCore"/>, so for them the queued remove is the compensating
///   operation of the reversal actions that already ran; for transactional backends the extra
///   remove lands in the transaction that is rolled back right after, which is harmless.</item>
///   <item>The WAL-file-id / timestamp orchestration expressed over a few backend primitives, and
///   the invariant that <see cref="ResetAll"/> wipes index data but preserves the WAL id and
///   resets the timestamp to 0.</item>
/// </list>
/// </summary>
public abstract class IndexEngineBase : IIndexEngine {

    // Opened indexes taking part in the base's cross-cutting rules (cache clearing on reset, and —
    // for the subset in _justCreated — the first-commit protocol).
    readonly Dictionary<string, IIndex> _managedIndexes = [];
    // Ids of indexes created (not merely opened) this session, awaiting their first commit.
    readonly HashSet<string> _justCreated = [];
    // The add/remove optimization queues of the wrappers handed out by the engine's open methods,
    // keyed by index id so repeated opens do not register duplicates.
    readonly Dictionary<string, AddRemoveOptimization> _indexQueues = [];

    bool _inTransaction;

    /// <summary>True between <see cref="BeginTransaction"/> and its commit/rollback.</summary>
    protected bool IsInTransaction => _inTransaction;

    public virtual string Name => GetType().Name.Decamelize();

    /// <summary>Register an opened index for the base's lifecycle rules. Pass
    /// <paramref name="justCreated"/> true only when the underlying storage did not exist and was
    /// created now — this drives the first-commit protocol.</summary>
    protected void RegisterManagedIndex(string id, IIndex index, bool justCreated) {
        _managedIndexes[id] = index;
        if (justCreated) _justCreated.Add(id);
    }

    /// <summary>Register the add/remove queue of a wrapper handed out by an open method, so the
    /// base can flush it at every commit boundary and on rollback. The key must be unique per
    /// index (prefix by kind when value and word indexes can share ids).</summary>
    protected void RegisterQueue(string key, AddRemoveOptimization queue) {
        _indexQueues[key] = queue;
    }

    /// <summary>Applies the add/remove optimization wrapper to a word index and registers its
    /// queue with the base, so the queued remove is flushed at every commit boundary. Text engines
    /// hand out the returned wrapper, never the raw index.</summary>
    protected IWordIndex WrapWordIndexAndRegisterQueue(string id, IWordIndex index) {
        var optimized = new OptimizedWordIndex(index);
        RegisterQueue("w:" + id, optimized.Queue);
        return optimized;
    }

    // ---- transactions ----------------------------------------------------------------------

    public void BeginTransaction() {
        if (_inTransaction) throw new InvalidOperationException("A transaction is already active; the index engine supports a single writer.");
        BeginTransactionCore();
        _inTransaction = true;
    }

    public void CommitTransaction(long timestamp) {
        if (!_inTransaction) throw new InvalidOperationException("No transaction is currently active.");
        // 0) Execute any queued (lazy) removes now, while the backend transaction is still open,
        //    so the commit actually contains them. Outside a transaction every queue is empty.
        foreach (var q in _indexQueues.Values) q.Dequeue();
        // 1) The backend atomically persists (or publishes) both the index data and the timestamp.
        CommitTransactionCore(timestamp);
        // 2) Newly created indexes are now durably backed: flip them off "just created".
        if (_justCreated.Count > 0) {
            foreach (var id in _justCreated) _managedIndexes[id].FlagFirstCommit();
            _justCreated.Clear();
        }
        _inTransaction = false;
    }

    public void RollbackTransaction() {
        if (!_inTransaction) throw new InvalidOperationException("No transaction is currently active.");
        // Execute (not discard) queued removes: the reversal actions that ran before this call may
        // have queued a compensating remove, and non-transactional backends (e.g. Lucene) are not
        // undone by RollbackTransactionCore — dropping the remove would leave a phantom document.
        // For transactional backends the remove lands in the transaction rolled back below: harmless.
        foreach (var q in _indexQueues.Values) {
            try { q.Dequeue(); } catch { q.Discard(); } // queue must be empty afterwards either way
        }
        RollbackTransactionCore();
        _inTransaction = false;
    }

    public void CleanUpOnUnknownTransactionError() {
        if (!_inTransaction) return;
        // No reversal ran on this path and the store is moving to error state: discarding is the
        // safest way to guarantee empty queues without risking further backend calls.
        foreach (var q in _indexQueues.Values) q.Discard();
        try { RollbackTransactionCore(); } catch { /* best effort: the caller is already failing */ }
        _inTransaction = false;
    }

    public void MakeDurable() {
        if (_inTransaction) throw new InvalidOperationException("MakeDurable cannot run while a transaction is active.");
        MakeDurableCore();
    }

    // ---- WAL id / timestamp ----------------------------------------------------------------

    public Guid GetWalFileId() => ReadWalFileId();
    public void SetWalFileId(Guid walFileId) => WriteWalFileId(walFileId, null);
    public virtual void SetWalFileIdAndTimestamp(long timestamp, Guid walFileId) => WriteWalFileId(walFileId, timestamp);

    // ---- maintenance / lifecycle -----------------------------------------------------------

    public void DeleteUnopenedIndexes() {
        if (_inTransaction) throw new InvalidOperationException("DeleteUnopenedIndexes cannot run while a transaction is active.");
        DeleteUnopenedIndexesCore();
    }

    public void ResetAll() {
        if (_inTransaction) throw new InvalidOperationException("ResetAll cannot run while a transaction is active.");
        var walFileId = ReadWalFileId(); // the WAL id must survive a reset (it ties the indexes to a log file)
        ResetAllDataCore();
        WriteWalFileId(walFileId, timestamp: 0); // data is gone, so the persisted timestamp restarts at 0
        // The backing data is gone: drop any cached counts/state the open indexes still hold.
        foreach (var i in _managedIndexes.Values) i.ClearCache();
    }

    public void Dispose() {
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    // ========================================================================================
    //  Backend primitives — implement these and nothing about the orchestration above changes.
    // ========================================================================================

    /// <summary>Begin the backend's single write transaction. The base has already verified none is active.</summary>
    protected abstract void BeginTransactionCore();

    /// <summary>
    /// Atomically persist the transaction's index data together with <paramref name="timestamp"/>
    /// as the engine's timestamp (see <see cref="GetTimestamp"/>). A backend may defer durability to
    /// <see cref="MakeDurableCore"/> (the commit must still be atomic and immediately visible to
    /// readers) — until the durable checkpoint, a crash rolls the backend back to the previous one.
    /// Deferring is only safe when the committed data can be reproduced from the data-store WAL,
    /// which holds for the index data an engine manages.
    /// </summary>
    protected abstract void CommitTransactionCore(long timestamp);

    /// <summary>Durably persist everything committed so far. No-op (the default) for backends that
    /// are durable per commit. The base has already verified no transaction is active.</summary>
    protected virtual void MakeDurableCore() { }

    /// <summary>Discard the active transaction's changes. The base has already verified one is active.
    /// A non-transactional backend implements this as a no-op and relies on the queue semantics
    /// described on the class.</summary>
    protected abstract void RollbackTransactionCore();

    /// <summary>The WAL file id persisted in the engine, or <see cref="Guid.Empty"/> if none.</summary>
    protected abstract Guid ReadWalFileId();

    /// <summary>Durably persist the WAL file id, and — when <paramref name="timestamp"/> is not null —
    /// the engine timestamp too, atomically. Runs outside the normal data transaction.</summary>
    protected abstract void WriteWalFileId(Guid walFileId, long? timestamp);

    /// <summary>The engine timestamp recorded by the most recent commit (or WAL-id-and-timestamp write).</summary>
    public abstract long GetTimestamp();

    /// <summary>Total bytes of storage the backend currently uses (0 for a memory-only backend).</summary>
    public abstract long GetTotalDiskSpace();

    /// <summary>Backend-specific disk optimization (e.g. VACUUM, segment merge).</summary>
    public abstract void OptimizeDisk();

    /// <summary>Delete indexes that exist in storage but were not opened this session, and drop
    /// their persisted timestamps.</summary>
    protected abstract void DeleteUnopenedIndexesCore();

    /// <summary>Wipe all index data. Must leave the settings/WAL-id storage functional: the base
    /// re-writes the WAL id and a timestamp of 0 immediately after this returns.</summary>
    protected abstract void ResetAllDataCore();

    /// <summary>Release backend resources (files, connections).</summary>
    protected abstract void DisposeCore();
}
