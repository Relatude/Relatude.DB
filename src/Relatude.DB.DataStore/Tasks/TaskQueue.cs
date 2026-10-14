using Relatude.DB.Common;
using Relatude.DB.DataStores;
using System.Collections;
using System.Diagnostics;
using System.Text;

namespace Relatude.DB.Tasks;

/// <summary>
/// One of a database's two task queues: what has been asked for, in the store behind it, and the
/// batches a runner is working on right now.
///
/// Batches of different types run side by side, and so may batches of one type, up to what the type
/// allows (<see cref="ConcurrencyOf"/>). A batch is reserved under the lock and run outside it, and
/// nothing is called on the database while the lock is held: a transaction queues its tasks while it
/// holds the database's write lock, so the order of the two locks is always database first.
///
/// <para>Batches are started in rounds (<see cref="ExecuteTasksAsync"/>), which is where the throttle
/// and the database's busy check come in, but a batch is not bound to its round: one still running
/// when the round's time is up carries on, holding only its own slot, and the next round starts others
/// beside it. So a batch that runs for an hour never stops the rest of the queue from moving.</para>
/// </summary>
public class TaskQueue : IDisposable {
    /// <summary>The most batches of one type a queue runs at once, whatever a runner or a setting asks for.</summary>
    public const int MaxSupportedConcurrency = 64;
    /// <param name="store"></param>
    /// <param name="queue"></param>
    /// <param name="runners"></param>    /// <param name="configuredConcurrency">What the settings say for a type, see <see cref="ConcurrencyOf"/>; null when they say nothing.</param>
    /// <param name="name">What the queue is called among the database's activities, "in memory" say; null leaves it unnamed.</param>
    public TaskQueue(IDataStore store, IQueueStore queue, Dictionary<string, ITaskRunner> runners, Func<ITaskRunner, int?>? configuredConcurrency = null, string? name = null) {
        _store = store;
        _queue = queue;
        _runners = runners;
        _configuredConcurrency = configuredConcurrency;
        _activityName = name == null ? "Running tasks" : "Running tasks, " + name;
    }
    readonly IDataStore _store;
    readonly IQueueStore _queue;
    readonly Dictionary<string, ITaskRunner> _runners;
    readonly Func<ITaskRunner, int?>? _configuredConcurrency;
    readonly Dictionary<string, IBatch> _batchBufferByTypeAndGroupKey = [];
    readonly object _lock = new();
    // the batches handed to a runner and not finished with yet, and the concurrency keys they hold per
    // type (a count per key: one batch may hold a key twice). Both guarded by _lock
    readonly Dictionary<Guid, RunningBatch> _running = [];
    readonly Dictionary<string, Dictionary<string, int>> _runningKeys = [];
    volatile bool _isShuttingdown = false;
    int _executing = 0; // calls to ExecuteTasksAsync in progress: the rounds that may still start batches
    int _inFlight = 0; // batches handed to a runner and not finished with, whichever round started them
    // the queue's own activity, the one its running batches are listed under: there while any batch
    // runs, and not tied to a round, since a batch may outlive the round that started it
    readonly string _activityName;
    static readonly AsyncLocal<long> _currentBatchActivity = new();
    /// <summary>
    /// The activity of the batch a runner is working on, read from inside the runner: a runner can show
    /// its progress on it with <see cref="IDataStore.UpdateActivity"/>. 0 outside a batch.
    /// </summary>
    public static long CurrentBatchActivityId => _currentBatchActivity.Value;
    readonly object _activitySync = new();
    long _activityId = -1;
    int _activityUsers = 0;
    RunningZeroEstimator runningZeroEstimator = new(10, TimeSpan.FromMinutes(10));

    /// <summary>A batch a runner is working on.</summary>
    sealed class RunningBatch(IBatch batch, string[] keys) {
        public IBatch Batch { get; } = batch;
        /// <summary>The concurrency keys of its tasks: no other batch of its type holding one of them starts meanwhile.</summary>
        public string[] Keys { get; } = keys;
        /// <summary>Put back in line while it ran: once it is done it stays pending, and runs again.</summary>
        public bool RunAgain { get; set; }
    }

