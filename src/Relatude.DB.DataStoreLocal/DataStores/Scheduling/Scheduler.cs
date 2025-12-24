using System.Diagnostics;
using Relatude.DB.Common;
using Relatude.DB.Tasks;

namespace Relatude.DB.DataStores.Scheduling;

internal class Scheduler(DataStoreLocal _db) {
    SettingsLocal _s => _db.Settings;
    Timer? _autoFlushTimer; // timer for auto flushing disk
    Timer? _taskDequeueTimer; // timer only for dequeuing index tasks
    Timer? _taskDequeuePersistedTimer; // timer only for dequeuing persisted index tasks
    Timer? _backgroundTaskTimer;  // general background grouping a number of background tasks ( auto state file save, backup, cache purge etc )
    Timer? _metricRecorder;

    OnlyOneThreadRunning _autoFlushTaskRunningFlag = new(); // flag to ensure only one thread is running auto flush at a time
    OnlyOneThreadRunning _dequeIndexTaskRunningFlag = new(); // flag to ensure only one thread is running index task dequeue at a time
    OnlyOneThreadRunning _dequeIndexTaskPersistedRunningFlag = new(); // flag to ensure only one thread is running index task dequeue at a time
    OnlyOneThreadRunning _backgroundTaskRunningFlag = new(); // flag to ensure only one thread is running background tasks at a time

    DateTime _lastSaveIndexStates = DateTime.MinValue; // running at startup if above min action count
    DateTime _lastTruncate = DateTime.UtcNow; // do not run truncate at startup unless due
    DateTime _lastCachePurge = DateTime.UtcNow;
    DateTime _lastAutoBackup = DateTime.UtcNow;

    int startupDelayMs = 2000; // delay before starting any timer. allowing system to start up and initialize properly before background tasks start running
    int timerStartupDelta = 323; // delta between different timers, so they do not all run at exactly the same time
    int defaultAutoFlushPulseIntervalMs = 1000; // default interval for auto flushing disk, if no setting is provided
    int taskQueuePulseIntervalMs = 1000; // default interval for checking for new tasks and the time allowed for building a batch of tasks
    int backgroundTasksPulseIntervalMs = 60000; // backup if due and delete old, cache purge, save log stats, flush logs etc. run every minute, not needed to run too often
    int metricRecorderPulseIntervalMs = 1000; // record metrics every second
    TimeSpan _intervalOfDeletingExpiredTasks = TimeSpan.FromMinutes(5); // interval for running delete expired tasks, default is 5 minutes

    //Timer _test = new Timer(_ => {
    //    Console.WriteLine("Transactions last 10 sec:" + _db._transactionActivity.EstimateLast10Seconds().To1000N() + ", Queries last 10 sec:" + _db._queryActivity.EstimateLast10Seconds().To1000N());
    //}, null, 10, 500); // just to have a timer for testing purposes

