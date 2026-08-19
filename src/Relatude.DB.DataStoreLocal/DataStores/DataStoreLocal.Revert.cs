using System.Diagnostics;
using Relatude.DB.Common;
using Relatude.DB.DataStores.Stores;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores;

// Reverting: rolling the database back to an earlier log timestamp by truncating the log tail and
// reloading. Rollback deliberately reuses the crash-recovery path (dispose the log-derived
// components, reopen, replay), so the destructive part of the work is only the file truncation.
//
// The revert window is the cheap, planned form. While a window is active the store suspends
// everything that would persist state past the window start: Engines.MakeDurable (gated in
// FlushToDisk), state snapshots (gated in SaveIndexStates), persisted index caches (gated in
// SaveIndexCaches) and hot-swap log rewrites (gated in rewriteStore). The deferring engines
// (Lucene, the disk text index, the native KV store, the semantic indexes) then never persist
// anything past the window start, and their Dispose discards unpersisted data by design, so a
// rollback reopens them exactly at the window start with no rebuild. The exception is an engine
// that is durable per transaction (the SQLite index engine commits every transaction): it advances
// regardless of the gates and is reset and rebuilt from the log on rollback.
//
// DeleteTransactionsAfter is the general form against any timestamp: correct on any store, but
// whatever persisted state has advanced past the timestamp — the state snapshot, memory index
// files, index engines — is reset and rebuilt from the truncated log, which can mean a full replay.
public sealed partial class DataStoreLocal : IDataStore {

    RevertWindowInfo? _revertWindow;
    // read by the gates in FlushToDisk/SaveIndexStates/SaveIndexCaches/rewriteStore; written only
    // under the write lock, and re-checked under it where a stale read could persist state past
    // the window start
    internal bool RevertWindowIsActive => _revertWindow != null;

    public RevertWindowInfo? RevertWindow {
        get {
            _lock.EnterReadLock();
            try {
                return _revertWindow;
            } finally {
                _lock.ExitReadLock();
            }
        }
    }

    public long BeginRevertWindow(bool saveStateFirst = true) {
        lock (_isRewritingOrCopyingLock) {
            if (_isRewritingOrCopying) throw new Exception("Cannot begin a revert window while a store rewrite or copy is in progress. ");
        }
        validateDatabaseState();
        // Writing the state snapshot first makes a later rollback reload from the snapshot instead
        // of replaying the log up to the window start. Done before the lock below, like every other
        // caller of SaveIndexStates, to keep the time under the write lock short.
        if (saveStateFirst) SaveIndexStates();
        _lock.EnterWriteLock();
        try {
            validateDatabaseState();
            if (_revertWindow != null) throw new Exception("A revert window is already active (begun UTC "
                + _revertWindow.BegunUtc.ToString("o") + " at timestamp " + _revertWindow.Timestamp
                + "). Commit or roll it back first. ");
            // everything queued must be in the log file, and the engines durable at the window
            // start, before the position is recorded (the window is not active yet, so the flush
            // still runs Engines.MakeDurable)
            FlushToDisk(true, 0);
            _revertWindow = new RevertWindowInfo {
                Timestamp = _wal.LastTimestamp,
                BegunUtc = DateTime.UtcNow,
                LogPosition = _wal.GetPositionAfterLastTransaction(),
                LogFileId = _wal.FileId,
            };
            LogInfo("Revert window begun at UTC " + new DateTime(_revertWindow.Timestamp, DateTimeKind.Utc).ToString("o")
                + " (timestamp " + _revertWindow.Timestamp + "). "
                + "Engine durability, state snapshots and log rewrites are suspended until the window is committed or rolled back. ");
            return _revertWindow.Timestamp;
        } finally {
            _lock.ExitWriteLock();
        }
    }

    public void CommitRevertWindow() {
        _lock.EnterWriteLock();
        try {
            validateDatabaseState();
            if (_revertWindow == null) throw new Exception("No revert window is active. ");
            LogInfo("Revert window begun UTC " + _revertWindow.BegunUtc.ToString("o") + " committed, keeping all changes. ");
            _revertWindow = null;
        } finally {
            _lock.ExitWriteLock();
        }
        FlushToDisk(true, 0); // engines become durable at the current head again
    }

