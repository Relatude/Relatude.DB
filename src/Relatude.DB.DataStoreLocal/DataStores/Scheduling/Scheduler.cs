using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Relatude.DB.Common;
using Relatude.DB.IO;
using Relatude.DB.Logging;
using Relatude.DB.Tasks;

namespace Relatude.DB.DataStores.Scheduling;

internal class Scheduler {
    DataStoreLocal _db;
    SettingsLocal _s;
    Timer? _autoFlushTimer; // timer for auto flushing disk
    Timer? _taskDequeueTimer; // timer only for dequeuing index tasks
    Timer? _taskDequeuePersistedTimer; // timer only for dequeuing persisted index tasks
    Timer? _backgroundTaskTimer;  // general background grouping a number of background tasks ( auto state file save, backup, cache purge etc )
    Timer? _metricRecorder;  // timer for recording metrics, separated to be able to run more often and not be delayed by other background tasks
    Timer? _scheduleTimer; // timer for the schedules of the jobs, see Jobs

    OnlyOneThreadRunning _autoFlushTaskRunningFlag = new(); // flag to ensure only one thread is running auto flush at a time
    OnlyOneThreadRunning _dequeIndexTaskRunningFlag = new(); // flag to ensure only one thread is running index task dequeue at a time
    OnlyOneThreadRunning _dequeIndexTaskPersistedRunningFlag = new(); // flag to ensure only one thread is running index task dequeue at a time
    OnlyOneThreadRunning _backgroundTaskRunningFlag = new(); // flag to ensure only one thread is running background tasks at a time
    OnlyOneThreadRunning _scheduleRunningFlag = new(); // flag to ensure only one thread is looking at the schedules at a time

    DateTime _lastSaveIndexStates = DateTime.MinValue; // running at startup if above min action count
    DateTime _lastTruncate = DateTime.UtcNow; // do not run truncate at startup unless due
    DateTime _lastCachePurge = DateTime.UtcNow;
    DateTime _lastAutoBackup = DateTime.UtcNow;

    int startupDelayMs = 2000; // delay before starting any timer. allowing system to start up and initialize properly before background tasks start running
    int timerStartupDelta = 323; // delta between different timers, so they do not all run at exactly the same time
    int defaultAutoFlushPulseIntervalMs = 1000; // default interval for auto flushing disk, if no setting is provided
    int taskQueuePulseIntervalMs; // interval for checking for new tasks and the time allowed for building a batch of tasks
    int backgroundTasksPulseIntervalMs = 60000; // backup if due and delete old, cache purge, save log stats, flush logs etc. run every minute, not needed to run too often
    int metricRecorderPulseIntervalMs = 1000; // record metrics every second
    int schedulePulseIntervalMs = 1000; // the finest a schedule can be is a second, and a look at every schedule costs next to nothing
    TimeSpan _intervalOfDeletingExpiredTasks = TimeSpan.FromMinutes(5); // interval for running delete expired tasks, default is 5 minutes

    int taskQueueMaxRuntimeMs;
    int taskQueuePauseLimitDbActionsPerSec;

    int _throttle = -1; // the speed in force, 0-100
    // the setting (SettingsLocal.TaskQueueSpeed) as last applied: a change to it applies at once, while a
    // speed set at runtime only (IDataStore.TaskQueueThrottle) stays until the setting changes again
    int _appliedSpeedSetting = -1;
    readonly object _throttleLock = new();
    volatile bool _stopped; // set by Stop(), prevents in-flight timer callbacks from rescheduling disposed timers

    public Scheduler(DataStoreLocal db) {
        _db = db;
        _s = _db.Settings;
        applySpeedSettingIfChanged();
    }

