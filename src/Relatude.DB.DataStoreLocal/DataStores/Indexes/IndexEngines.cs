namespace Relatude.DB.DataStores.Indexes;

/// <summary>
/// The set of persisted index engines a data store runs with, each registered under the id of its
/// <see cref="IndexEngineSettings"/> entry so an index can be routed to the engine its settings name.
/// The data store drives every lifecycle call through this composite instead of talking to engines
/// individually, so the orchestration (transaction cycle, WAL binding, replay checkpoints,
/// maintenance fan-out) is written once.
///
/// <para>Engines are de-duplicated by reference: a dual-role backend (e.g. SQLite serving both
/// value and FTS5 word indexes from one connection) is registered under several ids but receives
/// each lifecycle call exactly once, keeping its single-transaction atomicity.</para>
///
/// <para>Cross-engine atomicity is deliberately not required. After a crash, engines may be at
/// different durable timestamps; the startup loader replays the WAL into each engine from its own
/// position (see <see cref="IIndexEngine.GetTimestamp"/>), which reconciles them. The only hard
/// invariant is per engine: durable engine state never ahead of the durable log.</para>
/// </summary>
public sealed class IndexEngines : IDisposable {
    public static readonly IndexEngines Empty = new(); // stateless, safe to share
    readonly Dictionary<Guid, IValueIndexEngine> _value = [];
    readonly Dictionary<Guid, ITextIndexEngine> _text = [];
    readonly Dictionary<Guid, ISemanticIndexEngine> _vector = [];
    readonly IIndexEngine[] _distinct; // reference-deduped, fixed fan-out order
    /// <summary>The semantic (vector) engines. Not part of the transaction/WAL fan-out below: semantic
    /// indexes are driven through the memory-index protocol and carry their own persisted positions,
    /// see <see cref="ISemanticIndexEngine"/>.</summary>
    readonly ISemanticIndexEngine[] _distinctVector;
    /// <summary>
    /// Registers each engine under the id the settings know it by. An id must be unique within its
    /// kind; the same instance may be registered under several ids (or kinds) and is driven once.
    /// </summary>
    public IndexEngines(
        IEnumerable<(Guid Id, IValueIndexEngine Engine)>? valueEngines = null,
        IEnumerable<(Guid Id, ITextIndexEngine Engine)>? textEngines = null,
        IEnumerable<(Guid Id, ISemanticIndexEngine Engine)>? vectorEngines = null) {
        var list = new List<IIndexEngine>(2);
        if (valueEngines != null) {
            foreach (var (id, engine) in valueEngines) {
                add(_value, id, engine);
                if (!list.Contains(engine)) list.Add(engine);
            }
        }
        if (textEngines != null) {
            foreach (var (id, engine) in textEngines) {
                add(_text, id, engine);
                if (!list.Contains(engine)) list.Add(engine);
            }
        }
        _distinct = [.. list];
        var vectors = new List<ISemanticIndexEngine>(1);
        if (vectorEngines != null) {
            foreach (var (id, engine) in vectorEngines) {
                add(_vector, id, engine);
                if (!vectors.Contains(engine)) vectors.Add(engine);
            }
        }
        _distinctVector = [.. vectors];
        static void add<T>(Dictionary<Guid, T> map, Guid id, T engine) where T : class {
            if (id == Guid.Empty) throw new ArgumentException("An index engine cannot be registered under Guid.Empty; that id means the memory index. ");
            if (engine == null) throw new ArgumentNullException(nameof(engine), "Engine " + id + " is null. ");
            if (!map.TryAdd(id, engine)) throw new ArgumentException("Index engine id " + id + " is registered twice. ");
        }
    }
    /// <summary>Convenience for the common case of at most one engine per kind.</summary>
    public static IndexEngines Single(Guid valueId = default, IValueIndexEngine? value = null,
        Guid textId = default, ITextIndexEngine? text = null,
        Guid vectorId = default, ISemanticIndexEngine? vector = null) {
        return new IndexEngines(
            value == null ? null : [(valueId, value)],
            text == null ? null : [(textId, text)],
            vector == null ? null : [(vectorId, vector)]);
    }

    /// <summary>True when at least one persisted engine is configured (semantic included); with
    /// none, every lifecycle call is a no-op. Gates the durable-flush and replay-checkpoint work.</summary>
    public bool Any => _distinct.Length > 0 || _distinctVector.Length > 0;

    // ---- lookup by settings id ----------------------------------------------------------------
    /// <summary>The value engine registered under <paramref name="id"/>; null for <see cref="Guid.Empty"/>,
    /// the memory index. Any other id must have been registered: the settings were validated against
    /// their own lists, so a miss here means the engine set was built from different settings.</summary>
    public IValueIndexEngine? ValueEngine(Guid id) => find(_value, id, "value");
    public ITextIndexEngine? TextEngine(Guid id) => find(_text, id, "text");
    public ISemanticIndexEngine? VectorEngine(Guid id) => find(_vector, id, "vector");
    static T? find<T>(Dictionary<Guid, T> map, Guid id, string kind) where T : class {
        if (id == Guid.Empty) return null;
        if (map.TryGetValue(id, out var engine)) return engine;
        throw new Exception("The " + kind + " index engine " + id + " was not created for this store. Created: "
            + (map.Count == 0 ? "none" : string.Join(", ", map.Keys)) + ". ");
    }
    public IEnumerable<IIndexEngine> TransactionalEngines => _distinct;
    public IEnumerable<ISemanticIndexEngine> VectorEngines => _distinctVector;