    public void Start() {

        // avoid zero interval:
        if (_s.AutoSaveIndexStates && _s.AutoSaveIndexStatesIntervalInMinutes <= 0) _s.AutoSaveIndexStatesIntervalInMinutes = 30;
        if (_s.AutoTruncate && _s.AutoTruncateIntervalInMinutes <= 0) _s.AutoTruncateIntervalInMinutes = 24 * 60;

        // initiating flush timer:
        if (_s.AutoFlushDiskInBackground) {
            var flushIntervalMs = (int)_s.AutoFlushDiskIntervalInSeconds * 1000;
            if (flushIntervalMs == 0) flushIntervalMs = defaultAutoFlushPulseIntervalMs; // default to 1 second if not set
            _autoFlushTimer = new Timer(autoFlushDisk, null, startupDelayMs, flushIntervalMs);
        }

        // initiating task dequeue timer:
        if (_s.AutoDequeTasks) {
            initTaskQueue(_db, _db.TaskQueue, "Memory");
            if (_db.TaskQueuePersisted != null) initTaskQueue(_db, _db.TaskQueuePersisted, "Persisted");
            _taskDequeueTimer = new Timer(dequeueTaskQueues, null, startupDelayMs + timerStartupDelta, taskQueuePulseIntervalMs);
            if (_db.TaskQueuePersisted != null) {
                _taskDequeuePersistedTimer = new Timer(dequeuePersistedTaskQueues, null, startupDelayMs + timerStartupDelta * 2, taskQueuePulseIntervalMs);
            }
        }

        // initiating general background task timer, running multiple other tasks
        // these tasks are grouped to easier avoid running multiple tasks at the same time and minimize no timers
        var anyBackgroundTasks =
            _s.AutoSaveIndexStates ||
            _s.AutoBackUp ||
            _s.AutoTruncate ||
            _s.AutoPurgeCache;

        if (anyBackgroundTasks) {
            _backgroundTaskTimer = new Timer(backgroundTaskPulse, null, startupDelayMs + timerStartupDelta * 3, backgroundTasksPulseIntervalMs);
        }

        if (metricRecorderPulseIntervalMs > 0) {
            _metricRecorder = new Timer(recordMetrics, null, startupDelayMs + timerStartupDelta * 4, metricRecorderPulseIntervalMs);
        }

    }
    public void Stop() {
        _autoFlushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _backgroundTaskTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _taskDequeueTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _taskDequeuePersistedTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _metricRecorder?.Change(Timeout.Infinite, Timeout.Infinite);
        _autoFlushTimer?.Dispose();
        _backgroundTaskTimer?.Dispose();
        _taskDequeueTimer?.Dispose();
        _taskDequeuePersistedTimer?.Dispose();
        _metricRecorder?.Dispose();
        _autoFlushTimer = null;
        _backgroundTaskTimer = null;
        _taskDequeueTimer = null;
        _taskDequeuePersistedTimer = null;
        _metricRecorder = null;
    }
    DateTime _lastDeleteExpiredTasks = DateTime.MinValue;
    DateTime _lastDeleteExpiredPersistedTasks = DateTime.MinValue;

    long _actionCountAtCompletionOfTaskDequeue;
    void dequeueTaskQueues(object? state) {
        if (_db.State != DataStoreState.Open) return;
        _taskDequeueTimer?.Change(Timeout.Infinite, Timeout.Infinite); // stop it...
        var actionsSinceLastDequeue = _db.GetNoPrimitiveActionsSinceStartup() - _actionCountAtCompletionOfTaskDequeue;
        _actionCountAtCompletionOfTaskDequeue = _db.GetNoPrimitiveActionsSinceStartup();
        var actionsPerSecondSinceLastDequeue = actionsSinceLastDequeue * 1000 / taskQueuePulseIntervalMs;
        if (actionsPerSecondSinceLastDequeue > 500) { // requires a constant interval between runs to be meaningful
            // wait a longer since there is a lot of activity
            _actionCountAtCompletionOfTaskDequeue = _db.GetNoPrimitiveActionsSinceStartup();
            _taskDequeueTimer?.Change(taskQueuePulseIntervalMs, Timeout.Infinite); // start it again... to make sure intervals between runs are consistent
            return;
        }
        deleteExpiredTasksIfDue(_db, _db.TaskQueue, ref _lastDeleteExpiredTasks, _intervalOfDeletingExpiredTasks);
        dequeueOneTaskQueue(_db.TaskQueue, _dequeIndexTaskRunningFlag, _db);
        _actionCountAtCompletionOfTaskDequeue = _db.GetNoPrimitiveActionsSinceStartup();
        _taskDequeueTimer?.Change(taskQueuePulseIntervalMs, Timeout.Infinite); // start it again... to make sure intervals between runs are consistent
    }