    /// <summary>The speed in force, 0-100 - after taking up a change to the setting, so a speed just saved
    /// on the settings page reads back at once.</summary>
    public int GetTaskQueuesThrottle() {
        applySpeedSettingIfChanged();
        return _throttle;
    }
    // the setting decides at the start, and again whenever it changes; asked before every round
    void applySpeedSettingIfChanged() {
        var setting = _s.TaskQueueSpeed;
        if (setting == _appliedSpeedSetting) return;
        lock (_throttleLock) {
            if (setting == _appliedSpeedSetting) return;
            _appliedSpeedSetting = setting;
            ThrottleTaskQueue(setting);
        }
    }
    /// <summary>
    /// Sets the speed, 0-100, for as long as the database is open or until the setting changes. One knob over
    /// three numbers, each a straight line between its ends:
    /// how long a round may start batches (1 s at 0 to 10 s at 100: 5.5 s at 50, 9.1 s at 90), how long the
    /// queue waits between rounds (5.1 s at 0 to 0.1 s at 100: 2.6 s at 50, 0.6 s at 90), and how busy the
    /// database may be before a round is skipped - 4 × speed² write actions per second (any write at all at 0,
    /// 10,000/s at 50, 32,400/s at 90, 40,000/s at 100).
    /// </summary>
    public void ThrottleTaskQueue(int throttlePercentage) {
        lock (_throttleLock) {
            if (throttlePercentage < 0) throttlePercentage = 0;
            else if (throttlePercentage > 100) throttlePercentage = 100;
            if (_throttle == throttlePercentage) return;
            _throttle = throttlePercentage;
            double factor = _throttle / 100d; // 0 to 1
            taskQueueMaxRuntimeMs = (int)(10000d * (0.1d + 0.9d * factor)); // 1,000 ms at 0 to 10,000 ms at 100; 5,500 ms at 50
            taskQueuePauseLimitDbActionsPerSec = 4 * (int)Math.Pow(factor * 100d, 2d); // 0 at 0 to 40,000/s at 100; 10,000/s at 50
            taskQueuePulseIntervalMs = 100 + (int)(5000 * (1d - factor)); // 5,100 ms at 0 to 100 ms at 100; 2,600 ms at 50
            safeTimerChange(_taskDequeueTimer, taskQueuePulseIntervalMs);
            safeTimerChange(_taskDequeuePersistedTimer, taskQueuePulseIntervalMs);
        }
    }
    void safeTimerChange(Timer? timer, int dueTimeMs) {
        if (_stopped) return;
        try {
            timer?.Change(dueTimeMs, Timeout.Infinite);
        } catch (ObjectDisposedException) {
            // timer disposed by Stop() while a callback was in flight, safe to ignore
        }
    }
    //Timer _test = new Timer(_ => {
    //    Console.WriteLine("Transactions last 10 sec:" + _db._transactionActivity.EstimateLast10Seconds().To1000N() + ", Queries last 10 sec:" + _db._queryActivity.EstimateLast10Seconds().To1000N());
    //}, null, 10, 500); // just to have a timer for testing purposes

