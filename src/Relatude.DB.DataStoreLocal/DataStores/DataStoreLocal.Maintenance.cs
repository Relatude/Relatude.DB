using Relatude.DB.Common;
using Relatude.DB.DataStores.Stores;
using Relatude.DB.IO;
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
            _wal.DequeuAllTransactionWritesAndFlushStreams(deepFlush, (txt, prg) => {
                UpdateActivity(activityId, txt, prg);
            }, out transactionCount, out actionCount, out bytesWritten);
        } catch (Exception err) {
            _state = DataStoreState.Error;
            throw new Exception("Critical error. Database left in unknown state. Restart required. ", err);
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
            _state = DataStoreState.Error;
            throw new Exception("Failed to copy log file. ", err);
        } finally {
            DeRegisterActivity(activityId);
            _lock.ExitWriteLock();
            lock (_isRewritingOrCopyingLock) _isRewritingOrCopying = false;
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
    readonly object _isRewritingOrCopyingLock = new();
    bool _isRewritingOrCopying = false;
    public void RewriteStore(bool hotSwapToNewFile, string newLogFileKey, IIOProvider? destinationIO = null) {
        lock (_isRewritingOrCopyingLock) {
            if (_isRewritingOrCopying) throw new Exception("Store rewrite or copy already in progress. ");
            _isRewritingOrCopying = true;
        }
        var activityId = RegisterActvity(DataStoreActivityCategory.Rewriting, "Starting rewrite of log file", 0);
        try {
            rewriteStore(activityId, hotSwapToNewFile, newLogFileKey, destinationIO);
        } finally {
            DeRegisterActivity(activityId);
            lock (_isRewritingOrCopyingLock) _isRewritingOrCopying = false;
        }
    }
    void rewriteStore(long activityId, bool hotSwapToNewFile, string newLogFileKey, IIOProvider? destinationIO = null) {
        // written to minimize locking while rewriting store
        validateDatabaseState();
        if (destinationIO == null) destinationIO = _io;
        if (string.IsNullOrEmpty(newLogFileKey)) throw new Exception("New log file name cannot be empty. ");
        if (newLogFileKey == _wal.FileKey) throw new Exception("New log file name cannot be the same as current. ");
        if (_rewriter != null) throw new Exception("Rewriter already initialized. ");
        var sw = Stopwatch.StartNew();
        UpdateActivity(activityId, "Flushing stream before rewrite lock", 1);
        FlushToDisk(true, activityId); // ensuring a flush before starting rewrite and lock to minized time for flush while locked...
        sw.Stop();
        UpdateActivity(activityId, $"Flush completed in {sw.ElapsedMilliseconds} ms", 1);
        LogInfo($"Rewrite first flush completed in {sw.ElapsedMilliseconds} ms");
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
            sw.Restart();
            UpdateActivity(activityId, "Second flushing of stream inside rewrite lock", 2);
            FlushToDisk(true, activityId); // making sure every segment exists in _nodes ( through call back )
            sw.Stop();
            UpdateActivity(activityId, $"Second flush completed in {sw.ElapsedMilliseconds} ms", 2);
            LogInfo($"Rewrite second flush completed in {sw.ElapsedMilliseconds} ms");

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
        IOIndex.DeleteIfItExists(_fileKeys.StateFileKey);
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
                if (hotSwapToNewFile) IOIndex.DeleteIfItExists(_fileKeys.StateFileKey);
            } finally {
                _lock.ExitWriteLock();
            }
            if (hotSwapToNewFile) SaveIndexStates(true, true); // needed to refresh state file with new log file
            LogRewriter.DeleteFlagFileToIndicateLogRewriterStart(destinationIO, _rewriter.FileKey);
            _rewriter = null;
        } catch (Exception err) {
            _state = DataStoreState.Error;
            throw new Exception("Critical error. Database left in unknown state. Restart required. ", err);
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
    public void SaveIndexStates(bool forceRefresh = false, bool nodeSegmentsOnly = false) {
        var activityId = RegisterActvity(DataStoreActivityCategory.SavingState, "Saving index states");
        FlushToDisk(true, activityId); // ensuring all writes are flushed before locking
        lock (_saveStateLock) { // to avoid multiple simultaneous calls
            _lock.EnterReadLock();
            FlushToDisk(true, activityId); // ensuring all writes are flushed after locking
            try {
                validateDatabaseState();
                if (IOIndex.DoesNotExistOrIsEmpty(_fileKeys.StateFileKey) || _noPrimitiveActionsSinceLastStateSnaphot > 0 || forceRefresh) {
                    var sw = Stopwatch.StartNew();
                    LogInfo("Initiating index state write.");
                    saveMainState(activityId); // requires WriteLock after flush due to node segments
                    if (!nodeSegmentsOnly) saveIndexesStates(activityId);
                    LogInfo("Index state write completed in " + sw.ElapsedMilliseconds.To1000N() + " ms.");
                } else {
                    LogInfo("Index state write skipped as file reflects latest changes. ");
                }
            } catch (Exception err) {
                _state = DataStoreState.Error;
                throw new Exception("Failed to save index states. ", err);
            } finally {
                DeRegisterActivity(activityId);
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
                IOIndex.DeleteIfItExists(_fileKeys.StateFileKey); 
                var indexesFiles = FileKeys.Index_GetAll(IOIndex);
                foreach (var i in indexesFiles) IOIndex.DeleteIfItExists(i);
                PersistedIndexStore?.ResetAll();
            }
        } catch (Exception cacheErr) {
            _state = DataStoreState.Error;
            logCriticalError("Maintenance cache error. ", cacheErr);
            throw new Exception("Maintenance cache error. ", cacheErr);
        } finally {
            _lock.ExitWriteLock();
        }
        if (a.HasFlag(MaintenanceAction.FlushDisk)) FlushToDisk(true, 0);
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

    StoreStatus? _lastStoreStatusWhenOpen;
    public StoreStatus GetInfo() {
        var info = new StoreStatus();
        if (_state != DataStoreState.Open) return info;
        if (!_lock.TryEnterWriteLock(100)) {
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
                info.TypeCounts.Add(t.Model.FullName, _definition.GetCountForTypeForStatusInfo(t.Id));
            }

            info.QueuedTasksPending = TaskQueue.CountTasks(Tasks.BatchState.Pending);
            info.QueuedTaskStateCounts = TaskQueue.TaskCountsPerState().ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
            info.QueuedTasksPendingPersisted = TaskQueuePersisted?.CountTasks(Tasks.BatchState.Pending) ?? 0;
            info.QueuedTaskStateCountsPersisted = TaskQueuePersisted?.TaskCountsPerState().ToDictionary(kv => kv.Key.ToString(), kv => kv.Value) ?? [];

            info.QueuedBatchesPending = TaskQueue.CountBatch(Tasks.BatchState.Pending);
            info.QueuedBatchesStateCounts = TaskQueue.BatchCountsPerState().ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
            info.QueuedBatchesPendingPersisted = TaskQueuePersisted?.CountBatch(Tasks.BatchState.Pending) ?? 0;
            info.QueuedBatchesStateCountsPersisted = TaskQueuePersisted?.BatchCountsPerState().ToDictionary(kv => kv.Key.ToString(), kv => kv.Value) ?? [];

            info.DiskSpacePersistedIndexes = PersistedIndexStore?.GetTotalDiskSpace() ?? 0L;
            info.ProcessWorkingMemory = Process.GetCurrentProcess().WorkingSet64;

            _definition.AddInfo(info);
            _nodes.AddInfo(info);
            info.RelationCount = _relations.TotalCount();
            try { _wal.AddInfo(info); } catch { } // as files may be closed...
            _sets.AddInfo(info);
            info.LogStateFileSize = IOIndex.GetFileSizeOrZeroIfUnknown(_fileKeys.StateFileKey);
        } finally {
            _lock.ExitWriteLock();
        }
        _lastStoreStatusWhenOpen = info;
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