    long _actionCountAtCompletionOfPersistedTaskDequeue;
    void dequeuePersistedTaskQueues(object? state) {
        if (_db.State != DataStoreState.Open) return;
        _taskDequeuePersistedTimer?.Change(Timeout.Infinite, Timeout.Infinite); // stop it...
        var actionsSinceLastDequeue = _db.GetNoPrimitiveActionsSinceStartup() - _actionCountAtCompletionOfPersistedTaskDequeue;
        _actionCountAtCompletionOfPersistedTaskDequeue = _db.GetNoPrimitiveActionsSinceStartup();
        var actionsPerSecondSinceLastDequeue = actionsSinceLastDequeue * 1000 / taskQueuePulseIntervalMs;
        if (actionsPerSecondSinceLastDequeue > 500) { // requires a constant interval between runs to be meaningful
            // wait a longer since there is a lot of activity
            _actionCountAtCompletionOfPersistedTaskDequeue = _db.GetNoPrimitiveActionsSinceStartup();
            _taskDequeuePersistedTimer?.Change(taskQueuePulseIntervalMs, Timeout.Infinite);  // start it again... to make sure intervals between runs are consistent
            return;
        }
        if (_db.TaskQueuePersisted != null) {
            deleteExpiredTasksIfDue(_db, _db.TaskQueuePersisted, ref _lastDeleteExpiredPersistedTasks, _intervalOfDeletingExpiredTasks);
            dequeueOneTaskQueue(_db.TaskQueuePersisted, _dequeIndexTaskPersistedRunningFlag, _db);
        }
        _actionCountAtCompletionOfPersistedTaskDequeue = _db.GetNoPrimitiveActionsSinceStartup();
        _taskDequeuePersistedTimer?.Change(taskQueuePulseIntervalMs, Timeout.Infinite);  // start it again... to make sure intervals between runs are consistent
    }