    public void Start() {

        _stopped = false;

        // avoid zero interval:
        if (_s.AutoSaveIndexStates && _s.AutoSaveIndexStatesIntervalInMinutes <= 0) _s.AutoSaveIndexStatesIntervalInMinutes = 30;
        if (_s.AutoTruncate && _s.AutoTruncateIntervalInMinutes <= 0) _s.AutoTruncateIntervalInMinutes = 24 * 60;

        // initiating flush timer:
        if (_s.AutoFlushDiskInBackground) {
            var flushIntervalMs = (int)(_s.AutoFlushDiskIntervalInSeconds * 1000); // fractions count: 0.5 is 500 ms, not 0
            if (flushIntervalMs <= 0) flushIntervalMs = defaultAutoFlushPulseIntervalMs; // default to 1 second if not set
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

        // the schedules run whether or not any are set: one added in the admin UI applies without a reopen
        _db.Jobs.Start();
        if (_db.TickSchedulesInBackground) _scheduleTimer = new Timer(tickSchedules, null, startupDelayMs + timerStartupDelta * 5, schedulePulseIntervalMs);

    }
    public void Stop() {
        _stopped = true;
        _autoFlushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _backgroundTaskTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _taskDequeueTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _taskDequeuePersistedTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _metricRecorder?.Change(Timeout.Infinite, Timeout.Infinite);
        _scheduleTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _autoFlushTimer?.Dispose();
        _backgroundTaskTimer?.Dispose();
        _taskDequeueTimer?.Dispose();
        _taskDequeuePersistedTimer?.Dispose();
        _metricRecorder?.Dispose();
        _scheduleTimer?.Dispose();
        _autoFlushTimer = null;
        _backgroundTaskTimer = null;
        _taskDequeueTimer = null;
        _taskDequeuePersistedTimer = null;
        _metricRecorder = null;
        _scheduleTimer = null;
        // tasks still running are asked to stop, and what the scheduler remembers is written
        try {
            _db.Jobs.Stop();
        } catch (Exception err) {
            _db.LogError("Stopping the scheduler failed: ", err);
        }
    }
    DateTime _lastDeleteExpiredTasks = DateTime.MinValue;
    DateTime _lastDeleteExpiredPersistedTasks = DateTime.MinValue;

    long _actionCountAtCompletionOfTaskDequeue;
    // async void: a timer callback returns nothing, and everything after the first await is inside the
    // try below, so nothing can escape it. The timer is stopped until the pulse is over, however long
    // the batches it started take
    async void dequeueTaskQueues(object? state) {
        if (_stopped || _db.State != DataStoreState.Open) return;
        try {
            applySpeedSettingIfChanged();
            _taskDequeueTimer?.Change(Timeout.Infinite, Timeout.Infinite); // stop it...
            var actionsSinceLastDequeue = _db.GetNoPrimitiveActionsSinceStartup() - _actionCountAtCompletionOfTaskDequeue;
            _actionCountAtCompletionOfTaskDequeue = _db.GetNoPrimitiveActionsSinceStartup();
            var actionsPerSecondSinceLastDequeue = actionsSinceLastDequeue * 1000 / taskQueuePulseIntervalMs;
            if (actionsPerSecondSinceLastDequeue > taskQueuePauseLimitDbActionsPerSec) {
                // postpone background tasks if db is busy
                _actionCountAtCompletionOfTaskDequeue = _db.GetNoPrimitiveActionsSinceStartup();
                return;
            }
            deleteExpiredTasksIfDue(_db, _db.TaskQueue, ref _lastDeleteExpiredTasks, _intervalOfDeletingExpiredTasks);
            await dequeueOneTaskQueueAsync(_db.TaskQueue, _dequeIndexTaskRunningFlag, _db, taskQueueMaxRuntimeMs);
            _actionCountAtCompletionOfTaskDequeue = _db.GetNoPrimitiveActionsSinceStartup();
        } catch (ObjectDisposedException) {
            // timer disposed by Stop() while callback was in flight, safe to ignore
        } catch (Exception err) {
            _db.LogError("Task queue dequeue failed: ", err);
        } finally {
            safeTimerChange(_taskDequeueTimer, taskQueuePulseIntervalMs); // start it again... to make sure intervals between runs are consistent
        }
    }
    long _actionCountAtCompletionOfPersistedTaskDequeue;
    async void dequeuePersistedTaskQueues(object? state) { // async void for the same reason as dequeueTaskQueues
        if (_stopped || _db.State != DataStoreState.Open) return;
        try {
            applySpeedSettingIfChanged();
            _taskDequeuePersistedTimer?.Change(Timeout.Infinite, Timeout.Infinite); // stop it...
            var actionsSinceLastDequeue = _db.GetNoPrimitiveActionsSinceStartup() - _actionCountAtCompletionOfPersistedTaskDequeue;
            _actionCountAtCompletionOfPersistedTaskDequeue = _db.GetNoPrimitiveActionsSinceStartup();
            var actionsPerSecondSinceLastDequeue = actionsSinceLastDequeue * 1000 / taskQueuePulseIntervalMs;
            if (actionsPerSecondSinceLastDequeue > taskQueuePauseLimitDbActionsPerSec) {
                // postpone background tasks if db is busy:
                _actionCountAtCompletionOfPersistedTaskDequeue = _db.GetNoPrimitiveActionsSinceStartup();
                return;
            }
            if (_db.TaskQueuePersisted != null) {  // it will be null if persisted queue is not enabled, ( all tasks will then be run by non persisted queue )
                deleteExpiredTasksIfDue(_db, _db.TaskQueuePersisted, ref _lastDeleteExpiredPersistedTasks, _intervalOfDeletingExpiredTasks);
                await dequeueOneTaskQueueAsync(_db.TaskQueuePersisted, _dequeIndexTaskPersistedRunningFlag, _db, taskQueueMaxRuntimeMs);
            }
            _actionCountAtCompletionOfPersistedTaskDequeue = _db.GetNoPrimitiveActionsSinceStartup();
        } catch (ObjectDisposedException) {
            // timer disposed by Stop() while callback was in flight, safe to ignore
        } catch (Exception err) {
            _db.LogError("Persisted task queue dequeue failed: ", err);
        } finally {
            safeTimerChange(_taskDequeuePersistedTimer, taskQueuePulseIntervalMs); // start it again... to make sure intervals between runs are consistent
        }
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
    // One round of a queue. It ends when its time is up even if batches it started are still running:
    // they carry on, and the next round - after the interval, and only when the database is not too
    // busy - starts others beside them. The queue lists its running batches under an activity of its
    // own, and logs each failed batch where it fails, so the round only sums up what finished in it.
    static async Task dequeueOneTaskQueueAsync(TaskQueue queue, OnlyOneThreadRunning oneThread, DataStoreLocal db, int maxRunTime) {
        if (oneThread.IsRunning_IfNotSetFlagToRunning()) return;
        try {
            if (db.State != DataStoreState.Open) return;
            if (queue.CountBatch(BatchState.Pending) == 0) return; // no tasks to execute
            Stopwatch sw = Stopwatch.StartNew();
            bool abort() => db.State != DataStoreState.Open;
            // awaited rather than blocked on: the batches run on threads of their own, several at once
            BatchTaskResult[] result = await queue.ExecuteTasksAsync(maxRunTime, abort);
            var ms = sw.Elapsed.TotalMilliseconds;
            if (result.Length == 0) return; // nothing finished within the round
            var failed = result.Count(r => r.Error != null);
            var taskTotalCount = result.Sum(r => r.TaskCount);
            var taskFailedCount = result.Where(r => r.Error != null).Sum(r => r.TaskCount);
            var logType = failed > 0 ? SystemLogEntryType.Error : SystemLogEntryType.Info;
            db.Log(logType, "TaskQueue: " + taskTotalCount + " tasks in " + result.Length + " batches. " + ms.To1000N() + "ms total. " +
                (failed > 0 ? (taskFailedCount + " tasks in " + failed + " batches failed! ") : ""));
        } catch (Exception err) {
            db.LogError("TaskQueue: ", err);
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
            var lastTransaction = new DateTime(_db.LastTimestampWithoutLock, DateTimeKind.Utc); // no lock: the read lock would queue behind any writer waiting
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
                _db.FlushToDisk(true, activityId, background: true, out var t, out var a, out var w); // never queues for the write lock behind a long query
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
            if (_s.AutoTruncate) runAutoTruncateIfDueAndNotTooBusy();
            if (_db.State != DataStoreState.Open) return;
            if (_s.AutoSaveIndexStates) runAutoSaveIndexStatesIfDueAndNotTooBusy();
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
    bool isTooBusy() {
        if (_db._transactionActionActivity.EstimateLast10Seconds() > _s.BusyThresholdActivitiesLast10Sec) return true;
        if (_db._queryActivity.EstimateLast10Seconds() > _s.BusyThresholdQueriesLast10Sec) return true;
        return false;
    }
    DateTime _lastQueryLogFlush = DateTime.UtcNow;
    DateTime _lastQueryLogMaintenance = DateTime.UtcNow;
    void flushLoggerIfRunning() {
        try {
            if ((DateTime.UtcNow - _lastQueryLogFlush).TotalSeconds >= 30) {
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
    void runAutoSaveIndexStatesIfDueAndNotTooBusy() {
        var now = DateTime.UtcNow;
        try {
            if (!_s.AutoSaveIndexStates) return;
            if (isTooBusy()) return;
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
            // put off while a query holds the read lock, and tried again on the next pulse - unless the
            // snapshot is far behind, when it waits for the lock like any writer
            var mustRun = aboveUpperLimit || (now - _lastSaveIndexStates).TotalMinutes >= 2 * _s.AutoSaveIndexStatesIntervalInMinutes;
            var sw = Stopwatch.StartNew();
            if (!_db.TrySaveIndexStatesInBackground(mustRun)) return; // busy: _lastSaveIndexStates stays, so the next pulse tries again
            _db.LogInfo("Auto save index states finished in " + sw.ElapsedMilliseconds.To1000N() + "ms. ");
        } catch (Exception err) {
            _db.LogError("Auto save index states failed: ", err);
        }
        _lastSaveIndexStates = DateTime.UtcNow;
    }
    void runAutoTruncateIfDueAndNotTooBusy() {
        var now = DateTime.UtcNow;
        try {
            if (!_s.AutoTruncate) return;
            if (isTooBusy()) return;
            var noActionsToBeTruncated = _db.GetNoPrimitiveActionsInLogThatCanBeTruncated();
            var noActionsInLog = _db.GetNoPrimitiveActionsInLog();
            // the truncatable count is the removes, and each takes the add before it along: a truncation drops twice as many
            var redundantPercentage = noActionsInLog > 0 ? Math.Min(100, 200d * noActionsToBeTruncated / noActionsInLog) : 0;
            var countLimitReached = noActionsToBeTruncated >= _s.AutoTruncateActionCountLowerLimit;
            var percentageLimitReached = _s.AutoTruncateRedundantPercentageLimit > 0 && noActionsToBeTruncated > 0
                && redundantPercentage >= _s.AutoTruncateRedundantPercentageLimit;
            if (!countLimitReached && !percentageLimitReached) {
                // _db.Log("Truncate not due yet, below both the truncatable action count and the redundant percentage limit. ");
                return;
            }
            var belowTimeLimit = (now - _lastTruncate).TotalMinutes < _s.AutoTruncateIntervalInMinutes;
            if (belowTimeLimit) {
                // _db.Log("Truncate not due yet, based on time interval. ");
                return;
            } else {
                _db.LogInfo("Auto truncate due, time interval and " + (countLimitReached ? "truncatable action count" : "redundant percentage")
                    + " limit exceeded: " + noActionsToBeTruncated.To1000N() + " truncatable of " + noActionsInLog.To1000N()
                    + " actions in the log, " + redundantPercentage.ToString("0") + "% redundant. ");
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
            // only when the write lock is free: a purge is never urgent, so a busy store is left alone and the
            // next pulse, a minute on, tries again
            if (!_db.TryPurgeCachesInBackground()) return;
            _db.TrySaveIndexCachesInBackground();
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
            backupIfDue(now);
        } catch (Exception err) {
            _db.LogError("Backup failed: ", err);
        }
        try {
            DeleteOlderBackups();
        } catch (Exception err) {
            _db.LogError("Backup delete failed: ", err);
        }
        _lastAutoBackup = DateTime.UtcNow;
    }
    /// <summary>One pass of the automatic backup, without waiting for the minute between checks. For the tests,
    /// which can say what time it is.</summary>
    internal void RunAutoBackupPass(DateTime? now = null) {
        backupIfDue(now ?? DateTime.UtcNow);
        DeleteOlderBackups(now);
    }

    // The generations of the retention settings, finest first. A generation keeps the FIRST backup of each of
    // its last N periods (N = its copies kept), and both the automatic backup and the retention pass go by
    // these same period boundaries - see periodStart.
    enum BackupGeneration { Hourly, Daily, Weekly, Monthly, Yearly }
    int copiesKept(BackupGeneration generation) => generation switch {
        BackupGeneration.Hourly => _s.NoHourlyBackUps,
        BackupGeneration.Daily => _s.NoDailyBackUps,
        BackupGeneration.Weekly => _s.NoWeeklyBackUps,
        BackupGeneration.Monthly => _s.NoMonthlyBackUps,
        _ => _s.NoYearlyBackUps,
    };
    // the start of the hour, day, week (Monday), month or year a moment falls in
    static DateTime periodStart(DateTime t, BackupGeneration generation) => generation switch {
        BackupGeneration.Hourly => new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Kind),
        BackupGeneration.Daily => t.Date,
        BackupGeneration.Weekly => t.Date.AddDays(-(((int)t.DayOfWeek + 6) % 7)), // days since Monday, Sunday (0) => 6 days back
        BackupGeneration.Monthly => new DateTime(t.Year, t.Month, 1, 0, 0, 0, t.Kind),
        _ => new DateTime(t.Year, 1, 1, 0, 0, 0, t.Kind),
    };
    static DateTime periodsAgo(DateTime now, BackupGeneration generation, int count) => generation switch {
        BackupGeneration.Hourly => now.AddHours(-count),
        BackupGeneration.Daily => now.AddDays(-count),
        BackupGeneration.Weekly => now.AddDays(-7 * count),
        BackupGeneration.Monthly => now.AddMonths(-count),
        _ => now.AddYears(-count),
    };
    /// <summary>
    /// How often the automatic backup runs: once in each period of the finest generation that keeps any
    /// copies. Every hour while "Hourly copies kept" is above 0. With it at 0 there are no hourly backups at
    /// all, only one a day - or one a week, a month or a year when the daily, weekly and monthly copies kept
    /// are 0 as well. Null, no automatic backup at all, when every generation keeps 0.
    /// A backup taken more often than the finest generation keeps would be deleted by the next retention
    /// pass, a minute later, and its period would then have none again: a full backup written and deleted
    /// every other minute.
    /// </summary>
    BackupGeneration? backupCadence() {
        foreach (var generation in Enum.GetValues<BackupGeneration>()) {
            if (copiesKept(generation) > 0) return generation;
        }
        return null;
    }
    // the backups are looked for, written and pruned in the backup provider (IoBackup), which falls
    // back to the database's own provider when none is configured
    void backupIfDue(DateTime now) {
        if (backupCadence() is not BackupGeneration cadence) return; // every generation keeps 0: no automatic backups
        var period = periodStart(now, cadence);
        // any backup in this period counts, one taken by hand or marked keep included
        var hasOne = FileKeyUtility.WAL_GetAllBackUpFileKeys(_db.IOBackup).Any(f => periodStart(FileKeyUtility.WAL_GetBackUpDateTimeFromFileKey(f), cadence) == period);
        if (hasOne) return;
        // a backup has no file until it starts, and the queue holds one back while the database is busy or a long
        // batch is ahead of it: one already waiting is enough, or every pass would queue another meanwhile
        if (autoBackupQueued()) return;
        var task = new RewriteTask() {
            HotSwapToNewFile = false,
            DeleteOldDbFilesAfterHotSwap = false,
            NewLogFileKey = FileKeyUtility.WAL_GetFileKeyForBackup(now, false),
            IO = _db.IOBackup,
            Truncate = _s.TruncateBackups,
            IsBackup = true
        };
        _db.EnqueueTask(task, autoBackupGroupKey);
    }
    // the automatic backup's batches in the task queue (a backup is not persisted, so always the memory queue)
    const string autoBackupGroupKey = "AutoBackup";
    bool autoBackupQueued() {
        _db.TaskQueue.GetBatchMeta([BatchState.Pending, BatchState.Waiting, BatchState.Running], [], [autoBackupGroupKey], 0, 1, out var count);
        return count > 0;
    }
    /// <summary>Deletes the backups the retention settings do not keep: of each generation (hourly, daily,
    /// weekly, monthly and yearly copies kept) the first backup of each of its last N periods is kept, N being
    /// the copies kept, so a generation at 0 keeps nothing - and with every generation at 0 every backup goes.
    /// One marked keep is never deleted, nor is one still being written. Returns how many it deleted. Also the
    /// built-in named task "delete-old-backups".</summary>
    internal int DeleteOlderBackups(DateTime? at = null) {
        var now = at ?? DateTime.UtcNow;
        // keys are handled in their joined form here, so they can live in hash sets and set operations
        var files = FileKeyUtility.WAL_GetAllBackUpFileKeys(_db.IOBackup)
            .Where(f => FileKeyUtility.WAL_KeepForever(f) == false) // do not delete files that are marked to keep forever
            .Select(f => new { FileKey = f.AsKeyString(), Timestamp = FileKeyUtility.WAL_GetBackUpDateTimeFromFileKey(f) })
            .ToArray();
        HashSet<string> filesToKeep = new();
        // asked after the listing: a backup that starts after it is not in the list, and one that finished in
        // between is a whole file the rules below can decide about
        if (_db.FileKeyBeingWritten is string[] writing) filesToKeep.Add(writing.AsKeyString());
        foreach (var generation in Enum.GetValues<BackupGeneration>()) {
            for (int i = 0; i < copiesKept(generation); i++) {
                var period = periodStart(periodsAgo(now, generation, i), generation);
                var first = files.Where(f => periodStart(f.Timestamp, generation) == period).Select(f => f.FileKey).OrderBy(f => f).FirstOrDefault();
                if (first != null) filesToKeep.Add(first);
            }
        }
        var filesToDelete = files.Select(f => f.FileKey).Except(filesToKeep).ToArray(); // the opposite of keep
        foreach (var f in filesToDelete) {
            _db.LogInfo("Deleting: " + f);
            _db.IOBackup.DeleteFileIfItExists(f.SplitKey());
        }
        return filesToDelete.Length;
    }

    void tickSchedules(object? state) {
        if (_stopped || _db.State != DataStoreState.Open) return;
        if (_scheduleRunningFlag.IsRunning_IfNotSetFlagToRunning()) return;
        try {
            _db.Jobs.Tick(DateTime.UtcNow);
        } catch (Exception err) {
            _db.LogError("Scheduler failed: ", err);
        } finally {
            _scheduleRunningFlag.Reset();
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
