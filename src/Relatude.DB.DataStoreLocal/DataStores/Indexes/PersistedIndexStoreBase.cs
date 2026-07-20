using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Sets;

namespace Relatude.DB.DataStores.Indexes;

/// <summary>
/// Base class for <see cref="IPersistedIndexStore"/> implementations. It owns the cross-cutting
/// rules that every backend must get right but that the interface alone does not express, so a
/// concrete backend only has to implement the storage primitives at the bottom of this file.
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
///   is not). The base guarantees the call happens; backends never think about it.</item>
///   <item>The word-index registry and lifecycle fan-out (create/dedupe, commit, optimize,
///   reset, delete-unopened, dispose) across the optional <see cref="IPersistentWordIndexFactory"/>.</item>
///   <item>The WAL-file-id / timestamp orchestration expressed over a few backend primitives, and
///   the invariant that <see cref="ResetAll"/> wipes index data but preserves the WAL id and
///   resets the timestamp to 0.</item>
/// </list>
///
/// <para>To add a new backend: derive from this class and implement the abstract members below.
/// Read each member's doc for its exact contract; you should not need to know anything else about
/// how RelatudeDB drives the store.</para>
/// </summary>
public abstract class PersistedIndexStoreBase : IPersistedIndexStore {

    // All word indexes opened this session, keyed by id, for dedupe + reuse on re-open.
    readonly Dictionary<string, IWordIndex> _wordIndexes = [];
    // The subset of word indexes produced by the factory: these self-manage their storage and
    // need lifecycle calls (commit/optimize/close/open/dispose). Built-in word indexes are not here.
    readonly Dictionary<string, IPersistentWordIndex> _factoryWordIndexes = [];
    // Value indexes + built-in word indexes, i.e. everything committed through this store's own
    // transaction and therefore taking part in the first-commit protocol below.
    readonly Dictionary<string, IIndex> _flaggableIndexes = [];
    // Ids of indexes created (not merely opened) this session, awaiting their first commit.
    readonly HashSet<string> _justCreated = [];

    bool _inTransaction;

    /// <summary>The word-index factory supplied at construction, or null if word indexes are
    /// backend-native (see <see cref="CreateBuiltInWordIndex"/>).</summary>
    protected IPersistentWordIndexFactory? WordIndexFactory { get; }

    /// <summary>True between <see cref="BeginTransaction"/> and its commit/rollback.</summary>
    protected bool IsInTransaction => _inTransaction;

    protected PersistedIndexStoreBase(IPersistentWordIndexFactory? wordIndexFactory) {
        WordIndexFactory = wordIndexFactory;
    }

    // ---- index opening ---------------------------------------------------------------------

    public IValueIndex<T> OpenValueIndex<T>(SetRegister sets, string id, string friendlyName, PropertyType type) where T : notnull {
        var index = CreateValueIndex<T>(sets, id, friendlyName, type, out var justCreated);
        registerFlaggable(id, index, justCreated);
        return index;
    }

    public IWordIndex OpenWordIndex(SetRegister sets, string id, string friendlyName, int minWordLength, int maxWordLength, bool prefixSearch, bool infixSearch) {
        if (_wordIndexes.TryGetValue(id, out var existing)) return existing; // idempotent re-open
        IWordIndex index;
        if (WordIndexFactory != null) {
            var idx = WordIndexFactory.Create(sets, this, id, friendlyName, minWordLength, maxWordLength, prefixSearch, infixSearch);
            _factoryWordIndexes[id] = idx;
            index = idx;
            // Factory word indexes are intentionally NOT part of the first-commit protocol: they
            // track their own persisted state on disk and are flagged from that, not by the store.
        } else {
            index = CreateBuiltInWordIndex(sets, id, friendlyName, minWordLength, maxWordLength, prefixSearch, infixSearch, out var justCreated);
            registerFlaggable(id, index, justCreated);
        }
        _wordIndexes[id] = index;
        return index;
    }

    void registerFlaggable(string id, IIndex index, bool justCreated) {
        _flaggableIndexes[id] = index;
        if (justCreated) _justCreated.Add(id);
    }

    // ---- transactions ----------------------------------------------------------------------

    public void BeginTransaction() {
        if (_inTransaction) throw new InvalidOperationException("A transaction is already active; the index store supports a single writer.");
        BeginTransactionCore();
        _inTransaction = true;
    }

    public void CommitTransaction(long timestamp) {
        if (!_inTransaction) throw new InvalidOperationException("No transaction is currently active.");
        // 1) The backend atomically persists both the index data and the timestamp.
        CommitTransactionCore(timestamp);
        // 2) Flush factory word indexes (unless the backend defers this, see the flag below).
        if (CommitFactoryWordIndexesOnCommit)
            foreach (var w in _factoryWordIndexes.Values) w.Commit();
        // 3) Newly created indexes are now durably backed: flip them off "just created".
        if (_justCreated.Count > 0) {
            foreach (var id in _justCreated) _flaggableIndexes[id].FlagFirstCommit();
            _justCreated.Clear();
        }
        _inTransaction = false;
    }

    public void RollbackTransaction() {
        if (!_inTransaction) throw new InvalidOperationException("No transaction is currently active.");
        RollbackTransactionCore();
        _inTransaction = false;
    }

    public void CleanUpOnUnknownTransactionError() {
        if (!_inTransaction) return;
        try { RollbackTransactionCore(); } catch { /* best effort: the caller is already failing */ }
        _inTransaction = false;
    }