    static void initTaskQueue(DataStoreLocal db, TaskQueue queue, string name) {
        var a = queue.BatchCountsPerState();
        if (a.Length == 0) {
            db.LogInfo(name + " queue initiated. (" + queue.QueueStoreTypeName + ")");
        } else {
            db.LogInfo(name + " queue initiated (" + queue.QueueStoreTypeName + "):");
            for (int i = 0; i < a.Length; i++) {
                var kv = a[i];
                db.LogInfo(" - " + kv.Value + " " + kv.Key.ToString().Decamelize().ToLower());
            }
        }
        queue.RestartTasksFromDbShutdown(out int restartedBatches, out int abortedBatches, out int restaredTasks, out int abortedTasks);
        if (restartedBatches > 0) db.LogInfo("   -> " + restartedBatches + " batches with " + restaredTasks + " tasks restarted after shutdown");
        if (abortedBatches > 0) db.LogInfo("   -> " + abortedBatches + " batches with " + abortedTasks + " tasks aborted due to shutdown");
    }
    static void dequeueOneTaskQueue(TaskQueue queue, OnlyOneThreadRunning oneThread, DataStoreLocal db) {
        if (oneThread.IsRunning_IfNotSetFlagToRunning()) return;
        long activityId = -1;
        try {
            if (db.State != DataStoreState.Open) return;
            if (queue.CountBatch(BatchState.Pending) == 0) return; // no tasks to execute            
            Stopwatch sw = Stopwatch.StartNew();
            bool abort() => db.State != DataStoreState.Open;
            var tasks = new List<Task<BatchTaskResult[]>>();
            activityId = db.RegisterActvity(DataStoreActivityCategory.RunningTask, "Running tasks", 0);
            BatchTaskResult[] result = queue.ExecuteTasksAsync(10000, abort, activityId).Result;
            var ms = sw.Elapsed.TotalMilliseconds;
            if (result.Length == 0) return; // no tasks executed
            var failed = result.Count(r => r.Error != null);
            var taskTotalCount = result.Sum(r => r.TaskCount);
            var taskFailedCount = result.Where(r => r.Error != null).Sum(r => r.TaskCount);
            db.LogInfo("TaskQueue: " + taskTotalCount + " tasks in " + result.Length + " batches. " + ms.To1000N() + "ms total. " +
                (failed > 0 ? (taskFailedCount + " tasks in " + failed + " batches failed! ") : ""));
            foreach (var r in result) if (r.Error != null) db.LogError(r.TaskTypeName + " failed", r.Error!);
        } catch (Exception err) {
            db.LogError("TaskQueue: ", err);
        } finally {
            if (activityId > -1) db.DeRegisterActivity(activityId);
            oneThread.Reset();
        }
    }
    static void deleteExpiredTasksIfDue(IDataStore db, TaskQueue queue, ref DateTime lastDeleteExpiredTasks, TimeSpan intervalOfDeletingExpiredTasks) {
        if ((DateTime.UtcNow - lastDeleteExpiredTasks) < intervalOfDeletingExpiredTasks) return;
        lastDeleteExpiredTasks = DateTime.UtcNow;
        try {
            var sw = Stopwatch.StartNew();
            int deletedCount = queue.DeleteExpiredTasks();
            sw.Stop();
            if (deletedCount > 0) {
                db.LogInfo("Deleted " + deletedCount + " expired tasks in " + sw.ElapsedMilliseconds.To1000N() + "ms. ");
            }
        } catch (Exception err) {
            db.LogError("Deleting expired tasks failed: ", err);
        }
    }
    DateTime _lastAutoFlush = DateTime.UtcNow;
    void autoFlushDisk(object? state) {
        if (_autoFlushTaskRunningFlag.IsRunning_IfNotSetFlagToRunning()) return;
        try {
            if (_db.State != DataStoreState.Open) return;
            var now = DateTime.UtcNow;
            var lastTransaction = new DateTime(_db.Timestamp, DateTimeKind.Utc);
            var secondsSinceLastTransaction = (now - lastTransaction).TotalSeconds;
            var busy = secondsSinceLastTransaction < 0.5; // consider busy if there was a transaction in the last 500ms
            var secondsSinceLastAutoFlush = (now - _lastAutoFlush).TotalSeconds;
            if (busy) {
                if (secondsSinceLastAutoFlush < _s.MaxDelayAutoDiskFlushIfBusyInSeconds) {
                    //_db.Log("Auto disk flush delayed, database busy. ");
                    return; // too busy, delay
                } else {
                    _db.LogInfo("Auto disk flush forced. " + secondsSinceLastAutoFlush.To1000N() + "s since last auto flush. ");
                }
            } else {
                // _db.Log("Not busy, auto disk flush starting. ");
            }
            _lastAutoFlush = DateTime.UtcNow;
            var sw = Stopwatch.StartNew();
            var activityId = _db.RegisterActvity(DataStoreActivityCategory.Flushing, "Auto disk flush", 0);
            try {
                _db.FlushToDisk(true, activityId, out var t, out var a, out var w);
                if (t > 0) _db.LogInfo("Background disk flush. "
                    + sw.ElapsedMilliseconds.To1000N() + "ms, "
                    + t + " transaction" + (t != 1 ? "s" : "") + ", "
                    + a + " action" + (a != 1 ? "s" : "") + ", "
                    + w.ToByteString() + " written. ");
            } finally {
                _db.DeRegisterActivity(activityId);
            }
        } catch (Exception err) {
            _db.LogError("Auto disk flush failed: ", err);
        } finally {
            _autoFlushTaskRunningFlag.Reset();
        }
    }