    // ---- transaction protocol ------------------------------------------------------------------
    public void BeginTransaction() { foreach (var e in _distinct) e.BeginTransaction(); }
    public void CommitTransaction(long timestamp) { foreach (var e in _distinct) e.CommitTransaction(timestamp); }
    public void RollbackTransaction() { foreach (var e in _distinct) e.RollbackTransaction(); }
    public void CleanUpOnUnknownTransactionError() { foreach (var e in _distinct) e.CleanUpOnUnknownTransactionError(); }
    /// <summary>Durably persists everything committed so far; called right after a successful WAL
    /// flush. <paramref name="lastDurableLogTimestamp"/> is the newest timestamp the durable log
    /// covers — the transactional engines persist the position they recorded at commit, while the
    /// semantic indexes (driven through the index protocol, not transactions) are stamped here.</summary>
    public void MakeDurable(long lastDurableLogTimestamp) {
        foreach (var e in _distinct) e.MakeDurable();
        foreach (var v in _distinctVector) v.MakeDurable(lastDurableLogTimestamp);
    }

    // ---- startup -------------------------------------------------------------------------------
    /// <summary>
    /// Binds every engine to the log file at startup: a fresh engine (no WAL id yet) adopts the
    /// log's id; an engine bound to a different log file holds data that does not belong to this
    /// log and is reset, so the replay that follows rebuilds it from scratch. After the reset the
    /// engine adopts the log's id, so the rebuild happens once — not on every startup. (A crash in
    /// between leaves the old id with empty data, which simply resets again: idempotent.)
    /// </summary>
    public void BindToWalFile(Guid walFileId, Action<string> logInfo) {
        foreach (var e in _distinct) {
            if (e.GetWalFileId() == Guid.Empty) {
                e.SetWalFileId(walFileId);
                logInfo(" - " + e.Name + " initialized with log file id.");
            } else if (e.GetWalFileId() != walFileId) {
                e.ResetAll();
                e.SetWalFileId(walFileId); // adopt the log the engine is about to be rebuilt against
                logInfo(" - " + e.Name + " reset, log file id different.");
            }
        }
    }
    /// <summary>
    /// Mid-replay checkpoint: commits, makes durable and reopens the replay transaction of every
    /// engine that is behind <paramref name="timestamp"/>, bounding the amount of replay work a
    /// crash during startup could lose. Engines already at or past the position are left alone
    /// (committing would regress their timestamp).
    /// </summary>
    public void CheckpointDuringReplay(long timestamp) {
        foreach (var e in _distinct) {
            if (e.GetTimestamp() < timestamp) {
                e.CommitTransaction(timestamp);
                e.MakeDurable();
                e.BeginTransaction();
            }
        }
        // semantic indexes take no part in transactions; a durable flush checkpoints them the same
        // way (each index guards against regressing, so an up-to-date index is a no-op)
        foreach (var v in _distinctVector) v.MakeDurable(timestamp);
    }
    /// <summary>The engine whose durable timestamp is newer than the newest timestamp the durable
    /// log contains — evidence of acknowledged writes lost from the log — or null when none is.</summary>
    public IIndexEngine? FindEngineAheadOfLog(long lastLogTimestamp) {
        foreach (var e in _distinct) {
            if (e.GetTimestamp() > lastLogTimestamp) return e;
        }
        return null;
    }
    public void SetWalFileIdAndTimestamp(long timestamp, Guid walFileId) {
        foreach (var e in _distinct) e.SetWalFileIdAndTimestamp(timestamp, walFileId);
    }
    /// <summary>
    /// Resets every transactional engine whose durable position is newer than
    /// <paramref name="timestamp"/>, so the replay after a log truncation rebuilds it instead of
    /// leaving phantom transactions in it. Meant to be called on a freshly created engine set (no
    /// indexes opened, nothing published in memory), where <see cref="IIndexEngine.GetTimestamp"/>
    /// is the durable position by construction. Engines whose fresh instance cannot know its
    /// position before its indexes are opened (e.g. Lucene reports 0) are simply not reset here —
    /// the startup divergence check catches them and forces the full rebuild instead. Semantic
    /// engines carry no engine-level timestamp and are covered by the same startup check.
    /// Returns the names of the engines that were reset.
    /// </summary>
    public string[] ResetEnginesAhead(long timestamp, Action<string>? logInfo = null) {
        List<string> reset = [];
        foreach (var e in _distinct) {
            if (e.GetTimestamp() > timestamp) {
                e.ResetAll();
                reset.Add(e.Name);
                logInfo?.Invoke("Index engine \"" + e.Name + "\" held transactions newer than the revert point and was reset; it will be rebuilt from the log. ");
            }
        }
        return [.. reset];
    }

    // ---- maintenance ---------------------------------------------------------------------------
    public void DeleteUnopenedIndexes() {
        foreach (var e in _distinct) e.DeleteUnopenedIndexes();
        foreach (var v in _distinctVector) v.DeleteUnopenedIndexes();
    }
    public void ResetAll() {
        foreach (var e in _distinct) e.ResetAll();
        foreach (var v in _distinctVector) v.ResetAll();
    }
    public void OptimizeDisk() { foreach (var e in _distinct) e.OptimizeDisk(); }
    public long GetTotalDiskSpace() { return _distinct.Sum(e => e.GetTotalDiskSpace()) + _distinctVector.Sum(v => v.GetTotalDiskSpace()); }
    // derived query caches (facet-set sidecar) are a value-engine concern, see IValueIndexEngine
    public void SaveIndexCaches(bool force) { foreach (var e in _distinct) if (e is IValueIndexEngine v) v.SaveIndexCaches(force); }
    public void ResetIndexCaches() { foreach (var e in _distinct) if (e is IValueIndexEngine v) v.ResetIndexCaches(); }
    public void Dispose() {
        foreach (var e in _distinct) {
            try { e.Dispose(); } catch { }
        }
        foreach (var v in _distinctVector) {
            try { v.Dispose(); } catch { }
        }
    }
}
