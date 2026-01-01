using Relatude.DB.Common;
using Relatude.DB.DataStores.Stores;
using Relatude.DB.IO;
using Relatude.DB.Tasks;
using Relatude.DB.Transactions;
using System;
using System.Diagnostics;
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
        } catch (Exception err) {
            _state = DataStoreState.Error;
            throw createCriticalErrorAndSetDbToErrorState("Critical error. Database left in unknown state. Restart required. ", err);
        } finally {
            DeRegisterActivity(activityId);
        }
    }
    public void CopyStore(string newLogFileKey, IIOProvider? destinationIO = null) {
        lock (_isRewritingOrCopyingLock) {
            if (_isRewritingOrCopying) throw new Exception("Store rewrite or copy already in progress. ");
            _isRewritingOrCopying = true;
        }
        var activityId = RegisterActvity(DataStoreActivityCategory.Copying, "Copying log file");
        FlushToDisk(true, activityId);
        _lock.EnterWriteLock();
        try {
            _wal.Copy(newLogFileKey, destinationIO);
        } catch (Exception err) {
            throw createCriticalErrorAndSetDbToErrorState("Failed to copy log file. ", err);
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitWriteLock();
            lock (_isRewritingOrCopyingLock) _isRewritingOrCopying = false;
        }
    }
    public void TruncateIndexes() {
        var activityId = RegisterActvity(DataStoreActivityCategory.Copying, "Truncate indexes");
        FlushToDisk(true, activityId); // ensuring all writes are flushed before entering lock
        _lock.EnterWriteLock();
        try {
            FlushToDisk(true, activityId);
            validateDatabaseState();
            PersistedIndexStore?.OptimizeDisk();
        } catch (Exception err) {
            throw createCriticalErrorAndSetDbToErrorState("Failed to truncate indexes. ", err);
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitWriteLock();
        }
    }
    public int DeleteOldLogs() {
        _lock.EnterWriteLock();
        var fileDeleted = 0;
        var activityId = RegisterActvity(DataStoreActivityCategory.Copying, "Deleting old logs");
        try {
            validateDatabaseState();
            foreach (var f in _fileKeys.WAL_GetAllFileKeys(_io)) {
                if (_wal.FileKey != f) {
                    _io.DeleteIfItExists(f);
                    LogInfo($"Deleted old log file {f}. ");
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
        var activityId = RegisterActvity(DataStoreActivityCategory.SavingState, "Saving index states");
        FlushToDisk(true, activityId); // ensuring all writes are flushed before locking, to minimize time spent locked
        lock (_saveStateLock) { // to avoid multiple simultaneous calls
            _lock.EnterWriteLock();
            FlushToDisk(true, activityId); // ensuring all writes are flushed after locking, should be quick since flushed before lock
            try {
                validateDatabaseState();
                if (IOIndex.DoesNotExistOrIsEmpty(_fileKeys.StateFileKey) || _noPrimitiveActionsSinceLastStateSnapshot > 0 || forceRefresh) {
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
        var stateFileExisted = IOIndex.ExistsAndIsNotEmpty(_fileKeys.StateFileKey);
        IOIndex.DeleteIfItExists(_fileKeys.StateFileKey);
        var indexesFiles = FileKeys.Index_GetAll(IOIndex);
        foreach (var i in indexesFiles) IOIndex.DeleteIfItExists(i);
        if (stateFileExisted) {
            _noPrimitiveActionsSinceLastStateSnapshot = Settings.AutoSaveIndexStatesActionCountUpperLimit + 1;
        }
        PersistedIndexStore?.ResetAll();
    }
    public void Maintenance(MaintenanceAction a) {
        if (a.HasFlag(MaintenanceAction.TruncateLog) && _noPrimitiveActionsInLogThatCanBeTruncated > 0) {
            var task = new RewriteTask() {
                HotSwapToNewFile = true,
                DeleteOldDbFilesAfterHotSwap = a.HasFlag(MaintenanceAction.DeleteOldLogs),
                NewLogFileKey = _fileKeys.WAL_NextFileKey(_io),
                IO = _io,
                Truncate = true,
            };
            EnqueueTask(task);
        }
        if (a.HasFlag(MaintenanceAction.TruncateIndexes)) TruncateIndexes();
        if (a.HasFlag(MaintenanceAction.DeleteOldLogs)) DeleteOldLogs();
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
                foreach (var i in _definition.GetAllIndexes()) i.CompressMemory();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
            }
            if (a.HasFlag(MaintenanceAction.PurgeCache)) {
                _nodes.HalfCacheSize();
                _sets.HalfCacheSize();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
            }
            if (a.HasFlag(MaintenanceAction.CompressMemory)) foreach (var i in _definition.GetAllIndexes()) i.CompressMemory();
            if (a.HasFlag(MaintenanceAction.GarbageCollect)) GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
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
            try { info.FileStoreSize = _fileStores.Select(kv => kv.Value.GetSize()).Sum(); } catch { }
            try { info.BackupFileSize = FileKeys.FileStore_GetAllBackUpFileKeys(_ioAutoBackup).Select(f => _ioAutoBackup.GetFileSizeOrZeroIfUnknown(f)).Sum(); } catch { }
            try { info.IndexFileSize = PersistedIndexStore?.GetTotalDiskSpace() ?? 0L + FileKeys.Index_GetAll(_ioIndex).Select(f => _ioIndex.GetFileSizeOrZeroIfUnknown(f)).Sum(); } catch { }
            info.TotalFileSize = info.LogFileSize + info.FileStoreSize + info.LogStateFileSize + info.LoggingFileSize + info.SecondaryLogFileSize + info.BackupFileSize + info.IndexFileSize;

            lock (_isRewritingOrCopyingLock) {
                info.RunningRewriteFile = _rewriter != null ? _rewriter.FileKey : null;
            }

            _sets.AddInfo(info);
            info.LogStateFileSize = IOIndex.GetFileSizeOrZeroIfUnknown(_fileKeys.StateFileKey);
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
    public void Rollback(long timestamp) {
        throw new NotImplementedException();
    }
}