    // ---- WAL id / timestamp ----------------------------------------------------------------

    public Guid GetWalFileId() => ReadWalFileId();
    public void SetWalFileId(Guid walFileId) => WriteWalFileId(walFileId, null);
    public void SetWalFileIdAndTimestamp(long timestamp, Guid walFileId) => WriteWalFileId(walFileId, timestamp);

    // ---- maintenance / lifecycle -----------------------------------------------------------

    public void OptimizeDisk() {
        OptimizeDiskCore();
        // Maintenance is not on the hot path, so every backend optimizes its factory word indexes.
        foreach (var w in _factoryWordIndexes.Values) w.OptimizeAndMerge();
    }

    public void DeleteUnopenedIndexes() {
        if (_inTransaction) throw new InvalidOperationException("DeleteUnopenedIndexes cannot run while a transaction is active.");
        DeleteUnopenedIndexesCore();
        WordIndexFactory?.DeleteUnopenedFiles(_factoryWordIndexes.Keys);
    }

    public void ResetAll() {
        if (_inTransaction) throw new InvalidOperationException("ResetAll cannot run while a transaction is active.");
        var walFileId = ReadWalFileId(); // the WAL id must survive a reset (it ties the indexes to a log file)
        ResetAllDataCore();
        WriteWalFileId(walFileId, timestamp: 0); // data is gone, so the persisted timestamp restarts at 0
        // The backing data is gone: drop any cached counts/state the open indexes still hold.
        foreach (var i in _flaggableIndexes.Values) i.ClearCache();
        // Rebuild the factory word indexes from empty.
        foreach (var w in _factoryWordIndexes.Values) w.Close();
        WordIndexFactory?.DeleteAllFiles();
        foreach (var w in _factoryWordIndexes.Values) w.Open();
    }

    public void Dispose() {
        foreach (var w in _factoryWordIndexes.Values) {
            try { w.Dispose(); } catch { }
        }
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    // ========================================================================================
    //  Backend primitives — implement these and nothing about the orchestration above changes.
    // ========================================================================================

    /// <summary>
    /// Whether <see cref="CommitTransaction"/> should call <see cref="IPersistentWordIndex.Commit"/>
    /// on each factory word index after every data commit. Default true. Override to false for a
    /// backend whose word indexes are near-real-time and rebuilt from the WAL on restart, where a
    /// per-transaction commit would only add cost.
    /// </summary>
    protected virtual bool CommitFactoryWordIndexesOnCommit => true;

    /// <summary>Create (or open) the backend value index for <paramref name="id"/>.
    /// Set <paramref name="justCreated"/> true only when the underlying storage did not exist and
    /// was created now — this drives the first-commit protocol.</summary>
    protected abstract IValueIndex<T> CreateValueIndex<T>(SetRegister sets, string id, string friendlyName, PropertyType type, out bool justCreated) where T : notnull;

    /// <summary>Create a word index using backend-native storage. Only called when no
    /// <see cref="WordIndexFactory"/> was supplied; a backend without a built-in word index should
    /// throw <see cref="InvalidOperationException"/>. Set <paramref name="justCreated"/> as in
    /// <see cref="CreateValueIndex{T}"/>.</summary>
    protected abstract IWordIndex CreateBuiltInWordIndex(SetRegister sets, string id, string friendlyName, int minWordLength, int maxWordLength, bool prefixSearch, bool infixSearch, out bool justCreated);

    /// <summary>Begin the backend's single write transaction. The base has already verified none is active.</summary>
    protected abstract void BeginTransactionCore();

    /// <summary>Atomically persist the transaction's index data together with <paramref name="timestamp"/>
    /// as the store's timestamp (see <see cref="GetTimestamp"/>). Durable.</summary>
    protected abstract void CommitTransactionCore(long timestamp);

    /// <summary>Discard the active transaction's changes. The base has already verified one is active.</summary>
    protected abstract void RollbackTransactionCore();

    /// <summary>The WAL file id persisted in the store, or <see cref="Guid.Empty"/> if none.</summary>
    protected abstract Guid ReadWalFileId();

    /// <summary>Durably persist the WAL file id, and — when <paramref name="timestamp"/> is not null —
    /// the store timestamp too, atomically. Runs outside the normal data transaction.</summary>
    protected abstract void WriteWalFileId(Guid walFileId, long? timestamp);

    /// <summary>The store timestamp recorded by the most recent commit (or WAL-id-and-timestamp write).</summary>
    public abstract long GetTimestamp();

    /// <summary>Total bytes of storage the backend currently uses (0 for a memory-only backend).</summary>
    public abstract long GetTotalDiskSpace();

    /// <summary>Backend-specific disk optimization (e.g. VACUUM). Factory word indexes are handled by the base.</summary>
    protected abstract void OptimizeDiskCore();

    /// <summary>Delete value/built-in indexes that exist in storage but were not opened this session,
    /// and drop their persisted timestamps. Factory word-index files are handled by the base.</summary>
    protected abstract void DeleteUnopenedIndexesCore();

    /// <summary>Wipe all index data. Must leave the settings/WAL-id storage functional: the base
    /// re-writes the WAL id and a timestamp of 0 immediately after this returns.</summary>
    protected abstract void ResetAllDataCore();

    /// <summary>Release backend resources (files, connections). Factory word indexes are already disposed.</summary>
    protected abstract void DisposeCore();
}
