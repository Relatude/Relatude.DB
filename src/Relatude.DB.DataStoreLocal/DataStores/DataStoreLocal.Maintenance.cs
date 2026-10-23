using Relatude.DB.Common;
using Relatude.DB.DataStores.Stores;
using Relatude.DB.IO;
using Relatude.DB.Tasks;
using System.Diagnostics;
namespace Relatude.DB.DataStores;

public sealed partial class DataStoreLocal : IDataStore {
    // read without a lock: a background check asking for the write lock just to read a counter would
    // queue behind a long query and hold up every reader after it
    public long GetNoPrimitiveActionsInLogThatCanBeTruncated() {
        if (_state != DataStoreState.Open) return 0;
        return Interlocked.Read(ref _noPrimitiveActionsInLogThatCanBeTruncated);
    }
    public long GetNoPrimitiveActionsInLog() {
        if (_state != DataStoreState.Open) return 0;
        return Interlocked.Read(ref _noPrimitiveActionsInLog);
    }
    internal int FlushToDisk(bool deepFlush, long parentActivityId) {
        FlushToDisk(deepFlush, parentActivityId, out int transactionCount, out _, out _);
        return transactionCount;
    }
    internal void FlushToDisk(bool deepFlush, long parentActivityId, out int transactionCount, out int actionCount, out long bytesWritten)
        => FlushToDisk(deepFlush, parentActivityId, false, out transactionCount, out actionCount, out bytesWritten);
    // The log timestamp the engines were last made durable at. long.MinValue when that is not known, or when
    // something other than a transaction changed what the engines hold (see markEnginesChanged): the next
    // flush then makes them durable whatever the timestamp says.
    long _enginesDurableAt = long.MinValue;
    /// <summary>For changes to the engines that no transaction timestamp records: the next flush makes them durable.</summary>
    void markEnginesChanged() => Interlocked.Exchange(ref _enginesDurableAt, long.MinValue);
    // whether the write-locked part of a flush has anything to do: positions to drain into an engine-backed
    // state store, or engines that are behind the log (and may be made durable: not during a revert window)
    bool enginesNeedFlush() {
        if (!Engines.Any) return false;
        if (_nodes.HasPendingSegments) return true;
        return !RevertWindowIsActive && Interlocked.Read(ref _enginesDurableAt) != _wal.LastTimestamp;
    }
    // When a background flush first found the write lock taken, in Environment.TickCount64; 0 when it has not.
    // The scheduler's flushes run with background set: the timer's, and the one before a background snapshot.
    long _backgroundFlushLockMissedSince;
    /// <summary>
    /// <paramref name="background"/>: the scheduler's flush. Waiting for the write lock would put it in the
    /// queue behind whatever query holds the read lock, and every reader arriving after it would wait too,
    /// so it only takes the lock when it is free and otherwise leaves the engines to the next tick - unless
    /// they have waited longer than MaxDelayAutoDiskFlushIfBusyInSeconds, when it waits like any writer.
    /// The log itself is written and flushed either way: that part takes no store lock.
    /// </summary>
    internal void FlushToDisk(bool deepFlush, long parentActivityId, bool background, out int transactionCount, out int actionCount, out long bytesWritten) {
        var activityId = RegisterActvity(parentActivityId, DataStoreActivityCategory.Flushing, "Flushing to disk");
        validateDatabaseState();
        try {
            Action<string, int> progress = (txt, prg) => UpdateActivity(activityId, txt, prg);
            // Everything queued is written without the store lock, so readers and writers keep going.
            // A writer that keeps committing meanwhile refills the queue; a few more passes drain
            // that too, so the pass that must run under the write lock below only covers what was
            // committed during the last, short pass instead of a whole bulk transaction.
            _wal.DequeuAllTransactionWritesAndFlushStreamsThreadSafe(deepFlush, progress, out transactionCount, out actionCount, out bytesWritten);
            for (var pass = 1; pass < 4 && _wal.HasQueuedWrites; pass++) {
                _wal.DequeuAllTransactionWritesAndFlushStreamsThreadSafe(deepFlush, progress, out var t, out var a, out var b);
                transactionCount += t; actionCount += a; bytesWritten += b;
            }
            TaskQueuePersisted?.FlushDisk();
            if (enginesNeedFlush() && enterWriteLockForFlush(background)) {
                try {
                    // the engines may only be made durable up to a point the log file has reached, and no
                    // transaction commits while the write lock is held, so the remainder is written first
                    _wal.DequeuAllTransactionWritesAndFlushStreamsThreadSafe(deepFlush, null, out var t, out var a, out var b);
                    transactionCount += t; actionCount += a; bytesWritten += b;
                    if (_stateStore.Engine is { } stateEngine && _nodes.HasPendingSegments) { // the positions the flush confirmed, written by the single writer
                        stateEngine.BeginTransaction();
                        _nodes.DrainPendingSegments();
                        stateEngine.CommitTransaction(_wal.LastTimestamp);
                    }
                    if (_revertWindow == null) {
                        var durableAt = _wal.LastTimestamp;
                        Engines.MakeDurable(durableAt);
                        Interlocked.Exchange(ref _enginesDurableAt, durableAt);
                    }
                } finally {
                    _lock.ExitWriteLock();
                }
            }
        } catch (Exception err) {
            _state = DataStoreState.Error;
            throw createCriticalErrorAndSetDbToErrorState("Critical error. Database left in unknown state. Restart required. ", err);
        } finally {
            DeRegisterActivity(activityId);
        }
    }
    bool enterWriteLockForFlush(bool background) {
        if (!background) {
            _lock.EnterWriteLock();
            return true;
        }
        if (_lock.TryEnterWriteLock(0)) {
            Interlocked.Exchange(ref _backgroundFlushLockMissedSince, 0);
            return true;
        }
        var now = Environment.TickCount64;
        var since = Interlocked.CompareExchange(ref _backgroundFlushLockMissedSince, now, 0);
        if (since == 0) return false; // the first miss: noted, tried again on the next tick
        if (now - since < _settings.MaxDelayAutoDiskFlushIfBusyInSeconds * 1000) return false;
        _lock.EnterWriteLock(); // overdue: the engines are not left behind the log indefinitely
        Interlocked.Exchange(ref _backgroundFlushLockMissedSince, 0);
        return true;
    }
    /// <summary>
    /// For housekeeping the scheduler runs: the write lock if it is free right now, else nothing. A
    /// background job that queued for the lock behind a long query would hold up every reader arriving after
    /// it; skipping a round costs nothing. <paramref name="mustRun"/> waits like any other writer, for a job
    /// that has been put off long enough.
    /// </summary>
    bool tryEnterWriteLockForHousekeeping(bool mustRun) {
        if (_lock.TryEnterWriteLock(0)) return true;
        if (!mustRun) return false;
        _lock.EnterWriteLock();
        return true;
    }
    public void CopyStore(string[] newLogFileKey, IIOProvider? destinationIO = null) {
        lock (_isRewritingOrCopyingLock) {
            if (_isRewritingOrCopying) throw new Exception("Store rewrite or copy already in progress. ");
            _isRewritingOrCopying = true;
            _fileKeyBeingWritten = newLogFileKey;
        }
        var activityId = RegisterActvity(DataStoreActivityCategory.Copying, "Copying log file");
        try { // all fallible work inside try/finally so the flag and activity are always reset
            FlushToDisk(true, activityId);
            _lock.EnterWriteLock();
            try {
                _wal.Copy(newLogFileKey, destinationIO);
            } catch (LogCopyException err) {
                // a backup storage that failed fails the backup, not the database: its log file is open again
                logError("Failed to copy log file. ", err);
                throw;
            } catch (Exception err) {
                throw createCriticalErrorAndSetDbToErrorState("Failed to copy log file. ", err);
            } finally {
                _lock.ExitWriteLock();
            }
        } finally {
            DeRegisterActivity(activityId);
            lock (_isRewritingOrCopyingLock) {
                _isRewritingOrCopying = false;
                _fileKeyBeingWritten = null;
            }
        }
    }
    public void TruncateIndexes() {
        var activityId = RegisterActvity(DataStoreActivityCategory.Copying, "Truncate indexes");
        try { // outer try so a flush failure before the lock still deregisters the activity
            FlushToDisk(true, activityId); // ensuring all writes are flushed before entering lock
            _lock.EnterWriteLock();
            try {
                FlushToDisk(true, activityId);
                validateDatabaseState();
                Engines.OptimizeDisk();
                markEnginesChanged();
            } catch (Exception err) {
                throw createCriticalErrorAndSetDbToErrorState("Failed to truncate indexes. ", err);
            } finally {
                _lock.ExitWriteLock();
            }
        } finally {
            DeRegisterActivity(activityId);
        }
    }
    public int DeleteOldLogs() {
        _lock.EnterWriteLock();
        var fileDeleted = 0;
        var activityId = RegisterActvity(DataStoreActivityCategory.Copying, "Deleting old logs");
        try {
            validateDatabaseState();
            foreach (var f in FileKeyUtility.WAL_GetAllFileKeys(_io)) {
                if (!_wal.FileKey.IsSameKey(f)) {
                    _io.DeleteFileIfItExists(f);
                    LogInfo($"Deleted old log file {f.AsKeyString()}. ");
                    fileDeleted++;
                }
            }
        } catch (Exception err) {
            throw new Exception("Failed to delete old logs. ", err);
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitWriteLock();
        }
        return fileDeleted;
    }
    object _saveStateLock = new();
    public void SaveIndexStates(bool forceRefresh = false, bool nodeSegmentsOnly = false) => saveIndexStates(forceRefresh, nodeSegmentsOnly, false, true);
    /// <summary>
    /// The scheduler's snapshot: written when the write lock is free, put off (false) while a query holds the
    /// read lock - unless <paramref name="mustRun"/>, for a snapshot that has been put off long enough.
    /// </summary>
    internal bool TrySaveIndexStatesInBackground(bool mustRun) => saveIndexStates(false, false, true, mustRun);
    bool saveIndexStates(bool forceRefresh, bool nodeSegmentsOnly, bool background, bool mustRun) {
        if (RevertWindowIsActive) { // cheap early skip; re-checked under the lock below
            LogInfo("Index state save skipped: a revert window is active. ");
            return true;
        }
        var activityId = RegisterActvity(DataStoreActivityCategory.SavingState, "Saving index states");
        try { // outer try so a flush failure before the lock still deregisters the activity
            FlushToDisk(true, activityId, background, out _, out _, out _); // ensuring all writes are flushed before locking, to minimize time spent locked
        } catch {
            DeRegisterActivity(activityId);
            throw;
        }
        lock (_saveStateLock) { // to avoid multiple simultaneous calls
            if (background) {
                if (!tryEnterWriteLockForHousekeeping(mustRun)) {
                    DeRegisterActivity(activityId);
                    return false;
                }
            } else {
                _lock.EnterWriteLock();
            }
            try {
                if (_revertWindow != null) { // a state snapshot must never cover transactions past the window start
                    LogInfo("Index state save skipped: a revert window is active. ");
                    return true;
                }
                FlushToDisk(true, activityId); // ensuring all writes are flushed after locking, should be quick since flushed before lock ( inside try so the write lock is released if it throws )
                validateDatabaseState();
                var anyOutOfSyncIndexes = _definition.GetAllIndexes().Where(i => i.PersistedTimestamp < _wal.LastTimestamp).Any();
                var newestStateFileKey = FileKeyUtility.State_GetNewestFileKey(IOIndex);
                if (newestStateFileKey == null || IOIndex.DoesNotExistOrIsEmpty(newestStateFileKey) || _noPrimitiveActionsSinceLastStateSnapshot > 0 || anyOutOfSyncIndexes || forceRefresh) {
                    var sw = Stopwatch.StartNew();
                    LogInfo("Initiating index state write.");
                    saveMainState(activityId); // requires WriteLock after flush due to node segments
                    if (!nodeSegmentsOnly) saveIndexesStates(activityId);
                    LogInfo("Index state write completed in " + sw.ElapsedMilliseconds.To1000N() + " ms.");
                } else {
                    LogInfo("Index state write skipped as file reflects latest changes. ");
                }
            } catch (Exception err) {
                throw createCriticalErrorAndSetDbToErrorState("Failed to save index states. ", err);
            } finally {
                _lock.ExitWriteLock();
                DeRegisterActivity(activityId);
            }
        }
        return true;
    }
    void resetStateAndIndexes() {
        var stateFileKeys = FileKeyUtility.State_GetAllFileKeys(IOIndex);
        var stateFileExisted = stateFileKeys.Any(k => IOIndex.ExistsAndIsNotEmpty(k));
        foreach (var k in stateFileKeys) IOIndex.DeleteFileIfItExists(k);
        var indexesFiles = FileKeyUtility.Index_GetAll(IOIndex);
        foreach (var i in indexesFiles) IOIndex.DeleteFileIfItExists(i);
        if (stateFileExisted) {
            _noPrimitiveActionsSinceLastStateSnapshot = Settings.AutoSaveIndexStatesActionCountUpperLimit + 1;
        }
        Engines.ResetAll();
        Engines.ResetIndexCaches();
        markEnginesChanged();
    }
    public void SaveIndexCaches(bool force) => saveIndexCaches(force, false);
    /// <summary>The scheduler's save of the index caches: only when the write lock is free (false when put off).</summary>
    internal bool TrySaveIndexCachesInBackground() => saveIndexCaches(false, true);
    bool saveIndexCaches(bool force, bool background) {
        if (background) {
            if (!tryEnterWriteLockForHousekeeping(false)) return false;
        } else {
            _lock.EnterWriteLock();
        }
        try {
            if (_revertWindow != null) return true; // persisted caches must not cover transactions past the window start
            if (State == DataStoreState.Open) {
                Engines.SaveIndexCaches(force);
                markEnginesChanged();
            }
        } finally {
            _lock.ExitWriteLock();
        }
        return true;
    }
    public void ResetIndexCaches() {
        _lock.EnterWriteLock();
        try {
            if (State == DataStoreState.Open) {
                Engines.ResetIndexCaches();
                markEnginesChanged();
            }
        } finally {
            _lock.ExitWriteLock();
        }
    }
    /// <summary>
    /// Queues a rewrite of the log down to the current state, unless a revert window is open or the log
    /// holds nothing to drop. Returns whether one was queued. <paramref name="groupKey"/> names its batch in
    /// the task queue, so a caller can follow it there until it is gone (a rewrite deletes its batch
    /// when it succeeds).
    /// </summary>
    public bool EnqueueLogTruncation(bool deleteOldLogs, string? groupKey = null) {
        if (RevertWindowIsActive) {
            LogInfo("Log truncation skipped: a revert window is active. ");
            return false;
        }
        if (_noPrimitiveActionsInLogThatCanBeTruncated <= 0) return false;
        var task = new RewriteTask() {
            HotSwapToNewFile = true,
            DeleteOldDbFilesAfterHotSwap = deleteOldLogs,
            NewLogFileKey = null,
            IO = _io,
            Truncate = true,
        };
        EnqueueTask(task, groupKey);
        return true;
    }
    public void Maintenance(MaintenanceAction a) {
        if (a.HasFlag(MaintenanceAction.TruncateLog)) EnqueueLogTruncation(a.HasFlag(MaintenanceAction.DeleteOldLogs));
        if (a.HasFlag(MaintenanceAction.TruncateIndexes)) TruncateIndexes();
        if (a.HasFlag(MaintenanceAction.DeleteOldLogs) && !a.HasFlag(MaintenanceAction.TruncateLog)) DeleteOldLogs();
        if (a.HasFlag(MaintenanceAction.SaveIndexStates)) SaveIndexStates();
        if ((a & _actionsUnderWriteLock) != 0) maintenanceUnderWriteLock(a, false);
        if (a.HasFlag(MaintenanceAction.FlushDisk)) FlushToDisk(true, 0);
        if(a.HasFlag(MaintenanceAction.UpdatePersistedCaches)) SaveIndexCaches(true);
        // caches gone: rebuild the mirror and facet warm state in the background, so the next
        // filtered facet query does not pay the cold rebuild inline
        if (a.HasFlag(MaintenanceAction.ClearCache) && State == DataStoreState.Open) warmIndexesInBackground();
    }
    // the actions below that run under the write lock; any other action, alone, takes no lock here
    const MaintenanceAction _actionsUnderWriteLock = MaintenanceAction.ResetSecondaryLogFile | MaintenanceAction.ClearAiCache | MaintenanceAction.ClearCache
        | MaintenanceAction.PurgeCache | MaintenanceAction.CompressMemory | MaintenanceAction.GarbageCollect | MaintenanceAction.ResetStateAndIndexes;
    /// <summary>The scheduler's cache purge: only when the write lock is free (false when put off).</summary>
    internal bool TryPurgeCachesInBackground() => maintenanceUnderWriteLock(MaintenanceAction.PurgeCache, true);
    bool maintenanceUnderWriteLock(MaintenanceAction a, bool background) {
        if (background) {
            if (!tryEnterWriteLockForHousekeeping(false)) return false;
        } else {
            _lock.EnterWriteLock();
        }
        try {
            if (a.HasFlag(MaintenanceAction.ResetSecondaryLogFile)) {
                var activityId = RegisterActvity(DataStoreActivityCategory.Copying, "Resetting secondary log file");
                try {
                    _wal.EnsureSecondaryLogFile(activityId, this, true);
                } finally {
                    DeRegisterActivity(activityId);
                }
            }
            if (a.HasFlag(MaintenanceAction.ClearAiCache)) _ai?.ClearCache();
            if (a.HasFlag(MaintenanceAction.ClearCache)) {
                _nodes.ClearCache();
                _sets.ClearCache();
                _noPrimitiveActionsSinceClearCache = 0;
                _noTransactionsSinceClearCache = 0;
                _noQueriesSinceClearCache = 0;
                _noNodeGetsSinceClearCache = 0;
                Engines.ResetIndexCaches();
                markEnginesChanged();
                foreach (var i in _definition.GetAllIndexes()) i.CompressMemory();
                collectAndReleaseMemory();
            }

            if (a.HasFlag(MaintenanceAction.PurgeCache)) {
                _nodes.HalfCacheSize();
                _sets.HalfCacheSize();
                collectAndReleaseMemory();
            }
            if (a.HasFlag(MaintenanceAction.CompressMemory)) foreach (var i in _definition.GetAllIndexes()) i.CompressMemory();
            if (a.HasFlag(MaintenanceAction.GarbageCollect)) collectAndReleaseMemory();
            if (a.HasFlag(MaintenanceAction.ResetStateAndIndexes)) {
                if (State == DataStoreState.Closed || State == DataStoreState.Open || State == DataStoreState.Error) {
                    resetStateAndIndexes();
                } else {
                    throw new Exception("ResetStateAndIndexes can only be performed when the database is closed or in error state. ");
                }
            }
        } catch (Exception cacheErr) {
            throw createCriticalErrorAndSetDbToErrorState("Maintenance cache error. ", cacheErr);
        } finally {
            _lock.ExitWriteLock();
        }
        return true;
    }
    // Only a blocking, compacting, aggressive collect actually shrinks the process, and one pass on
    // its own reclaims nothing that has a finalizer - see MemoryReclaim, which measures both. It is a
    // pause, but these maintenance actions exist precisely to reclaim memory. One finalizer round
    // rather than the default two: this runs holding the write lock, so every pass is time no query
    // can be served, and one round already covers the file handles and streams the dropped caches let
    // go of. The button in the admin UI, which holds nothing, takes the deeper default.
    static void collectAndReleaseMemory() => MemoryReclaim.Collect(finalizerRounds: 1);
    public Task MaintenanceAsync(MaintenanceAction actions) {
        Maintenance(actions);
        return Task.CompletedTask;
    }
    public Task<DataStoreInfo> GetInfoAsync() => Task.FromResult(GetInfo());
    public long GetLogActionsNotItInStatefile() => Interlocked.Read(ref _noPrimitiveActionsSinceLastStateSnapshot); // a counter: no lock needed to read it
    DataStoreInfo? _lastStoreStatusWhenOpen;
    CpuMonitor _cpuMonitorInfo = new();
    public DataStoreInfo GetInfo() {
        var info = new DataStoreInfo();
        if (_state != DataStoreState.Open) return info;
        // the read lock is enough: everything read here is changed under the write lock or has a lock of its
        // own. Waiting for the write lock, even for 5 ms, would hold up every reader arriving meanwhile
        if (!_lock.TryEnterReadLock(5)) {
            if (_lastStoreStatusWhenOpen == null) return info;
            _lastStoreStatusWhenOpen.IsFresh = false;
            return _lastStoreStatusWhenOpen;
        }
        try {
            if (_state != DataStoreState.Open) return info;
            info.IsFresh = true;
            info.LogFirstStateUtc = new DateTime(_wal.FirstTimestamp, DateTimeKind.Utc);
            info.LogLastChange = new DateTime(_wal.LastTimestamp, DateTimeKind.Utc);
            info.StartUpMs = _startUpTimeMs;
            info.LogTruncatableActions = _noPrimitiveActionsInLogThatCanBeTruncated;
            info.LogTotalActions = _noPrimitiveActionsInLog;
            info.LogActionsNotItInStatefile = _noPrimitiveActionsSinceLastStateSnapshot;
            info.NoIndexesOutOfSync = _definition.GetAllIndexes().Where(i => i.PersistedTimestamp < _wal.LastTimestamp).Count();
            info.LogTransactionsNotItInStatefile = _noTransactionsSinceLastStateSnapshot;
            info.CountActionsSinceClearCache = _noPrimitiveActionsSinceClearCache;
            info.CountTransactionsSinceClearCache = _noTransactionsSinceClearCache;
            info.CountQueriesSinceClearCache = _noQueriesSinceClearCache;
            info.CountNodeGetsSinceClearCache = _noNodeGetsSinceClearCache;
            info.InitiatedUtc = _initiatedUtc;
            info.UptimeMs = (long)Math.Round((DateTime.UtcNow - _initiatedUtc).TotalMilliseconds);

            info.TypeCounts = [];
            foreach (var t in _definition.NodeTypes.Values) {
                info.TypeCounts.Add(t.Model.FullName, _definition.GetCountForTypeForStatusInfo(t.Id));
            }

            info.QueuedTaskEstimatedMsUntilEmpty = (long?)TaskQueue.EstimateDurationUntilEmpty()?.TotalMilliseconds ?? 0;
            info.QueuedTaskEstimatedMsUntilEmptyPersisted = (long?)TaskQueuePersisted?.EstimateDurationUntilEmpty()?.TotalMilliseconds ?? 0;

            info.QueuedTasksPending = TaskQueue.CountTasks(BatchState.Pending);
            info.QueuedTaskStateCounts = TaskQueue.TaskCountsPerState().ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
            info.QueuedTasksPendingPersisted = TaskQueuePersisted?.CountTasks(Tasks.BatchState.Pending) ?? 0;
            info.QueuedTaskStateCountsPersisted = TaskQueuePersisted?.TaskCountsPerState().ToDictionary(kv => kv.Key.ToString(), kv => kv.Value) ?? [];

            info.QueuedBatchesPending = TaskQueue.CountBatch(BatchState.Pending);
            info.QueuedBatchesStateCounts = TaskQueue.BatchCountsPerState().ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
            info.QueuedBatchesPendingPersisted = TaskQueuePersisted?.CountBatch(Tasks.BatchState.Pending) ?? 0;
            info.QueuedBatchesStateCountsPersisted = TaskQueuePersisted?.BatchCountsPerState().ToDictionary(kv => kv.Key.ToString(), kv => kv.Value) ?? [];

            info.ProcessWorkingMemory = Process.GetCurrentProcess().WorkingSet64;
            info.CpuUsagePercentage = _cpuMonitorInfo.DequeCpuUsage() * 100.0;
            info.CpuUsagePercentageLastMinute = _cpuMonitorInfo.Estimate(TimeSpan.FromMinutes(1)) * 100.0;
            _definition.AddInfo(info);
            _nodes.AddInfo(info);
            info.RevisionCount = _definition.NodeTypeIndex.RevisionCount;
            info.EmbeddedNodeCount = _definition.NodeTypeIndex.EmbeddedNodeCount;
            info.MarkedDeletedNodeCount = _definition.NodeTypeIndex.MarkedDeletedNodeCount;
            info.RelationCount = _relations.TotalCount();
            try { _wal.AddInfo(info); } catch { } // as files may be closed...

            try { info.LoggingFileSize = Logger.GetTotalFileSize(); } catch { }
            try { info.BackupFileSize = FileKeyUtility.WAL_GetAllBackUpFileKeys(_ioAutoBackup).Sum(f => _ioAutoBackup.GetFileSizeOrZeroIfUnknown(f)); } catch { }

            lock (_isRewritingOrCopyingLock) {
                info.RunningRewriteFile = _rewriter != null ? _rewriter.FileKey.AsKeyString() : null;
            }

            _sets.AddInfo(info);
            info.LogStateFileSize = FileKeyUtility.State_GetAllFileKeys(IOIndex).Sum(k => IOIndex.GetFileSizeOrZeroIfUnknown(k));
        } finally {
            _lock.ExitReadLock();
        }
        _lastStoreStatusWhenOpen = info;
        return info;
    }
    public void SetTimestamp(long timestamp) {
        _lock.EnterWriteLock();
        try {
            validateDatabaseState();
            if (timestamp <= _wal.LastTimestamp) throw new Exception("Timestamp must be greater than last timestamp. ");
            _wal.StoreTimestamp(timestamp); // written to the log as an empty transaction
            // the engines move with it: left behind, the next state snapshot would be newer than all of them, and
            // the open after it would replay the log from the start
            Engines.BeginTransaction();
            Engines.CommitTransaction(timestamp);
        } finally {
            _lock.ExitWriteLock();
        }
    }
}