    void emptyBuffer() {
        if (_batchBufferByTypeAndGroupKey.Count > 0) { // move all batches to the queues
            foreach (var kvp in _batchBufferByTypeAndGroupKey) {
                _queue.Enqueue(kvp.Value, _runners[kvp.Value.Meta.TaskTypeId]);
            }
            _batchBufferByTypeAndGroupKey.Clear();
        }
    }
    public string QueueStoreTypeName => _queue.GetType().Name.ToString();
    /// <summary>The runners registered for this queue: what kinds of task it can hold, and how each behaves.</summary>
    public IEnumerable<ITaskRunner> Runners => _runners.Values;
    /// <summary>
    /// How many batches of the runner's type this queue runs at the same time: what the settings say
    /// for the type when they say anything, and otherwise what the runner asks for
    /// (<see cref="ITaskRunner.MaxConcurrency"/>) - never below one, and never above the runner's own
    /// limit or <see cref="MaxSupportedConcurrency"/>. Asked every time a batch is about to start, so a
    /// changed setting applies to the next one.
    /// </summary>
    public int ConcurrencyOf(ITaskRunner runner) {
        var wanted = ConfiguredConcurrencyOf(runner) ?? runner.MaxConcurrency;
        return Math.Clamp(wanted, 1, ConcurrencyLimitOf(runner));
    }
    /// <summary>What the settings say for the runner's type, before any limit is applied; null when they leave it to the runner.</summary>
    public int? ConfiguredConcurrencyOf(ITaskRunner runner) => _configuredConcurrency?.Invoke(runner);
    /// <summary>The most batches of the runner's type that can ever run at once here.</summary>
    public static int ConcurrencyLimitOf(ITaskRunner runner) => Math.Clamp(runner.MaxConcurrencyLimit, 1, MaxSupportedConcurrency);
    /// <summary>How many batches of the type a runner is working on right now.</summary>
    public int RunningCount(string taskTypeId) {
        lock (_lock) {
            var count = 0;
            foreach (var running in _running.Values) if (running.Batch.Meta.TaskTypeId == taskTypeId) count++;
            return count;
        }
    }
    public int CountBatch(BatchState state) {
        lock (_lock) {
            emptyBuffer();
            return _queue.CountBatch(state);
        }
    }
    public bool AnyPendingOrRunning() {
        lock (_lock) {
            emptyBuffer();
            return _queue.AnyPendingOrRunning();
        }
    }
    public int CountTasks(BatchState state) {
        lock (_lock) {
            emptyBuffer();
            return _queue.CountTasks(state);
        }
    }
    public KeyValuePair<BatchState, int>[] BatchCountsPerState() {
        lock (_lock) {
            emptyBuffer();
            return _queue.BatchCountsPerState();
        }
    }
    public int Count(BatchState[] states) {
        lock (_lock) {
            emptyBuffer();
            return states.Sum(_queue.CountTasks);
        }
    }
    public KeyValuePair<BatchState, int>[] TaskCountsPerState() {
        lock (_lock) {
            emptyBuffer();
            return _queue.TaskCountsPerState();
        }
    }
    public void Enqueue(TaskData task, string? groupKey = null) {
        Enqueue(task, groupKey, BatchState.Pending);
    }
    public void Enqueue(TaskData task, string? groupKey, BatchState state) {
        lock (_lock) {
            var typeId = task.TaskTypeId;
            if (!_runners.TryGetValue(typeId, out var runner)) throw new Exception("No runner for " + typeId);
            var batchKey = typeId + groupKey;
            if (_batchBufferByTypeAndGroupKey.TryGetValue(batchKey, out var batch)) {
                if (batch.TaskCount < runner.MaxTaskCountPerBatch) {
                    batch.AddTask(task);
                } else {
                    _queue.Enqueue(batch, runner);
                    _batchBufferByTypeAndGroupKey[batchKey] = runner.CreateBatchWithOneTask(task, state, groupKey);
                }
            } else {
                _batchBufferByTypeAndGroupKey.Add(batchKey, runner.CreateBatchWithOneTask(task, state, groupKey));
            }
        }
    }
    DateTime _lastReportedCount = DateTime.MinValue;
    /// <summary>
    /// One round: starts batches until the time is up or nothing more can start, as many side by side
    /// as their types allow, refilling a slot as soon as a batch in it is done. Priority still decides:
    /// of the batches that can start the one with the highest priority goes first, then the oldest. A
    /// batch waits while its type already runs as many as it may - counting batches of earlier rounds
    /// still running - or while a running batch of its type holds one of its keys.
    /// <para>Returns when nothing it started is running and nothing more can start, or when the time is
    /// up. Batches still running then carry on, and the next round starts batches beside them; each one
    /// that outlives its round logs its own end. The results are those of the batches that finished
    /// within the round, so a caller that wants everything done gives the round time enough.</para>
    /// </summary>
    public async Task<BatchTaskResult[]> ExecuteTasksAsync(int maxDurationMs, Func<bool> abort) {
        // counted before the shutdown flag is read, and the shutdown sets the flag before it reads the
        // count, so one of the two always sees the other
        Interlocked.Increment(ref _executing);
        try {
            if (_isShuttingdown) return [];
            return await dispatch(maxDurationMs, abort);
        } finally {
            Interlocked.Decrement(ref _executing);
        }
    }
    async Task<BatchTaskResult[]> dispatch(int maxDurationMs, Func<bool> abort) {
        var results = new List<BatchTaskResult>();
        var running = new List<Task<BatchTaskResult>>();
        using var endOfRound = new CancellationTokenSource();
        var timeUp = Task.Delay(Math.Max(0, maxDurationMs), endOfRound.Token);
        Exception? startError = null;
        try {
            while (true) {
                reportPendingCountIfDue();
                // fill every free slot, until the time is up or nothing more can start right now
                while (!_isShuttingdown && !abort() && !timeUp.IsCompleted) {
                    IBatch? next;
                    try {
                        next = startNext();
                    } catch (Exception err) {
                        // a store that cannot hand out a batch: nothing more is started this round, and
                        // what already runs carries on as it does at the end of any round
                        startError = err;
                        break;
                    }
                    if (next == null) break;
                    // counted while this round is: a shutdown that has seen the rounds end sees every
                    // batch they started
                    Interlocked.Increment(ref _inFlight);
                    // on a thread of its own: a runner may do all its work before its first await, and it
                    // would otherwise hold up every slot behind it
                    running.Add(Task.Run(() => executeAsync(next)));
                }
                if (running.Count == 0 || startError != null || timeUp.IsCompleted) break;
                // a batch done frees its slot for the next one; the time running out ends the round
                var done = await Task.WhenAny([.. running, timeUp]);
                if (done == timeUp) break;
                var finished = (Task<BatchTaskResult>)done;
                running.Remove(finished);
                results.Add(await finished);
            }
        } finally {
            endOfRound.Cancel();
            // what the round leaves running finishes on its own, holding its slot until then
            foreach (var batch in running) _ = batch.ContinueWith(finishedAfterItsRound, TaskScheduler.Default);
        }
        if (startError != null) throw startError;
        lock (_lock) {
            emptyBuffer(); // what is still in the buffer is pending too
            if (_queue.CountTasks(BatchState.Pending) == 0 && _running.Count == 0) runningZeroEstimator.ReportValue(0);
        }
        return [.. results];
    }
    // a batch that outlived the round that started it: no round reports it, so it says itself that it
    // is done (a failure has already been logged where it failed)
    void finishedAfterItsRound(Task<BatchTaskResult> batch) {
        try {
            if (!batch.IsCompletedSuccessfully || batch.Result.Error != null) return;
            var r = batch.Result;
            _store.LogInfo("TaskQueue: " + typeName(r.TaskTypeName) + ", " + r.TaskCount + " task" + (r.TaskCount == 1 ? "" : "s")
                + ", finished in " + r.DurationMs.To1000N() + "ms, after the round that started it. ");
        } catch {
            // the log is not the batch
        }
    }
    void reportPendingCountIfDue() {
        if (_lastReportedCount >= DateTime.UtcNow.AddSeconds(-1)) return;
        lock (_lock) { // due to counting
            runningZeroEstimator.ReportValue(_queue.CountTasks(BatchState.Pending));
        }
        _lastReportedCount = DateTime.UtcNow;
    }
    /// <summary>The next batch that can start, reserved and set to running; null when none can.</summary>
    IBatch? startNext() {
        lock (_lock) {
            emptyBuffer();
            var full = fullTypes();
            if (full.Count >= _runners.Count) return null;
            var batch = _queue.DequeueAndSetRunning(_runners, full, canStartNow);
            if (batch == null) return null;
            string[] keys;
            try {
                keys = keysOf(batch); // a runner's own code, which may throw
            } catch {
                _queue.Set([batch.Meta.BatchId], BatchState.Pending); // not left marked as running with nothing running it
                throw;
            }
            _running[batch.Meta.BatchId] = new RunningBatch(batch, keys);
            if (keys.Length > 0) {
                if (!_runningKeys.TryGetValue(batch.Meta.TaskTypeId, out var held)) _runningKeys[batch.Meta.TaskTypeId] = held = [];
                foreach (var key in keys) held[key] = held.TryGetValue(key, out var count) ? count + 1 : 1;
            }
            return batch;
        }
    }
    // the types already running as many batches as they may
    HashSet<string> fullTypes() {
        var full = new HashSet<string>();
        if (_running.Count == 0) return full;
        var counts = new Dictionary<string, int>();
        foreach (var running in _running.Values) {
            var typeId = running.Batch.Meta.TaskTypeId;
            counts[typeId] = counts.TryGetValue(typeId, out var count) ? count + 1 : 1;
        }
        foreach (var (typeId, count) in counts) {
            if (_runners.TryGetValue(typeId, out var runner) && count >= ConcurrencyOf(runner)) full.Add(typeId);
        }
        return full;
    }
    // asked of the candidates in queue order, under the lock
    bool canStartNow(IBatch candidate) {
        // never the same batch twice at once: one put back in line while it runs waits for itself
        if (_running.ContainsKey(candidate.Meta.BatchId)) return false;
        if (!_runningKeys.TryGetValue(candidate.Meta.TaskTypeId, out var held) || held.Count == 0) return true;
        if (!_runners.TryGetValue(candidate.Meta.TaskTypeId, out var runner)) return true;
        foreach (var task in candidate.GenericTasks) {
            var key = runner.GetConcurrencyKeyGeneric(task);
            if (key != null && held.ContainsKey(key)) return false;
        }
        return true;
    }
    string[] keysOf(IBatch batch) {
        if (!_runners.TryGetValue(batch.Meta.TaskTypeId, out var runner)) return [];
        List<string>? keys = null;
        foreach (var task in batch.GenericTasks) {
            var key = runner.GetConcurrencyKeyGeneric(task);
            if (key != null) (keys ??= []).Add(key);
        }
        return keys == null ? [] : [.. keys];
    }
    async Task<BatchTaskResult> executeAsync(IBatch batch) {
        try {
            return await executeCoreAsync(batch);
        } finally {
            Interlocked.Decrement(ref _inFlight);
        }
    }
    async Task<BatchTaskResult> executeCoreAsync(IBatch batch) {
        var startTime = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        // the Tasks log of the Activity page: a runner may write its own entries through this, and one
        // that writes none gets one entry per task from the queue once the batch is done
        var logTasks = _store.Logger.LoggingTask;
        var entriesWritten = 0;
        TaskLogger? taskLogging = null;
        if (logTasks) {
            taskLogging = (bool success, string id, string details) => {
                Interlocked.Increment(ref entriesWritten);
                _store.Logger.RecordTask(batch.Meta.TaskTypeId, success, batch.Meta.BatchId, id, details);
            };
        }
        long childActivityId = -1;
        Exception? error = null;
        RemainingTasks? remaining = null;
        _runners.TryGetValue(batch.Meta.TaskTypeId, out var runner);
        try {
            if (runner == null) throw new Exception("No runner for: " + batch.Meta.TaskTypeId);
            childActivityId = enterActivity(describe(batch, runner));
            // this batch's own flow: each batch runs on a Task.Run of its own, so no other batch sees it
            _currentBatchActivity.Value = childActivityId;
            remaining = await runner.ExecuteAsyncGeneric(batch, taskLogging, CancellationToken.None);
        } catch (Exception err) {
            error = err;
        } finally {
            if (childActivityId > -1) leaveActivity(childActivityId);
        }
        error = finish(batch, runner, error) ?? error;
        // nothing past this point may throw: the dispatcher waits for this task to know the batch is
        // done, and an exception here would take it out while other batches are still running
        try {
            if (error == null && remaining != null && remaining.Tasks.Length > 0) {
                // this part is not 100% complete and needs refinement
                // should change or log the fact that previous batch was not fully completed
                // and that these tasks are being re-enqueued as a new batch
                // also, they should be added to the top of the queue, not the bottom
                foreach (var task in remaining.Tasks) {
                    Enqueue(task, batch.Meta.GroupKey);
                }
            }
        } catch (Exception err) {
            error = err;
        }
        var result = new BatchTaskResult(batch.Meta.TaskTypeId, sw.Elapsed.TotalMilliseconds, startTime, batch.TaskCount, error);
        try {
            if (_store.Logger.LoggingTaskBatch) _store.Logger.RecordTaskBatch(batch.Meta.BatchId, result);
            if (logTasks && Volatile.Read(ref entriesWritten) == 0) recordTasks(batch, runner, error);
        } catch {
            // the log is not the batch: a failure to record it changes nothing about how it went
        }
        try {
            // logged where it failed rather than by the round, which may have ended long before
            if (error != null) _store.LogError(batch.Meta.TaskTypeId + " failed", error);
        } catch {
        }
        return result;
    }
    // one entry per task, each with how the batch as a whole went: a batch succeeds or fails together
    void recordTasks(IBatch batch, ITaskRunner? runner, Exception? error) {
        foreach (var task in batch.GenericTasks) {
            var description = runner?.DescribeTaskGeneric(task);
            var details = error == null ? description ?? "" : (description == null ? "" : description + ": ") + error.Message;
            _store.Logger.RecordTask(batch.Meta.TaskTypeId, error == null, batch.Meta.BatchId, runner?.GetTaskLogIdGeneric(task) ?? "", details);
        }
    }
    long enterActivity(string description) {
        lock (_activitySync) {
            if (_activityUsers++ == 0) _activityId = _store.RegisterActvity(DataStoreActivityCategory.RunningTask, _activityName);
            return _store.RegisterChildActvity(_activityId, DataStoreActivityCategory.RunningTask, description);
        }
    }
    void leaveActivity(long childActivityId) {
        lock (_activitySync) {
            _store.DeRegisterActivity(childActivityId);
            if (--_activityUsers == 0) {
                _store.DeRegisterActivity(_activityId);
                _activityId = -1;
            }
        }
    }
    /// <summary>
    /// Frees the batch's slot and keys, whatever else happens, and records how it went. Returns the
    /// error when recording it failed, so the batch is reported as failed rather than lost.
    /// </summary>
    Exception? finish(IBatch batch, ITaskRunner? runner, Exception? error) {
        lock (_lock) {
            var id = batch.Meta.BatchId;
            if (_running.Remove(id, out var running) && running.Keys.Length > 0
                && _runningKeys.TryGetValue(batch.Meta.TaskTypeId, out var held)) {
                foreach (var key in running.Keys) {
                    if (!held.TryGetValue(key, out var count)) continue;
                    if (count <= 1) held.Remove(key);
                    else held[key] = count - 1;
                }
            }
            try {
                if (running?.RunAgain == true) {
                    // put back in line while it ran: it already is pending, and runs again once
                    // picked up - which is what the admin UI promises when it does that
                } else if (error != null) {
                    _queue.Set(id, error);
                } else if (runner?.DeleteOnSuccess == true) {
                    _queue.Delete([id]);
                } else {
                    _queue.Set([id], BatchState.Completed);
                }
                return null;
            } catch (Exception err) {
                return err;
            }
        }
    }
    static string describe(IBatch batch, ITaskRunner runner) {
        // one task the runner can say something about is named by it: "Run job: backup"
        if (batch.TaskCount == 1) {
            var task = batch.GenericTasks.First();
            var what = runner.DescribeTaskGeneric(task) ?? runner.GetTaskLogIdGeneric(task);
            if (what != null) return typeName(task.TaskTypeId) + ": " + what;
        }
        var stringOfTypes = batch.GenericTasks.GroupBy(task => task.TaskTypeId).Select(g => new { Name = g.Key, Count = g.Count() });
        StringBuilder sb = new();
        foreach (var type in stringOfTypes) {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(typeName(type.Name) + " - " + type.Count + "");
        }
        return sb.ToString();
    }
    static string typeName(string typeId) {
        var index = typeId.LastIndexOf(".");
        return index > -1 ? typeId.Substring(index + 1).Decamelize() : typeId;
    }
    public BatchMetaWithCount[] GetBatchMeta(BatchState[] states, string[] typeIds, string[] groupKeys, int page, int pageSize, out int totalCount) {
        lock (_lock) {
            emptyBuffer();
            return _queue.GetBatchInfo(states, typeIds, groupKeys, page, pageSize, out totalCount);
        }
    }
    public void SetState(Guid[] batchIds, BatchState state) {
        lock (_lock) {
            emptyBuffer();
            _queue.Set(batchIds, state);
            // a batch a runner still holds is not handed out again until that run is over; put back in
            // line, it then stays pending instead of being marked done by the run that is finishing
            foreach (var id in batchIds) {
                if (_running.TryGetValue(id, out var running)) running.RunAgain = state == BatchState.Pending;
            }
        }
    }
    public void DeleteById(Guid[] batchIds) {
        lock (_lock) {
            emptyBuffer();
            _queue.Delete(batchIds);
            // deleted is deleted: a run that is finishing must not bring it back
            foreach (var id in batchIds) {
                if (_running.TryGetValue(id, out var running)) running.RunAgain = false;
            }
        }
    }
    public void DeleteByStateOrType(BatchState[] states, string[] typeIds) {
        lock (_lock) {
            emptyBuffer();
            _queue.Delete(states, typeIds);
        }
    }
    public void DeleteAll() => DeleteByStateOrType([], []);
    public int DeleteExpiredTasks() {
        lock (_lock) {
            emptyBuffer();
            int deletedCount = 0;
            foreach (var runner in _runners.Values) {
                foreach (var state in Enum.GetValues<BatchState>()) {
                    var maxAge = runner.GetMaximumAgeInQueuePerState(state);
                    if (maxAge == TimeSpan.MaxValue) continue; // no expiration for this state
                    if (maxAge <= TimeSpan.Zero) maxAge = TimeSpan.Zero;
                    var cutoff = DateTime.UtcNow.SafeSubtract(maxAge);
                    var batches = _queue.GetBatchInfo([state], [runner.TaskTypeId], [], 0, int.MaxValue, out _);
                    var expiredBatches = batches
                    .Where(b => b.State == state && b.CreatedUtc < cutoff)
                    .Select(b => b.BatchId)
                    .ToArray();
                    _queue.Delete(expiredBatches);
                    deletedCount += expiredBatches.Length;
                }
            }
            return deletedCount;
        }
    }
    public void Dispose() {
        emptyBuffer();
        _queue.Dispose();
    }
    public void ReOpen() {
        _isShuttingdown = false;
        lock (_lock) {
            // whatever ran before is not this session's to count against any limit
            _running.Clear();
            _runningKeys.Clear();
            _queue.ReOpen();
        }
    }
    public void RestartTasksFromDbShutdown(out int restaredCount, out int abortedCount, out int restaredTaskCount, out int abortedTaskCount) {
        lock (_lock) {
            restaredCount = 0;
            abortedCount = 0;
            restaredTaskCount = 0;
            abortedTaskCount = 0;
            var batches = _queue.GetBatchInfo([BatchState.Running], [], [], 0, int.MaxValue, out _);
            foreach (var batch in batches) {
                if (_running.ContainsKey(batch.BatchId)) continue; // running now, in this session: not left over from a shutdown
                if (!_runners.TryGetValue(batch.TaskTypeId, out var runner)) continue; // ignore if no runner for this type
                if (runner.RestartTaskBatchesOnStartupThatStartedButNeverFailedOrCompleted) {
                    _queue.Set([batch.BatchId], BatchState.Pending);
                    restaredCount++;
                    restaredTaskCount += batch.TaskCount;
                } else {
                    _queue.Set([batch.BatchId], BatchState.AbortedOnStartup);
                    abortedTaskCount += batch.TaskCount;
                    abortedCount++;
                }
            }
        }
    }
    /// <summary>
    /// Stops handing out batches and waits, up to the given time, for the ones already running to
    /// finish - those of rounds that have ended too. True when nothing is running any more.
    /// </summary>
    public bool TryGracefulShutdown(int maxWaitMs) {
        _isShuttingdown = true;
        Stopwatch sw = Stopwatch.StartNew();
        // a round still under way may start one more batch, which it counts before it ends
        while (Volatile.Read(ref _executing) > 0 || Volatile.Read(ref _inFlight) > 0) {
            if (sw.ElapsedMilliseconds > maxWaitMs) return false; // give up...
            Thread.Sleep(50);
        }
        return true;
    }
    public void FlushDisk() {
        lock (_lock) {
            //Stopwatch sw = Stopwatch.StartNew();
            _queue.FlushDiskIfNeeded();
            //Console.WriteLine("Flushed disk in " + sw.Elapsed.TotalMilliseconds.ToString("0.00ms"));
        }
    }
    public TimeSpan? EstimateDurationUntilEmpty() {
        if (runningZeroEstimator.TryEstimateDurationUntilZero(out var duration))
            return duration;
        return null;
    }
}