    void backgroundTaskPulse(object? state) {
        if (_backgroundTaskRunningFlag.IsRunning_IfNotSetFlagToRunning()) return;
        try {
            if (_db.State != DataStoreState.Open) return;
            if (_s.AutoBackUp) runAutoBackup();
            if (_db.State != DataStoreState.Open) return;
            if (_s.AutoTruncate) runAutoTruncateIfDue();
            if (_db.State != DataStoreState.Open) return;
            if (_s.AutoSaveIndexStates) runAutoSaveIndexStatesIfDue();
            if (_db.State != DataStoreState.Open) return;
            if (_s.AutoPurgeCache) runAutoPurgeCache();
            if (_db.State != DataStoreState.Open) return;
            if (_db.Logger.LoggingAny) flushLoggerIfRunning();
        } catch (Exception err) {
            _db.LogError("Background task failed: ", err);
        } finally {
            _backgroundTaskRunningFlag.Reset();
        }
    }
    DateTime _lastQueryLogFlush = DateTime.UtcNow;
    DateTime _lastQueryLogMaintenance = DateTime.UtcNow;
    void flushLoggerIfRunning() {
        try {
            if ((DateTime.UtcNow - _lastQueryLogFlush).TotalSeconds < 30) {
                _db.Logger.FlushToDiskNow();
                _lastQueryLogFlush = DateTime.UtcNow;
            }
            if ((DateTime.UtcNow - _lastQueryLogMaintenance).TotalSeconds > 60) {
                _db.Logger.SaveStatsAndDeleteExpiredData();
                _lastQueryLogMaintenance = DateTime.UtcNow;
            }
        } catch (Exception err) {
            _db.LogError("Query log flush failed: ", err);
        }
    }
    void runAutoSaveIndexStatesIfDue() {
        var now = DateTime.UtcNow;
        try {
            if (!_s.AutoSaveIndexStates) return;
            if (_db._transactionActionActivity.EstimateLast10Seconds() > 1000) return; // too busy, delay            
            if (_db._queryActivity.EstimateLast10Seconds() > 10000) return; // too busy, delay            
            var noActionsNotInStateFile = _db.GetLogActionsNotItInStatefile();
            var belowLowerLimit = noActionsNotInStateFile < _s.AutoSaveIndexStatesActionCountLowerLimit;
            if (belowLowerLimit) {
                // _db.Log("Auto save index states not due yet, unsaved action count below lower limit. ");
                return;
            }
            var aboveUpperLimit = noActionsNotInStateFile > _s.AutoSaveIndexStatesActionCountUpperLimit;
            if (aboveUpperLimit == false) { // not above upper limit, base on time only:
                var belowTimeLimit = (now - _lastSaveIndexStates).TotalMinutes < _s.AutoSaveIndexStatesIntervalInMinutes;
                if (belowTimeLimit) {
                    // _db.Log("Auto save index states not due yet, based on time interval. ");
                    return;
                } else {
                    _db.LogInfo("Auto save index states due, time interval and lower unsaved action count limit exceeded. ");
                }
            }
            var sw = Stopwatch.StartNew();
            _db.LogInfo("Auto save index states started");
            _db.Maintenance(MaintenanceAction.SaveIndexStates);
            _db.LogInfo("Auto save index states finished in " + sw.ElapsedMilliseconds.To1000N() + "ms. ");
        } catch (Exception err) {
            _db.LogError("Auto save index states failed: ", err);
        }
        _lastSaveIndexStates = DateTime.UtcNow;
    }
    void runAutoTruncateIfDue() {
        var now = DateTime.UtcNow;
        try {
            if (!_s.AutoTruncate) return;
            if (_db._transactionActionActivity.EstimateLast10Seconds() > 1000) return; // too busy, delay
            if (_db._queryActivity.EstimateLast10Seconds() > 10000) return; // too busy, delay      
            var noActionsToBeTruncated = _db.GetNoPrimitiveActionsInLogThatCanBeTruncated();
            var belowLowerLimit = noActionsToBeTruncated < _s.AutoTruncateActionCountLowerLimit;
            if (belowLowerLimit) {
                // _db.Log("Truncate not due yet, unsaved action count below lower limit. ");
                return;
            }
            var belowTimeLimit = (now - _lastTruncate).TotalMinutes < _s.AutoTruncateIntervalInMinutes;
            if (belowTimeLimit) {
                // _db.Log("Truncate not due yet, based on time interval. ");
                return;
            } else {
                _db.LogInfo("Auto truncate due, time interval and lower unsaved action count limit exceeded. ");
            }
            var sw = Stopwatch.StartNew();
            _db.LogInfo("Auto truncate started");
            _db.Maintenance(MaintenanceAction.TruncateLog);
            _db.LogInfo("Auto truncate finished in " + sw.ElapsedMilliseconds.To1000N() + "ms. ");
            if (_s.AutoTruncateDeleteOldFileOnSuccess) {
                _db.LogInfo("Auto truncate delete old log files started");
                try {
                    _db.DeleteOldLogs();
                } catch (Exception err) {
                    _db.LogError("Auto truncate delete old log files failed: ", err);
                }
            }
        } catch (Exception err) {
            _db.LogError("Auto truncate failed: ", err);
        }
        _lastTruncate = DateTime.UtcNow;
    }
    void runAutoPurgeCache() {
        var now = DateTime.UtcNow;
        try {
            if ((now - _lastCachePurge).TotalMinutes < _s.AutoPurgeCacheIntervalInMinutes) return;
            long intialSize = _db._nodes.CacheSize + _db._definition.Sets.CacheSize;
            long lowerSizeLimit = (long)_s.AutoPurgeCacheLowerSizeLimitInMb * 1024L * 1024L;
            if (lowerSizeLimit < 1L) lowerSizeLimit = 1024 * 100; // 100KB
            if (intialSize < lowerSizeLimit) return;
            var sw = Stopwatch.StartNew();
            //_db.Maintenance(MaintenanceAction.PurgeCache | MaintenanceAction.CompressMemory | MaintenanceAction.GarbageCollect);
            _db.Maintenance(MaintenanceAction.PurgeCache);
            sw.Stop();
            long finalSize = _db._nodes.CacheSize + _db._definition.Sets.CacheSize;
            _db.LogInfo("Auto cache purge " + sw.ElapsedMilliseconds.To1000N() + "ms. "
                + intialSize.ToByteString() + " -> "
                + finalSize.ToByteString() + ". ");
        } catch (Exception err) {
            _db.LogError("Auto cache purge failed: ", err);
        }
        _lastCachePurge = DateTime.UtcNow;
    }

