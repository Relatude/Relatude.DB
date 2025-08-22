using System.Diagnostics;
using WAF.Common;
using WAF.Tasks;

namespace WAF.DataStores.Scheduling;
internal class Scheduler(DataStoreLocal _db) {
    SettingsLocal _s => _db.Settings;
    Timer? _autoFlushTimer; // timer for auto flushing disk
    Timer? _taskDequeueTimer; // timer only for dequeuing index tasks
    Timer? _taskDequeuePersistedTimer; // timer only for dequeuing index tasks
    Timer? _backgroundTaskTimer;  // general background grouping a number of background tasks ( backup, cache purge etc )

    OnlyOneThreadRunning _autoFlushTaskRunningFlag = new(); // flag to ensure only one thread is running auto flush at a time
    OnlyOneThreadRunning _dequeIndexTaskRunnningFlag = new(); // flag to ensure only one thread is running index task dequeue at a time
    OnlyOneThreadRunning _dequeIndexTaskPersistedRunnningFlag = new(); // flag to ensure only one thread is running index task dequeue at a time
    OnlyOneThreadRunning _backgroundTaskRunningFlag = new(); // flag to ensure only one thread is running background tasks at a time

    DateTime _lastSaveIndexStates = DateTime.UtcNow;
    DateTime _lastTruncate = DateTime.UtcNow;
    DateTime _lastCachePurge = DateTime.UtcNow;
    DateTime _lastAutoBackup = DateTime.UtcNow;

    int startupDelayMs = 2000; // delay before starting any timer. allowing system to start up and initialize properly before background tasks start running
    int timerStartupDelta = 323; // delta between different timers, so they do not all run at exactly the same time
    int defaultAutoFlushPulseIntervalMs = 1000; // default interval for auto flushing disk, if no setting is provided
    int taskQueuePulseIntervalMs = 1000; // default interval for checking for new tasks and the time allowed for building a batch of tasks
    int backgroundTasksPulseIntervalMs = 60000; // backup, cache purge etc. run every minute, not needed to run too often
    TimeSpan _intervalOfDeletingExpiredTasks = TimeSpan.FromMinutes(5); // interval for running delete expired tasks, default is 5 minutes

