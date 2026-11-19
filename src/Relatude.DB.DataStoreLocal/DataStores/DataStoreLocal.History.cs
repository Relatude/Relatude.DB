using Relatude.DB.DataStores.History;
using Relatude.DB.IO;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;

namespace Relatude.DB.DataStores;

// The change history: a time index of the log the history reader keeps in the background (see LogHistory). The index
// holds where each change is and what sort it was, never a value; lists and counts are read from it, names and
// fields from the log. Everything but the status is an administrator's.
public sealed partial class DataStoreLocal : IDataStore {
    LogHistory? _history;
    internal LogHistory History => _history ??= new LogHistory(this);
    internal IIOProvider IOLog2 => _ioLog2;
    /// <summary>True while a log rewrite, a copy (a backup) or a revert works on the log files: the history reader waits.</summary>
    internal bool IsBusyWithLogFiles {
        get {
            lock (_isRewritingOrCopyingLock) if (_isRewritingOrCopying) return true;
            return _reverting;
        }
    }
    volatile bool _reverting;

    /// <summary>The node an internal id is now, and its type, for the history reader: relation actions name nodes by
    /// internal ids. Takes the store's read lock for a moment, and gives up rather than wait long.</summary>
    internal bool TryResolveNodeForHistory(int id, out Guid guid, out Guid type) {
        guid = Guid.Empty;
        type = Guid.Empty;
        if (!_lock.TryEnterReadLock(250)) return false;
        try {
            if (_state != DataStoreState.Open) return false;
            return _guids.TryGetId(id, out guid) && _definition.TryGetTypeOfNode(id, out type);
        } finally {
            _lock.ExitReadLock();
        }
    }

    /// <summary>The log file a source of the history is: the one in use through the log's own streams, any other opened for each read.</summary>
    IHistoryLogFile? historyFileOf(HistorySourceState source) {
        var wal = _wal;
        if (source.Secondary) {
            if (wal.HasSecondaryStream) return new ActiveLogFile(() => _wal, true);
            var key2 = FileKeyUtility.WAL_GetSecondaryFileKey();
            return _ioLog2.Exists(key2) ? new ClosedLogFile(_ioLog2, key2) : null;
        }
        var key = source.Key.SplitKey();
        if (key.IsSameKey(wal.FileKey)) return new ActiveLogFile(() => _wal, false);
        return _io.Exists(key) ? new ClosedLogFile(_io, key) : null;
    }

    void requireHistoryAdmin(QueryContext? ctx) {
        _lock.EnterReadLock();
        try {
            if (!writerOf(ctx ?? _defaultQueryCtx).IsAdmin) throw new UnauthorizedAccessException("The change history is for administrators. ");
        } finally {
            _lock.ExitReadLock();
        }
    }

    public LogHistoryStatus GetLogHistoryStatus() => History.Status();
    public LogChangePage ReadLogChanges(LogChangeQuery query, QueryContext? ctx = null) {
        requireHistoryAdmin(ctx);
        return History.Read(query, historyFileOf);
    }
    public LogActivity AnalyseLogChanges(LogActivityQuery query, QueryContext? ctx = null) {
        requireHistoryAdmin(ctx);
        return History.Analyse(query);
    }
    public NodeVersionData? FindVersionAt(Guid nodeId, long timestamp, QueryContext? ctx = null, Guid? revisionId = null) {
        requireHistoryAdmin(ctx);
        var found = History.VersionAt(nodeId, timestamp, historyFileOf);
        if (found == null) return null;
        if (revisionId is { } wanted) {
            INodeDataExternal? revision = found.Value.node switch {
                NodeDataRevisions revisions => revisions.Revisions.FirstOrDefault(r => r.RevisionId == wanted),
                INodeDataExternal plain => plain,
                _ => null,
            };
            return revision == null ? null : new NodeVersionData { Source = found.Value.source, Timestamp = timestamp, Node = revision };
        }
        _lock.EnterReadLock();
        try {
            return new NodeVersionData { Source = found.Value.source, Timestamp = timestamp, Node = ToOuter(found.Value.node, ctx) };
        } finally {
            _lock.ExitReadLock();
        }
    }
    public void RebuildLogHistory(QueryContext? ctx = null) {
        requireHistoryAdmin(ctx);
        History.Rebuild();
    }
    /// <summary>
    /// Reads what the log gained since the history reader's last run, now, on the calling thread, with the queued writes
    /// flushed to the log first: for tests and the rebuild job. Waits for a run under way. Does nothing while history is off.
    /// </summary>
    public void UpdateLogHistoryNow() {
        validateDatabaseState();
        if (_wal.GetQueueActionCount() > 0) FlushToDisk(false, 0);
        History.UpdateNow();
    }
}