    void runAutoBackup() {
        var now = DateTime.UtcNow;
        if (now.Subtract(_lastAutoBackup).TotalMinutes < 1) return; // no need to check more than once per minute
        try {
            backupIfDue();
        } catch (Exception err) {
            _db.LogError("Backup failed: ", err);
        }
        try {
            deleteOlderBackupsIfDue();
        } catch (Exception err) {
            _db.LogError("Backup delete failed: ", err);
        }
        _lastAutoBackup = DateTime.UtcNow;
    }
    void backupIfDue() {
        var now = DateTime.UtcNow;
        var files = _db.FileKeys.WAL_GetAllBackUpFileKeys(_db.IO).Select(f => new { FileKey = f, Timestamp = _db.FileKeys.WAL_GetBackUpDateTimeFromFileKey(f) });
        var filesInCurrentHour = files.Where(f => f.Timestamp.Date == now.Date && f.Timestamp.Hour == now.Hour).Select(f => f.FileKey);
        if (filesInCurrentHour.Count() == 0) {
            var sw = Stopwatch.StartNew();
            var fileKey = _db.FileKeys.WAL_GetFileKeyForBackup(now, false);
            _db.Log(SystemLogEntryType.Backup, "Backup started: " + fileKey);
            if (_s.TruncateBackups) {
                _db.RewriteStore(false, fileKey, _db.IOBackup);
            } else {
                _db.CopyStore(fileKey, _db.IOBackup);
            }
            _db.Log(SystemLogEntryType.Backup, "Backup completed: " + fileKey + " in " + sw.ElapsedMilliseconds.To1000N() + "ms. ");
        }
    }
    void deleteOlderBackupsIfDue() {
        var now = DateTime.UtcNow;
        var files = _db.FileKeys.WAL_GetAllBackUpFileKeys(_db.IO).Select(f => new { FileKey = f, Timestamp = _db.FileKeys.WAL_GetBackUpDateTimeFromFileKey(f) });
        files = files.Where(f => _db.FileKeys.WAL_KeepForever(f.FileKey) == false); // do not delete files that are marked to keep forever
        HashSet<string> filesToKeep = new();

        // based on yearly backups, keep one per year:
        var isFileInYear = (DateTime file, DateTime now, int yearsAgo) => {
            var referenceDate = now.AddYears(-yearsAgo);
            return file.Year == referenceDate.Year;
        };
        for (int i = 0; i < _s.NoYearlyBackUps; i++) {
            var filesInYear = files.Where(f => isFileInYear(f.Timestamp, now, i)).Select(f => f.FileKey);
            var bestFileToKeep = filesInYear.OrderBy(f => f).FirstOrDefault();
            if (bestFileToKeep != null) filesToKeep.Add(bestFileToKeep);
        }

        // based on monthly backups, keep one per month:
        var isFileInMonth = (DateTime file, DateTime now, int monthsAgo) => {
            var referenceDate = now.AddMonths(-monthsAgo);
            return file.Year == referenceDate.Year && file.Month == referenceDate.Month;
        };
        for (int i = 0; i < _s.NoMontlyBackUps; i++) {
            var filesInMonth = files.Where(f => isFileInMonth(f.Timestamp, now, i)).Select(f => f.FileKey);
            var bestFileToKeep = filesInMonth.OrderBy(f => f).FirstOrDefault();
            if (bestFileToKeep != null) filesToKeep.Add(bestFileToKeep);
        }

        // based on weekly backups, keep one per week
        var isFileInWeek = (DateTime file, DateTime now, int weeksAgo) => {
            var referenceDate = now.AddDays(-7 * weeksAgo);
            var monday = referenceDate.Date.AddDays(-(int)referenceDate.DayOfWeek + 1);
            var nextMonday = monday.AddDays(7);
            return file.Date >= monday && file.Date < nextMonday;
        };
        for (int i = 0; i < _s.NoWeeklyBackUps; i++) {
            var filesInWeek = files.Where(f => isFileInWeek(f.Timestamp, now, i)).Select(f => f.FileKey);
            var bestFileToKeep = filesInWeek.OrderBy(f => f).FirstOrDefault();
            if (bestFileToKeep != null) filesToKeep.Add(bestFileToKeep);
        }
        // based on daily backups, keep one per day
        var isFileFromDay = (DateTime file, DateTime now, int daysAgo) => {
            return file.Date == now.Date.AddDays(-daysAgo);
        };
        for (int i = 0; i < _s.NoDailyBackUps; i++) {
            var filesInDay = files.Where(f => isFileFromDay(f.Timestamp, now, i)).Select(f => f.FileKey);
            var bestFileToKeep = filesInDay.OrderBy(f => f).FirstOrDefault();
            if (bestFileToKeep != null) filesToKeep.Add(bestFileToKeep);
        }
        // based on hourly backups, keep one per hour, not really needed, but just in case more than one backup per hour exists
        var isFileFromHour = (DateTime file, DateTime now, int hoursAgo) => {
            var referenceDate = now.AddHours(-hoursAgo);
            if (file.Date != referenceDate.Date) return false;
            return file.Hour == referenceDate.Hour;
        };
        for (int i = 0; i < _s.NoHourlyBackUps; i++) {
            var filesInHour = files.Where(f => isFileFromHour(f.Timestamp, now, i)).Select(f => f.FileKey);
            var bestFileToKeep = filesInHour.OrderBy(f => f).FirstOrDefault();
            if (bestFileToKeep != null) filesToKeep.Add(bestFileToKeep);
        }
        var filesToDelete = files.Select(f => f.FileKey).Except(filesToKeep); // the opoosite of keep
        foreach (var f in filesToDelete) {
            _db.LogInfo("Deleting: " + f);
            _db.IO.DeleteIfItExists(f);
        }
    }

    void recordMetrics(object? state) {
        try {
            if (_db.State != DataStoreState.Open) return;
            var metrics = _db.DequeMetrics();
            if (metrics != null) {
                _db.Logger.RecordMetrics(metrics);
            }
        } catch (Exception err) {
            _db.LogError("Recording metrics failed: ", err);
        }
    }

}