    public DeleteTransactionsResult RollbackRevertWindow() {
        lock (_isRewritingOrCopyingLock) {
            if (_isRewritingOrCopying) throw new Exception("Cannot roll back while a store rewrite or copy is in progress. ");
        }
        _lock.EnterWriteLock();
        try {
            validateDatabaseState();
            var w = _revertWindow ?? throw new Exception("No revert window is active. ");
            if (w.LogFileId != _wal.FileId) throw new Exception("The log file changed after the revert window was begun. Rollback is not possible. ");
            var result = deleteTransactionsAfterCore(w.Timestamp, w.LogPosition, dryRun: false);
            _revertWindow = null; // already null after a real rollback; also ends the window when there was nothing to delete
            return result;
        } finally {
            _lock.ExitWriteLock();
        }
    }

    public DeleteTransactionsResult DeleteTransactionsAfter(long afterTimestamp, bool dryRun = false) {
        lock (_isRewritingOrCopyingLock) {
            if (_isRewritingOrCopying) throw new Exception("Cannot delete transactions while a store rewrite or copy is in progress. ");
        }
        _lock.EnterWriteLock();
        try {
            validateDatabaseState();
            if (_revertWindow != null && !dryRun) throw new Exception("A revert window is active. Use RollbackRevertWindow or CommitRevertWindow to end it first. ");
            return deleteTransactionsAfterCore(afterTimestamp, null, dryRun);
        } finally {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// The shared implementation. Caller holds the write lock and has validated the store state.
    /// <paramref name="knownPositionAfterLastKept"/> is the recorded revert-window position (the
    /// scan then only counts the doomed tail); without it the log is scanned from the newest known
    /// position at or before the target to find the truncation point.
    /// </summary>
    DeleteTransactionsResult deleteTransactionsAfterCore(long afterTimestamp, long? knownPositionAfterLastKept, bool dryRun) {
        var sw = Stopwatch.StartNew();
        var activityId = RegisterActvity(DataStoreActivityCategory.Rewriting, dryRun ? "Scanning log for revert" : "Reverting database", 0);
        try {
            if (afterTimestamp <= 0) throw new Exception("Invalid timestamp. Take it from GetLastTimestampID() before making the changes to delete. ");
            // everything queued must be in the log file so the scan and the truncation see it all:
            _wal.DequeuAllTransactionWritesAndFlushStreamsThreadSafe(true);
            var lastTimestamp = _wal.LastTimestamp;
            if (afterTimestamp >= lastTimestamp) { // nothing after the timestamp
                return new DeleteTransactionsResult {
                    DryRun = dryRun, AfterTimestamp = afterTimestamp, LastTimestamp = lastTimestamp,
                    TransactionsDeleted = 0, ActionsDeleted = 0, BytesTruncated = 0, DurationMs = sw.Elapsed.TotalMilliseconds,
                };
            }
            var firstTimestamp = _wal.FirstTimestamp;
            if (firstTimestamp > 0 && afterTimestamp < firstTimestamp)
                throw new Exception("The timestamp lies before the first transaction in the log (UTC "
                    + new DateTime(firstTimestamp, DateTimeKind.Utc) + "). Deleting every transaction is not supported. ");

            var walFileKey = _wal.FileKey;
            var walFileSize = _wal.FileSize;
            var stateHeaderOk = tryReadStateFileHeader(out var stateTimestamp, out var statePosition);
            var stateFileExists = IOIndex.ExistsAndIsNotEmpty(_fileKeys.StateFileKey);

            // find where to truncate, and count what falls off. The log streams must be closed
            // while an independent reader scans the file (and stay closed for the truncation):
            long keepEnd = -1;
            long lastKeptTimestamp = 0;
            int deletedTransactions = 0;
            int deletedActions = 0;
            _wal.Close();
            var reopened = false;
            try {
                long scanFrom = 0, scanFromTimestamp = 0;
                if (knownPositionAfterLastKept.HasValue) {
                    if (knownPositionAfterLastKept.Value > walFileSize) throw new Exception("The recorded revert position lies outside the log file. ");
                    keepEnd = scanFrom = knownPositionAfterLastKept.Value;
                    lastKeptTimestamp = scanFromTimestamp = afterTimestamp;
                } else if (stateHeaderOk && stateTimestamp <= afterTimestamp && statePosition <= walFileSize) {
                    // the snapshot position is a transaction boundary at or before the target: skip the log before it
                    keepEnd = scanFrom = statePosition;
                    lastKeptTimestamp = scanFromTimestamp = stateTimestamp;
                }
                using (var reader = new LogReader(walFileKey, _definition, _io, scanFrom, scanFromTimestamp)) {
                    if (keepEnd < 0) keepEnd = reader.Position; // right after the file header
                    var pastTarget = false;
                    while (reader.ReadNextTransaction(out var transaction, false, logError, out _)) {
                        if (!pastTarget && transaction.Timestamp <= afterTimestamp) {
                            keepEnd = reader.Position; // position right after this transaction
                            lastKeptTimestamp = transaction.Timestamp;
                        } else {
                            // timestamps are monotonic in the log; everything from the first
                            // too-new transaction on is deleted, so the kept range stays contiguous
                            pastTarget = true;
                            deletedTransactions++;
                            deletedActions += transaction.ExecutedActions.Count;
                        }
                    }
                }
                if (dryRun || deletedTransactions == 0) {
                    _wal.OpenForAppending();
                    reopened = true;
                    return new DeleteTransactionsResult {
                        DryRun = dryRun, AfterTimestamp = afterTimestamp,
                        LastTimestamp = deletedTransactions == 0 ? lastTimestamp : lastKeptTimestamp,
                        TransactionsDeleted = deletedTransactions, ActionsDeleted = deletedActions,
                        BytesTruncated = walFileSize - keepEnd, DurationMs = sw.Elapsed.TotalMilliseconds,
                    };
                }
            } catch when (reopenWalOnError(ref reopened)) {
                throw; // filter reopens the log streams so a failed scan leaves the store usable; never matches
            }

            // the destructive part: from here on the store is rebuilt around the truncated log
            LogInfo("Reverting database to UTC " + new DateTime(afterTimestamp, DateTimeKind.Utc).ToString("o")
                + ": deleting " + deletedTransactions.To1000N() + " transaction" + (deletedTransactions != 1 ? "s" : "")
                + " with " + deletedActions.To1000N() + " action" + (deletedActions != 1 ? "s" : "")
                + " (" + (walFileSize - keepEnd).ToByteString() + " of log). ");
            _revertWindow = null; // the gates must be off for the reload below
            var fullReset = stateFileExists && (!stateHeaderOk || stateTimestamp > afterTimestamp);
            string[] enginesReset = [];
            try {
                UpdateActivity(activityId, "Reverting database", 10);
                _scheduler.Stop();
                if (fullReset) {
                    // the state snapshot is newer than the revert point and cannot be used; delete
                    // it together with the index files and reset the engines, so the reload below
                    // rebuilds everything from the truncated log
                    LogInfo("The state snapshot is newer than the revert point; state and indexes will be rebuilt from the log. ");
                    resetStateAndIndexes();
                }
                _state = DataStoreState.Closed;
                // same recreate-set as the recovery path in Open(): only the components
                // initialize() rebuilds. Engines.Dispose discards unpersisted data by design, so
                // the deferring engines reopen at their last durable position:
                try { _index?.Dispose(); } catch { }
                try { _wal?.Dispose(); } catch { }
                try { Engines.Dispose(); } catch { }
                UpdateActivity(activityId, "Truncating log", 20);
                truncateWalFiles(walFileKey, keepEnd, walFileSize);
                if (!fullReset && _createIndexEngines != null) {
                    // a freshly created engine reports its durable position (nothing published in
                    // memory yet): reset the ones that persisted past the revert point (e.g. the
                    // SQLite engine, which is durable per transaction), so the reload rebuilds them
                    // instead of tripping the divergence check and rebuilding everything
                    var probe = _createIndexEngines();
                    try {
                        enginesReset = probe.ResetEnginesAhead(afterTimestamp, msg => LogInfo(msg));
                    } finally {
                        probe.Dispose();
                    }
                }
                UpdateActivity(activityId, "Reloading", 30);
                initialize();
                Open(throwOnBadLogFile: false, throwOnBadStateFile: false);
            } catch (Exception err) {
                throw createCriticalErrorAndSetDbToErrorState("Revert failed. Database left in unknown state. ", err);
            }
            var newHead = _wal!.LastTimestamp; // reassigned by initialize() above
            LogInfo("Revert completed in " + sw.ElapsedMilliseconds.To1000N() + "ms. Head is now UTC "
                + new DateTime(newHead, DateTimeKind.Utc).ToString("o") + " (timestamp " + newHead + "). ");
            return new DeleteTransactionsResult {
                DryRun = false, AfterTimestamp = afterTimestamp, LastTimestamp = newHead,
                TransactionsDeleted = deletedTransactions, ActionsDeleted = deletedActions,
                BytesTruncated = walFileSize - keepEnd,
                StateAndIndexesReset = fullReset, EnginesReset = enginesReset,
                DurationMs = sw.Elapsed.TotalMilliseconds,
            };
        } finally {
            DeRegisterActivity(activityId);
        }
    }

    // exception-filter helper: reopen the log streams after a failed scan, then let the exception
    // continue (the filter always returns false, so the catch body never runs)
    bool reopenWalOnError(ref bool reopened) {
        if (!reopened) {
            try { _wal.OpenForAppending(); reopened = true; } catch { /* the original error is the one to surface */ }
        }
        return false;
    }

    /// <summary>Truncates the primary log file, keeping the secondary in sync: a secondary that is
    /// byte-identical (same length) is truncated at the same position, anything else is deleted so
    /// the next open recreates it as a copy of the truncated primary. Log streams must be closed.</summary>
    void truncateWalFiles(string walFileKey, long keepEnd, long preTruncateSize) {
        _io.TruncateFile(walFileKey, keepEnd);
        if (!_settings.SecondaryBackupLog) return;
        var secondaryKey = _fileKeys.WAL_GetSecondaryFileKey();
        var secondarySize = _ioLog2.GetFileSizeOrZeroIfUnknown(secondaryKey);
        if (secondarySize == 0) return;
        if (secondarySize == preTruncateSize) {
            _ioLog2.TruncateFile(secondaryKey, keepEnd);
        } else {
            LogInfo("Secondary log file is out of sync with the primary; it will be recreated from the truncated primary. ");
            _ioLog2.DeleteFileIfItExists(secondaryKey);
        }
    }

    /// <summary>Reads only the fixed header of the state snapshot: its log timestamp and the log
    /// position right after the last transaction it covers. False when there is no usable header.</summary>
    bool tryReadStateFileHeader(out long timestamp, out long positionAfterLastTransaction) {
        timestamp = 0;
        positionAfterLastTransaction = 0;
        try {
            if (IOIndex.DoesNotExistOrIsEmpty(_fileKeys.StateFileKey)) return false;
            using var stream = IOIndex.OpenRead(_fileKeys.StateFileKey, 0);
            var version = stream.ReadVerifiedInt();
            if (version != _stateFileVersion) return false;
            timestamp = stream.ReadVerifiedLong();
            positionAfterLastTransaction = stream.ReadVerifiedLong();
            return true;
        } catch {
            timestamp = 0;
            positionAfterLastTransaction = 0;
            return false;
        }
    }

    /// <summary>Closing or disposing the store ends an active revert window as a commit: the
    /// changes made inside it stay, and the flush that follows makes the engines durable again.</summary>
    void endRevertWindowAsCommitIfActive() {
        if (_revertWindow == null) return;
        LogInfo("Revert window begun UTC " + _revertWindow.BegunUtc.ToString("o") + " ended by store shutdown; keeping all changes. ");
        _revertWindow = null;
    }
}
