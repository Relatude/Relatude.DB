using Relatude.DB.IO;
using Relatude.DB.Common;
using Relatude.DB.DataStores.Stores;
using Relatude.DB.Transactions;
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
    public void DeleteIndexStateFile() => _io.DeleteIfItExists(_fileKeys.StateFileKey);
    internal int FlushToDisk() {
        FlushToDisk(out int transactionCount, out _, out _);
        return transactionCount;
    }
    internal void FlushToDisk(out int transactionCount, out int actionCount, out long bytesWritten) {
        _lock.EnterWriteLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Flushing, "Flushing to disk");
        try {
            validateDatabaseState();
            _wal.FlushToDisk(out transactionCount, out actionCount, out bytesWritten);
        } catch (Exception err) {
            _state = DataStoreState.Error;
            throw new Exception("Critical error. Database left in unknown state. Restart required. ", err);
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitWriteLock();
        }
    }
    public void CopyStore(string newLogFileKey, IIOProvider? destinationIO = null) {
        _lock.EnterWriteLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Copying, "Copying log file");
        try {
            _wal.FlushToDisk();
            _wal.Copy(newLogFileKey, destinationIO);
        } catch (Exception err) {
            _state = DataStoreState.Error;
            throw new Exception("Failed to copy log file. ", err);
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitWriteLock();
        }
    }
    byte[][] threadSafeReadSegments(NodeSegment[] segments, out int diskReads) {
        _lock.EnterReadLock();
        try {
            validateDatabaseState();
            return _wal.ReadNodeSegments(segments, out diskReads);
        } finally {
            _lock.ExitReadLock();
        }
    }
    public void RewriteStore(bool hotSwapToNewFile, string newLogFileKey, IIOProvider? destinationIO = null) {
        var activityId = RegisterActvity(DataStoreActivityCategory.Rewriting, "Starting rewrite of log file", 0);
        try {
            rewriteStore(activityId, hotSwapToNewFile, newLogFileKey, destinationIO);
        } finally {
            DeRegisterActivity(activityId);
        }
    }
    void rewriteStore(long activityId, bool hotSwapToNewFile, string newLogFileKey, IIOProvider? destinationIO = null) {
        // written to minimize locking while rewriting store
        validateDatabaseState();
        if (destinationIO == null) destinationIO = _io;
        if (string.IsNullOrEmpty(newLogFileKey)) throw new Exception("New log file name cannot be empty. ");
        if (newLogFileKey == _wal.FileKey) throw new Exception("New log file name cannot be the same as current. ");
        if (_rewriter != null) throw new Exception("Rewriter already initialized. ");
        _lock.EnterWriteLock();
        try {
            if (LogRewriter.LogRewriterAlreadyInprogress(destinationIO)) {
                throw new Exception("Log rewriter already in progress. ");
            }
        } catch {
            _lock.ExitWriteLock();
            throw;
        }
        var initialNoPrimitiveActionsInLogThatCanBeTruncated = _noPrimitiveActionsInLogThatCanBeTruncated;
        try {
            _wal.FlushToDisk(); // maing sure every segment exists in _nodes ( through call back )
            // starting rewrite of log file, requires all writes and reads to be blocked, making sure snaphot is consistent
            LogRewriter.CreateFlagFileToIndicateLogRewriterInprogress(destinationIO, newLogFileKey);
            UpdateActivity(activityId, "Starting rewrite of log file", 5);
            _rewriter = new LogRewriter(newLogFileKey, _definition, destinationIO, _nodes.Snapshot(), _relations.Snapshot(), threadSafeReadSegments, updateNodeDataPositionInLogFile);
            UpdateActivity(activityId, "Starting rewrite of log file", 10);
        } catch (Exception err) {
            _state = DataStoreState.Error;
            throw new Exception("Critical error. Database left in unknown state. Restart required. ", err);
        } finally {
            _lock.ExitWriteLock();
        }
        try {
            // no block, allowing simulatenous writes or reads while log is being rewritten
            _rewriter.Step1_RewriteLog_NoLockRequired((string desc, int prg) => UpdateActivity(activityId, desc, prg)); // (10%-80%)
        } catch (Exception err) {
            _state = DataStoreState.Error;
            throw new Exception("Critical error. Database left in unknown state. Restart required. ", err);
        }
        _io.DeleteIfItExists(_fileKeys.StateFileKey);
        try {
            _lock.EnterWriteLock();
            UpdateActivity(activityId, "Finalizing rewrite", 90);  // (90%-100%)
            if (_rewriter == null) throw new Exception("Rewriter not initialized. ");
            try {
                _rewriter.Step2_HotSwap_RequiresWriteLock(_wal, hotSwapToNewFile);  // finalizes log rewrite, should be short, but blocks all writes and reads
                if (hotSwapToNewFile) {
                    _noPrimitiveActionsInLogThatCanBeTruncated -= initialNoPrimitiveActionsInLogThatCanBeTruncated;
                    // reset, since we have a new log file
                }
                //if (hotSwapToNewFile) saveState(); // needed to refresh state file with new log file
                if (hotSwapToNewFile) _io.DeleteIfItExists(_fileKeys.StateFileKey);
            } finally {
                _lock.ExitWriteLock();
            }
            if (hotSwapToNewFile) SaveIndexStates(); // needed to refresh state file with new log file
            LogRewriter.DeleteFlagFileToIndicateLogRewriterStart(destinationIO, _rewriter.FileKey);
            _rewriter = null;
        } catch (Exception err) {
            _state = DataStoreState.Error;
            throw new Exception("Critical error. Database left in unknown state. Restart required. ", err);
        }
    }
    public void TruncateIndexes() {
        _lock.EnterWriteLock();
        var activityId = RegisterActvity(DataStoreActivityCategory.Copying, "Truncate indexes");
        try {
            validateDatabaseState();
            _wal.FlushToDisk();
            PersistedIndexStore?.OptimizeDisk();
        } catch (Exception err) {
            _state = DataStoreState.Error;
            throw new Exception("Failed truncate indexes. ", err);
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
    public void SaveIndexStates(bool forceRefresh = false) {
        lock (_saveStateLock) { // to avoid multiple simultaneous calls
            //_lock.EnterWriteLock();
            _lock.EnterReadLock();
            var activityId = RegisterActvity(DataStoreActivityCategory.SavingState, "Saving index states");
            try {
                validateDatabaseState();
                if (_io.DoesNotExistOrIsEmpty(_fileKeys.StateFileKey) || _noPrimitiveActionsSinceLastStateSnaphot > 0 || forceRefresh) {
                    LogInfo("Initiating index state write.");
                    _wal.FlushToDisk();
                    saveState();
                    LogInfo("Index state write completed.");
                } else {
                    LogInfo("Index state write skipped as file reflects latest changes. ");
                }
            } catch (Exception err) {
                _state = DataStoreState.Error;
                throw new Exception("Failed to save index states. ", err);
            } finally {
                DeRegisterActivity(activityId);
                //_lock.ExitWriteLock();
                _lock.ExitReadLock();
            }
        }
    }
    public void Maintenance(MaintenanceAction a) {
        if (a.HasFlag(MaintenanceAction.TruncateLog) && _noPrimitiveActionsInLogThatCanBeTruncated > 0) RewriteStore(true, _fileKeys.WAL_NextFileKey(_io));
        if (a.HasFlag(MaintenanceAction.TruncateIndexes)) TruncateIndexes();
        if (a.HasFlag(MaintenanceAction.DeleteOldLogs)) DeleteOldLogs();
        if (a.HasFlag(MaintenanceAction.SaveIndexStates)) SaveIndexStates();
        _lock.EnterWriteLock();
        try {
            if (a.HasFlag(MaintenanceAction.ClearAiCache)) _ai?.ClearCache();
            if (a.HasFlag(MaintenanceAction.ClearCache)) {
                _nodes.ClearCache();
                _sets.ClearCache();
                _noPrimitiveActionsSinceClearCache = 0;
                _noTransactionsSinceClearCache = 0;
                _noQueriesSinceClearCache = 0;
                _noNodeGetsSinceClearCache = 0;
                foreach (var i in _definition.GetAllIndexes()) i.CompressMemory();
            }
            if (a.HasFlag(MaintenanceAction.PurgeCache)) {
                _nodes.HalfCacheSize();
                _sets.HalfCacheSize();
            }
            if (a.HasFlag(MaintenanceAction.CompressMemory)) foreach (var i in _definition.GetAllIndexes()) i.CompressMemory();
            if (a.HasFlag(MaintenanceAction.GarbageCollect)) GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
        } catch (Exception cacheErr) {
            _state = DataStoreState.Error;
            logCriticalError("Maintenance cache error. ", cacheErr);
            throw new Exception("Maintenance cache error. ", cacheErr);
        } finally {
            _lock.ExitWriteLock();
        }
        if (a.HasFlag(MaintenanceAction.FlushDisk)) FlushToDisk();
    }
    public Task<StoreStatus> GetInfoAsync() => Task.FromResult(GetInfo());
    public long GetLogActionsNotItInStatefile() {
        _lock.EnterReadLock();
        try {
            return _noPrimitiveActionsSinceLastStateSnaphot;
        } finally {
            _lock.ExitReadLock();
        }
    }
    public StoreStatus GetInfo() {
        var info = new StoreStatus();
        if (_state != DataStoreState.Open) return info;
        _lock.EnterWriteLock();
        try {
            if (_state != DataStoreState.Open) return info;
            info.LogFirstStateUtc = new DateTime(_wal.FirstTimestamp, DateTimeKind.Utc);
            info.LogLastChange = new DateTime(_wal.LastTimestamp, DateTimeKind.Utc);
            info.StartUpMs = _startUpTimeMs;
            info.LogTruncatableActions = _noPrimitiveActionsInLogThatCanBeTruncated;
            info.LogActionsNotItInStatefile = _noPrimitiveActionsSinceLastStateSnaphot;
            info.LogTransactionsNotItInStatefile = _noTransactionsSinceLastStateSnaphot;
            info.CountActionsSinceClearCache = _noPrimitiveActionsSinceClearCache;
            info.CountTransactionsSinceClearCache = _noTransactionsSinceClearCache;
            info.CountQueriesSinceClearCache = _noQueriesSinceClearCache;
            info.CountNodeGetsSinceClearCache = _noNodeGetsSinceClearCache;
            info.InitiatedUtc = _initiatedUtc;
            info.UptimeMs = (long)Math.Round((DateTime.UtcNow - _initiatedUtc).TotalMilliseconds);

            info.TypeCounts = [];
            foreach (var t in _definition.NodeTypes.Values) {
                info.TypeCounts.Add(t.Model.FullName, _definition.GetCountForType(t.Id));
            }

            info.QueuedTasksPending = TaskQueue.CountTasks(Tasks.BatchState.Pending);
            info.QueuedTaskStateCounts = TaskQueue.TaskCountsPerState().ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
            info.QueuedTasksPendingPersisted = TaskQueuePersisted?.CountTasks(Tasks.BatchState.Pending) ?? 0;
            info.QueuedTaskStateCountsPersisted = TaskQueuePersisted?.TaskCountsPerState().ToDictionary(kv => kv.Key.ToString(), kv => kv.Value) ?? [];

            info.QueuedBatchesPending = TaskQueue.CountBatch(Tasks.BatchState.Pending);
            info.QueuedBatchesStateCounts = TaskQueue.BatchCountsPerState().ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
            info.QueuedBatchesPendingPersisted = TaskQueuePersisted?.CountBatch(Tasks.BatchState.Pending) ?? 0;
            info.QueuedBatchesStateCountsPersisted = TaskQueuePersisted?.BatchCountsPerState().ToDictionary(kv => kv.Key.ToString(), kv => kv.Value) ?? [];

            _definition.AddInfo(info);
            _nodes.AddInfo(info);
            info.RelationCount = _relations.TotalCount();
            _wal.AddInfo(info);
            _sets.AddInfo(info);
            info.LogStateFileSize = _io.GetFileSizeOrZeroIfUnknown(_fileKeys.StateFileKey);
        } finally {
            _lock.ExitWriteLock();
        }
        return info;
    }
    public Task MaintenanceAsync(MaintenanceAction actions) {
        Maintenance(actions);
        return Task.CompletedTask;
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
