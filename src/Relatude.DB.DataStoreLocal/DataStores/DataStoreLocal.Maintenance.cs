using Relatude.DB.Common;
using Relatude.DB.IO;
using Relatude.DB.Tasks;
using System.Diagnostics;
using System.Runtime;
namespace Relatude.DB.DataStores;

public sealed partial class DataStoreLocal : IDataStore {
    public long GetNoPrimitiveActionsInLogThatCanBeTruncated() {
        if (_state != DataStoreState.Open) return 0;
        _lock.EnterWriteLock();
        try {
            return _noPrimitiveActionsInLogThatCanBeTruncated;
        } finally {
            _lock.ExitWriteLock();
        }
    }
    internal int FlushToDisk(bool deepFlush, long parentActivityId) {
        FlushToDisk(deepFlush, parentActivityId, out int transactionCount, out _, out _);
        return transactionCount;
    }
    internal void FlushToDisk(bool deepFlush, long parentActivityId, out int transactionCount, out int actionCount, out long bytesWritten) {
        var activityId = RegisterActvity(parentActivityId, DataStoreActivityCategory.Flushing, "Flushing to disk");
        validateDatabaseState();
        try {
            _wal.DequeuAllTransactionWritesAndFlushStreamsThreadSafe(deepFlush, (txt, prg) => {
                UpdateActivity(activityId, txt, prg);
            }, out transactionCount, out actionCount, out bytesWritten);
            TaskQueuePersisted?.FlushDisk();
            if (Engines.Any) {
                // Persisted indexes commit only in memory at transaction execution and are made
                // durable here, AFTER the WAL flush � so the durable indexes can never contain
                // transactions the durable log is missing. The write lock (briefly, the lock
                // supports recursion) excludes executes, and the final drain covers transactions
                // that executed while the flush above was writing, so at the durable write the
                // log provably contains every committed index transaction.
                _lock.EnterWriteLock();
                try {
                    _wal.DequeuAllTransactionWritesAndFlushStreamsThreadSafe(deepFlush);
                    // during a revert window the engines must not persist past the window start, so
                    // they can reopen there after a rollback; the log itself stays fully durable
                    // (checked inside the lock: a racing BeginRevertWindow cannot be trailed by a
                    // stale MakeDurable that would stamp the engines past the window start)
                    if (_revertWindow == null) Engines.MakeDurable(_wal.LastTimestamp);
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
    public void CopyStore(string[] newLogFileKey, IIOProvider? destinationIO = null) {
        lock (_isRewritingOrCopyingLock) {
            if (_isRewritingOrCopying) throw new Exception("Store rewrite or copy already in progress. ");
            _isRewritingOrCopying = true;
        }
        var activityId = RegisterActvity(DataStoreActivityCategory.Copying, "Copying log file");
        try { // all fallible work inside try/finally so the flag and activity are always reset
            FlushToDisk(true, activityId);
            _lock.EnterWriteLock();
            try {
                _wal.Copy(newLogFileKey, destinationIO);
            } catch (Exception err) {
                throw createCriticalErrorAndSetDbToErrorState("Failed to copy log file. ", err);
            } finally {
                _lock.ExitWriteLock();
            }
        } finally {
            DeRegisterActivity(activityId);
            lock (_isRewritingOrCopyingLock) _isRewritingOrCopying = false;
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
    public void SaveIndexStates(bool forceRefresh = false, bool nodeSegmentsOnly = false) {
        if (RevertWindowIsActive) { // cheap early skip; re-checked under the lock below
            LogInfo("Index state save skipped: a revert window is active. ");
            return;
        }
        var activityId = RegisterActvity(DataStoreActivityCategory.SavingState, "Saving index states");
        try { // outer try so a flush failure before the lock still deregisters the activity
            FlushToDisk(true, activityId); // ensuring all writes are flushed before locking, to minimize time spent locked
        } catch {
            DeRegisterActivity(activityId);
            throw;
        }
        lock (_saveStateLock) { // to avoid multiple simultaneous calls
            _lock.EnterWriteLock();
            try {
                if (_revertWindow != null) { // a state snapshot must never cover transactions past the window start
                    LogInfo("Index state save skipped: a revert window is active. ");
                    return;
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
    }
    public void SaveIndexCaches(bool force) {
        _lock.EnterWriteLock();
        try {
            if (_revertWindow != null) return; // persisted caches must not cover transactions past the window start
            if (State == DataStoreState.Open)
                Engines.SaveIndexCaches(force);
        } finally {
            _lock.ExitWriteLock();
        }
    }
    public void ResetIndexCaches() {
        _lock.EnterWriteLock();
        try {
            if (State == DataStoreState.Open)
                Engines.ResetIndexCaches();
        } finally {
            _lock.ExitWriteLock();
        }
    }
    public void Maintenance(MaintenanceAction a) {
        if (a.HasFlag(MaintenanceAction.TruncateLog) && RevertWindowIsActive) {
            LogInfo("Log truncation skipped: a revert window is active. ");
        } else if (a.HasFlag(MaintenanceAction.TruncateLog) && _noPrimitiveActionsInLogThatCanBeTruncated > 0) {
            var task = new RewriteTask() {
                HotSwapToNewFile = true,
                DeleteOldDbFilesAfterHotSwap = a.HasFlag(MaintenanceAction.DeleteOldLogs),
                NewLogFileKey = null,
                IO = _io,
                Truncate = true,
            };
            EnqueueTask(task);
        }
        if (a.HasFlag(MaintenanceAction.TruncateIndexes)) TruncateIndexes();
        if (a.HasFlag(MaintenanceAction.DeleteOldLogs) && !a.HasFlag(MaintenanceAction.TruncateLog)) DeleteOldLogs();
        if (a.HasFlag(MaintenanceAction.SaveIndexStates)) SaveIndexStates();
        _lock.EnterWriteLock();
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
        if (a.HasFlag(MaintenanceAction.FlushDisk)) FlushToDisk(true, 0);
        if(a.HasFlag(MaintenanceAction.UpdatePersistedCaches)) SaveIndexCaches(true);
        // caches gone: rebuild the mirror and facet warm state in the background, so the next
        // filtered facet query does not pay the cold rebuild inline
        if (a.HasFlag(MaintenanceAction.ClearCache) && State == DataStoreState.Open) warmIndexesInBackground();
    }
    // A background, non-compacting collect leaves the freed bytes as holes in committed regions and
    // returns nothing to the OS. Only a blocking, compacting, aggressive collect actually shrinks
    // the process. It is a pause, but these maintenance actions exist precisely to reclaim memory.
    static void collectAndReleaseMemory() {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }
    public Task MaintenanceAsync(MaintenanceAction actions) {
        Maintenance(actions);
        return Task.CompletedTask;
    }
    public Task<DataStoreInfo> GetInfoAsync() => Task.FromResult(GetInfo());
    public long GetLogActionsNotItInStatefile() {
        _lock.EnterReadLock();
        try {
            return _noPrimitiveActionsSinceLastStateSnapshot;
        } finally {
            _lock.ExitReadLock();
        }
    }
    DataStoreInfo? _lastStoreStatusWhenOpen;
    CpuMonitor _cpuMonitorInfo = new();
    public DataStoreInfo GetInfo() {
        var info = new DataStoreInfo();
        if (_state != DataStoreState.Open) return info;
        if (!_lock.TryEnterWriteLock(5)) {
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
            _lock.ExitWriteLock();
        }
        _lastStoreStatusWhenOpen = info;
        return info;
    }
    public void SetTimestamp(long timestamp) {
        _lock.EnterWriteLock();
        try {
            validateDatabaseState();
            if (timestamp <= _wal.LastTimestamp) throw new Exception("Timestamp must be greater than last timestamp. ");
            _wal.StoreTimestamp(timestamp);
        } finally {
            _lock.ExitWriteLock();
        }
    }
}
