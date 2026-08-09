using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Sets;

namespace Relatude.DB.DataStores.Indexes;

/// <summary>
/// The lifecycle contract every persisted index engine implements, regardless of the kind of
/// indexes it serves (value, text, later vector). The data store drives all engines uniformly
/// through <see cref="IndexEngines"/>: one Begin/Commit/Rollback cycle per transaction, one
/// MakeDurable after every WAL flush, and the WAL-binding/replay protocol at startup.
///
/// <para>The two invariants an engine must uphold:</para>
/// <list type="bullet">
///   <item><b>Durable state never ahead of the durable log.</b> <see cref="CommitTransaction"/> may
///   publish in memory only; durability may be deferred to <see cref="MakeDurable"/>, which the
///   data store calls right after a successful WAL flush. A crash rolls the engine back to its last
///   durable checkpoint, never forward past the log.</item>
///   <item><b><see cref="GetTimestamp"/> reflects the engine's own durably recoverable position</b> —
///   the timestamp a fresh process would observe after a crash right now. It must never borrow
///   another engine's timestamp: the startup loader compares it (and each index's
///   <see cref="IIndex.PersistedTimestamp"/>) against the WAL to decide how much log to replay, so a
///   too-new answer silently skips the replay that would repair the engine.</item>
/// </list>
/// </summary>
public interface IIndexEngine : IDisposable {
    /// <summary>Short human-readable engine name, used in index friendly names and log messages.</summary>
    string Name { get; }

    // ---- WAL binding -------------------------------------------------------------------------
    /// <summary>The WAL file id this engine's data belongs to, or <see cref="Guid.Empty"/> if none
    /// has been persisted yet. On open, a mismatch with the actual log file resets the engine.</summary>
    Guid GetWalFileId();
    /// <summary>Durably persist the WAL file id (first binding of a fresh engine).</summary>
    void SetWalFileId(Guid walFileId);
    /// <summary>Atomically rebind the engine (and every index it owns) to a new WAL file id and
    /// timestamp after a log rewrite hot-swap.</summary>
    void SetWalFileIdAndTimestamp(long timestamp, Guid walFileId);
    /// <summary>The engine's own durably recoverable position; 0 when empty or freshly created.</summary>
    long GetTimestamp();

    // ---- transaction protocol (single writer, driven by the data store) -----------------------
    void BeginTransaction();
    /// <summary>Atomically publish the transaction together with <paramref name="timestamp"/>.
    /// Must be immediately visible to readers; durability may be deferred to <see cref="MakeDurable"/>.</summary>
    void CommitTransaction(long timestamp);
    void RollbackTransaction();
    /// <summary>Best-effort cleanup when a transaction failed in an unknown state; the store is
    /// moving to its error state and will be reopened.</summary>
    void CleanUpOnUnknownTransactionError();
    /// <summary>Durably persist everything committed so far. Called right after a successful WAL
    /// flush, so the durable engine state can never contain transactions the durable log is missing.
    /// Only allowed outside a transaction.</summary>
    void MakeDurable();

    // ---- maintenance / lifecycle ---------------------------------------------------------------
    /// <summary>
    /// Durably deletes every persisted index that has not been opened in this session, data and
    /// definition included; open indexes are untouched. Call only after every index in the current
    /// schema has been opened: anything still unopened is then an index that has left the schema,
    /// and deleting it ensures a later re-add starts fresh (timestamp 0, forcing a rebuild)
    /// instead of resurrecting stale data that claims to be current.
    /// Only allowed outside a transaction.
    /// </summary>
    void DeleteUnopenedIndexes();
    /// <summary>Wipe all index data but keep the WAL binding, resetting the timestamp to 0 so the
    /// startup loader rebuilds everything from the log.</summary>
    void ResetAll();
    void OptimizeDisk();
    long GetTotalDiskSpace();
}

/// <summary>Engine serving the value and array indexes (equality/range/facet queries).</summary>
public interface IValueIndexEngine : IIndexEngine {
    IValueIndex<T> OpenValueIndex<T>(SetRegister sets, string id, string friendlyName, PropertyType type) where T : notnull;
    IStringArrayIndex OpenStringArrayIndex(SetRegister sets, string id, string friendlyName, PropertyType type);
    IGuidArrayIndex OpenGuidArrayIndex(SetRegister sets, string id, string friendlyName, PropertyType type);
    IIntArrayIndex OpenIntArrayIndex(SetRegister sets, string id, string friendlyName, PropertyType type);
    /// <summary>Persist any derived query caches the engine maintains (e.g. the facet-set sidecar),
    /// so the next open starts warm. No-op for engines without such caches.</summary>
    void SaveIndexCaches(bool force);
    /// <summary>Drop the persisted derived caches; the engine rebuilds them lazily.</summary>
    void ResetIndexCaches();
}

/// <summary>Engine serving the full-text word indexes. A backend may implement this alongside
/// <see cref="IValueIndexEngine"/> on the same instance (e.g. SQLite with FTS5 tables sharing the
/// value store's transaction); <see cref="IndexEngines"/> de-duplicates lifecycle calls by
/// reference so a dual-role backend gets one Begin/Commit cycle, not two.</summary>
public interface ITextIndexEngine : IIndexEngine {
    IWordIndex OpenWordIndex(SetRegister sets, string id, string friendlyName, WordIndexOptions options);
}

/// <summary>Per-property word index configuration, taken from the string property model.</summary>
public sealed record WordIndexOptions(int MinWordLength, int MaxWordLength, bool PrefixSearch, bool InfixSearch);
