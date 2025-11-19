using Relatude.DB.Common;
using Relatude.DB.DataStores;
using System.Diagnostics;

namespace Relatude.DB.Tasks;

public class TaskQueue : IDisposable {
    public TaskQueue(IDataStore store, IQueueStore queue, Dictionary<string, ITaskRunner> runners) {
        _store = store;
        _queue = queue;
        _runners = runners;
    }
    readonly IDataStore _store;
    readonly IQueueStore _queue;
    readonly Dictionary<string, ITaskRunner> _runners;
    readonly Dictionary<string, IBatch> _batchBufferByTypeAndJobId = [];
    readonly object _lock = new();
    bool _isShuttingdown = false;
    bool _hasShutdown = false;
    bool _isExecuting = false;
    void emptyBuffer() {
        if (_batchBufferByTypeAndJobId.Count > 0) { // move all batches to the queues
            foreach (var kvp in _batchBufferByTypeAndJobId) {
                _queue.Enqueue(kvp.Value, _runners[kvp.Value.Meta.TaskTypeId]);
            }
            _batchBufferByTypeAndJobId.Clear();
        }
    }
    public string QueueStoreTypeName => _queue.GetType().Name.ToString();
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
    public KeyValuePair<BatchState, int>[] TaskCountsPerState() {
        lock (_lock) {
            emptyBuffer();
            return _queue.TaskCountsPerState();
        }
    }
    public IBatch? Dequeue() {
        lock (_lock) {
            emptyBuffer();
            return _queue.DequeueAndSetRunning(_runners);
        }
    }
    public void Enqueue(TaskData task, string? jobId = null) {
        Enqueue(task, jobId, BatchState.Pending);
    }
    public void Enqueue(TaskData task, string? jobId, BatchState state) {
        lock (_lock) {
            var typeId = task.TaskTypeId;
            if (!_runners.TryGetValue(typeId, out var runner)) throw new Exception("No runner for " + typeId);
            var batchKey = typeId + jobId;
            if (_batchBufferByTypeAndJobId.TryGetValue(batchKey, out var batch)) {
                if (batch.TaskCount < runner.MaxTaskCountPerBatch) {
                    batch.AddTask(task);
                } else {
                    _queue.Enqueue(batch, runner);
                    _batchBufferByTypeAndJobId[batchKey] = runner.CreateBatchWithOneTask(task, state, jobId);
                }
            } else {
                _batchBufferByTypeAndJobId.Add(batchKey, runner.CreateBatchWithOneTask(task, state, jobId));
            }
        }
    }
    public async Task<BatchTaskResult[]> ExecuteTasksAsync(int maxDurationMs, Func<bool> abort, long parentActivityId) {
        var results = new List<BatchTaskResult>();
        if (_isShuttingdown) return [];
        _isExecuting = true;
        var totalMs = Stopwatch.StartNew();
        var taskNo = 0;
        while (!_isShuttingdown) {
            long childActivityId = -1;
            if (abort()) break; // allow external abort
            if (totalMs.ElapsedMilliseconds >= maxDurationMs) break;
            var startTime = DateTime.UtcNow;
            var sw = Stopwatch.StartNew();
            var nextBatch = Dequeue();
            if (nextBatch == null) break; // no more tasks to process
            taskNo += nextBatch.TaskCount;
            TaskLogger? taskLogging = null;
            if (_store.Logger.LoggingTask) {
                taskLogging = (bool success, string id, string details) => {
                    _store.Logger.RecordTask(nextBatch.Meta.TaskTypeId, success, nextBatch.Meta.BatchId, id, details);
                };
            }
            BatchTaskResult result;
            try {
                if (!_runners.TryGetValue(nextBatch.Meta.TaskTypeId, out var runner)) throw new Exception("No runner for: " + nextBatch.Meta.TaskTypeId);
                var tasksLeft = CountTasks(BatchState.Pending);
                var progress = tasksLeft + taskNo > 0 ? (100 * taskNo / (tasksLeft + taskNo)) : 100;
                childActivityId = _store.RegisterChildActvity(parentActivityId, DataStoreActivityCategory.RunningTask, $"Running task {taskNo} of {tasksLeft + taskNo}. ", progress);
                await runner.ExecuteAsyncGeneric(nextBatch, taskLogging);
                lock (_lock) {
                    if (runner.DeleteOnSuccess) {
                        _queue.Delete([nextBatch.Meta.BatchId]);
                    } else {
                        _queue.Set([nextBatch.Meta.BatchId], BatchState.Completed);
                    }
                }
                result = new BatchTaskResult(nextBatch.Meta.TaskTypeId, sw.Elapsed.TotalMilliseconds, startTime, nextBatch.TaskCount);
            } catch (Exception err) {
                lock (_lock) {
                    _queue.Set(nextBatch.Meta.BatchId, err);
                }
                result = new BatchTaskResult(nextBatch.Meta.TaskTypeId, sw.Elapsed.TotalMilliseconds, startTime, nextBatch.TaskCount, err);
            } finally {
                if (childActivityId > -1) _store.DeRegisterActivity(childActivityId);
            }
            if (_store.Logger.LoggingTaskBatch) _store.Logger.RecordTaskBatch(nextBatch.Meta.BatchId, result);
            results.Add(result);
        }
        if (_isShuttingdown) _hasShutdown = true;
        _isExecuting = false;
        return [.. results];
    }
    public BatchMetaWithCount[] GetBatchMeta(BatchState[] states, string[] typeIds, string[] jobIds, int page, int pageSize, out int totalCount) {
        lock (_lock) {
            emptyBuffer();
            return _queue.GetBatchInfo(states, typeIds, jobIds, page, pageSize, out totalCount);
        }
    }
    public void SetState(Guid[] batchIds, BatchState state) {
        lock (_lock) {
            emptyBuffer();
            _queue.Set(batchIds, state);
        }
    }
    public void DeleteById(Guid[] batchIds) {
        lock (_lock) {
            emptyBuffer();
            _queue.Delete(batchIds);
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
                    var maxAge = runner.GetMaximumAgeInPersistedQueuePerState(state);
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
        _hasShutdown = false;
        _isShuttingdown = false;
        _isExecuting = false;
        _queue.ReOpen();
    }
    public void RestartTasksFromDbShutdown(out int restaredCount, out int abortedCount, out int restaredTaskCount, out int abortedTaskCount) {
        lock (_lock) {
            restaredCount = 0;
            abortedCount = 0;
            restaredTaskCount = 0;
            abortedTaskCount = 0;
            var batches = _queue.GetBatchInfo([BatchState.Running], [], [], 0, int.MaxValue, out _);
            foreach (var batch in batches) {
                if (!_runners.TryGetValue(batch.TaskTypeId, out var runner)) continue; // ignore if no runner for this type
                if (runner.RestartIfAbortedDuringShutdown) {
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
    public bool TryGracefulShutdown(int maxWaitMs) {
        _isShuttingdown = true;
        if (!_isExecuting) {
            _hasShutdown = true;
            return true; // no need to wait
        }
        Stopwatch sw = Stopwatch.StartNew();
        while (!_hasShutdown) {
            Thread.Sleep(50);
            //Console.WriteLine("Waited for shutdown for " + sw.ElapsedMilliseconds + "ms");
            if (sw.ElapsedMilliseconds > maxWaitMs) break; // give up...
        }
        return _hasShutdown;
    }

    public void FlushDisk() {
        lock (_lock) {
            //Stopwatch sw = Stopwatch.StartNew();
            _queue.FlushDiskIfNeeded();
            //Console.WriteLine("Flushed disk in " + sw.Elapsed.TotalMilliseconds.ToString("0.00ms"));
        }
    }
}