    public void Start() {
        // avoid zero interval:
        if (_s.AutoSaveIndexStates && _s.AutoSaveIndexStatesIntervalInMinutes <= 0) _s.AutoSaveIndexStatesIntervalInMinutes = 45;
        if (_s.AutoTruncate && _s.AutoTruncateIntervalInMinutes <= 0) _s.AutoTruncateIntervalInMinutes = 24 * 60;

        // initating flush timer:
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
            _backgroundTaskTimer = new Timer(backgroundTaskPuls, null, startupDelayMs + timerStartupDelta * 3, backgroundTasksPulseIntervalMs);
        }

    }
    public void Stop() {
        _autoFlushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _backgroundTaskTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _taskDequeueTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _taskDequeuePersistedTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _autoFlushTimer?.Dispose();
        _backgroundTaskTimer?.Dispose();
        _taskDequeueTimer?.Dispose();
        _taskDequeuePersistedTimer?.Dispose();
        _autoFlushTimer = null;
        _backgroundTaskTimer = null;
        _taskDequeueTimer = null;
        _taskDequeuePersistedTimer = null;
    }
    DateTime _lastDeleteExpiredTasks = DateTime.MinValue;
    DateTime _lastDeleteExpiredPersistedTasks = DateTime.MinValue;

    void dequeueTaskQueues(object? state) {
        if (_db.State != DataStoreState.Open) return;
        deleteExpiredTasksIfDue(_db, _db.TaskQueue, ref _lastDeleteExpiredTasks, _intervalOfDeletingExpiredTasks);
        dequeueOneTaskQueue(_db.TaskQueue, _dequeIndexTaskRunnningFlag, _db);
    }
    void dequeuePersistedTaskQueues(object? state) {
        if (_db.State != DataStoreState.Open) return;
        if (_db.TaskQueuePersisted != null) {
            deleteExpiredTasksIfDue(_db, _db.TaskQueuePersisted, ref _lastDeleteExpiredPersistedTasks, _intervalOfDeletingExpiredTasks);
            dequeueOneTaskQueue(_db.TaskQueuePersisted, _dequeIndexTaskPersistedRunnningFlag, _db);
        }
    }

    static void initTaskQueue(IDataStore db, TaskQueue queue, string name) {
        var a = queue.BatchCountsPerState();
        if (a.Length == 0) {
            db.Log(name + " queue initiated.");
        } else {
            db.Log(name + " queue initiated with:");
            for (int i = 0; i < a.Length; i++) {
                var kv = a[i];
                db.Log(" - " + kv.Value + " " + kv.Key.ToString().Decamelize().ToLower());
            }
        }
        queue.RestartTasksFromDbShutdown(out int restaredBatches, out int abortedBatches, out int restaredTasks, out int abortedTasks);
        if (restaredBatches > 0) db.Log("   -> " + restaredBatches + " batches with " + restaredTasks + " tasks restarted after shutdown");
        if (abortedBatches > 0) db.Log("   -> " + abortedBatches + " batches with " + abortedTasks + " tasks aborted due to shutdown");
    }
    static void dequeueOneTaskQueue(TaskQueue queue, OnlyOneThreadRunning oneThread, IDataStore db) {
        if (oneThread.IsRunning_IfNotFlagToRunning()) return;
        try {
            if (db.State != DataStoreState.Open) return;
            if (queue.CountBatch(BatchState.Pending) == 0) return; // no tasks to execute            
            Stopwatch sw = Stopwatch.StartNew();

            bool abort() => db.State != DataStoreState.Open;
            var tasks = new List<Task<BatchTaskResult[]>>();

            BatchTaskResult[] result = queue.ExecuteTasksAsync(10000, abort).Result;

            var ms = sw.Elapsed.TotalMilliseconds;
            if (result.Length == 0) return; // no tasks executed
            var failed = result.Count(r => r.Error != null);
            var taskTotalCount = result.Sum(r => r.TaskCount);
            var taskFailedCount = result.Where(r => r.Error != null).Sum(r => r.TaskCount);
            db.Log("Dequeued " + taskTotalCount + " tasks in " + result.Length + " batches. " + ms.To1000N() + "ms total. " +
                (failed > 0 ? (taskFailedCount + " tasks in " + failed + " batches failed! ") : ""));
            foreach (var r in result) if (r.Error != null) db.LogError(r.TaskTypeName + " failed", r.Error!);
        } catch (Exception err) {
            db.LogError("Dequeuing index tasks failed: ", err);
        } finally {
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
                db.Log("Deleted " + deletedCount + " expired tasks in " + sw.ElapsedMilliseconds.To1000N() + "ms. ");
            }
        } catch (Exception err) {
            db.LogError("Deleting expired tasks failed: ", err);
        }
    }
    void autoFlushDisk(object? state) {
        if (_autoFlushTaskRunningFlag.IsRunning_IfNotFlagToRunning()) return;
        try {
            if (_db.State != DataStoreState.Open) return;
            var now = DateTime.UtcNow;
            var sw = Stopwatch.StartNew();
            _db.FlushToDisk(out var t, out var a, out var w);
            if (t > 0) _db.Log("Background disk flush. "
                + sw.ElapsedMilliseconds.To1000N() + "ms, "
                + t + " transaction" + (t != 1 ? "s" : "") + ", "
                + a + " action" + (a != 1 ? "s" : "") + ", "
                + w.ToByteString() + " written. ");
        } catch (Exception err) {
            _db.LogError("Auto disk flush failed: ", err);
        } finally {
            _autoFlushTaskRunningFlag.Reset();
        }
    }

    void backgroundTaskPuls(object? state) {
        if (_backgroundTaskRunningFlag.IsRunning_IfNotFlagToRunning()) return;
        try {
            if (_db.State != DataStoreState.Open) return;
            if (_s.AutoSaveIndexStates) runAutoSaveIndexStatesIfDue();
            if (_db.State != DataStoreState.Open) return;
            if (_s.AutoBackUp) runAutoBackup();
            if (_db.State != DataStoreState.Open) return;
            if (_s.AutoTruncate) runAutoTruncateIfDue();
            if (_db.State != DataStoreState.Open) return;
            if (_s.AutoPurgeCache) runAutoPurgeCache();
            if (_db.State != DataStoreState.Open) return;
            if (_db.QueryLogger.Enabled) flushQueryLogIfRunning();
        } catch (Exception err) {
            _db.LogError("Background task failed: ", err);
        } finally {
            _backgroundTaskRunningFlag.Reset();
        }
    }
    DateTime _lastQueryLogFlush = DateTime.UtcNow;
    DateTime _lastQueryLogMaintenance = DateTime.UtcNow;
    void flushQueryLogIfRunning() {
        try {
            if ((DateTime.UtcNow - _lastQueryLogFlush).TotalSeconds < 30) {
                _db.QueryLogger.Flush();
                _lastQueryLogFlush = DateTime.UtcNow;
            }
            if ((DateTime.UtcNow - _lastQueryLogMaintenance).TotalMinutes > 5) {
                _db.QueryLogger.Maintenance();
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
            if ((now - _lastSaveIndexStates).TotalMinutes < _s.AutoSaveIndexStatesIntervalInMinutes) return;
            var sw = Stopwatch.StartNew();
            _db.Log("Auto save index states started");
            _db.Maintenance(MaintenanceAction.SaveIndexStates);
            _db.Log("Auto save index states finished in " + sw.ElapsedMilliseconds.To1000N() + "ms. ");
        } catch (Exception err) {
            _db.LogError("Auto save index states failed: ", err);
        }
        _lastSaveIndexStates = DateTime.UtcNow;
    }
    void runAutoTruncateIfDue() {
        if (!_db.IsThereAnythingToTruncate()) return;
        var now = DateTime.UtcNow;
        try {
            if ((now - _lastTruncate).TotalMinutes < _s.AutoTruncateIntervalInMinutes) return;
            var sw = Stopwatch.StartNew();
            _db.Log("Auto truncate started");
            _db.Maintenance(MaintenanceAction.TruncateLog);
            _db.Log("Auto truncate finished in " + sw.ElapsedMilliseconds.To1000N() + "ms. ");
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
            _db.Log("Auto cache purge " + sw.ElapsedMilliseconds.To1000N() + "ms. "
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
        var files = _db.FileKeys.Log_GetAllBackUpFileKeys(_db.IO).Select(f => new { FileKey = f, Timestamp = _db.FileKeys.Log_GetBackUpDateTimeFromFileKey(f) });
        var filesInCurrentHour = files.Where(f => f.Timestamp.Date == now.Date && f.Timestamp.Hour == now.Hour).Select(f => f.FileKey);
        if (filesInCurrentHour.Count() == 0) {
            var sw = Stopwatch.StartNew();
            var fileKey = _db.FileKeys.Log_GetFileKeyForBackup(now, false);
            _db.Log("Backup started: " + fileKey);
            if (_s.TruncateBackups) {
                _db.RewriteStore(false, fileKey, _db.IOBackup);
            } else {
                _db.CopyStore(fileKey, _db.IOBackup);
            }
            _db.Log("Backup finished: " + fileKey + " in " + sw.ElapsedMilliseconds.To1000N() + "ms. ");
        }
    }
    void deleteOlderBackupsIfDue() {
        var now = DateTime.UtcNow;
        var files = _db.FileKeys.Log_GetAllBackUpFileKeys(_db.IO).Select(f => new { FileKey = f, Timestamp = _db.FileKeys.Log_GetBackUpDateTimeFromFileKey(f) });
        files = files.Where(f => _db.FileKeys.Log_KeepForever(f.FileKey) == false); // do not delete files that are marked to keep forever
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
            _db.Log("Deleting: " + f);
            _db.IO.DeleteIfItExists(f);
        }
    }
}
