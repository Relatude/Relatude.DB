//using WAF.Common;
//using WAF.Datamodels;
//using WAF.Nodes;
//namespace WAF.Tasks;
//// Poco class representing the batch task information in the queue.
//[Node(TextIndex = BoolValue.False, SemanticIndex = BoolValue.False)]
//public class BatchModel {
//    public Guid Id { get; set; }
//    public string TypeId { get; set; } = string.Empty;
//    public string JobId { get; set; } = string.Empty;
//    public BatchTaskPriority Priority { get; set; }
//    public BatchState State { get; set; }
//    public DateTime CreatedUtc { get; set; }
//    public DateTime Completed { get; set; }
//    public string ErrorType { get; set; } = string.Empty;
//    public string ErrorMessage { get; set; } = string.Empty;
//    public int TaskCount { get; set; }
//    public byte[] TaskData { get; set; } = [];
//}
//public class DefaultQueueStore : IQueueStore {
//    NodeStore? _store;
//    NodeStore store {
//        get {
//            if (_store == null) throw new InvalidOperationException("Queue store is not initialized. Call Init() first.");
//            return _store;
//        }
//    }
//    public void Init(NodeStore store) {
//        _store = store;
//    }
//    public void AddModels(Datamodel model) {
//        model.Add<BatchModel>();
//    }
//    public void Enqueue(IBatch task, ITaskRunner runner) {
//        var info = new BatchModel {
//            Id = task.Meta.BatchId,
//            TypeId = task.Meta.TaskTypeId,
//            JobId = task.Meta.JobId ?? string.Empty,
//            Priority = task.Meta.Priority,
//            State = task.Meta.State,
//            CreatedUtc = task.Meta.CreatedUtc,
//            Completed = task.Meta.Completed ?? DateTime.MinValue,
//            ErrorType = task.Meta.ErrorType ?? string.Empty,
//            ErrorMessage = task.Meta.ErrorMessage ?? string.Empty,
//            TaskCount = task.TaskCount,
//            TaskData = task.TasksToBytes(runner)
//        };
//        store.Insert(info);
//    }
//    public IBatch? DequeueAndSetRunning(Dictionary<string, ITaskRunner> runners) {
//        var info = store.Query<BatchModel>()
//            .Where(x => x.State == BatchState.Pending)
//            .OrderBy(x => x.Priority).OrderBy(x => x.CreatedUtc)
//            .Take(1).Execute().FirstOrDefault();
//        if (info == null) return null;
//        if (!runners.TryGetValue(info.TypeId, out var runner)) {
//            throw new InvalidOperationException($"No runner found for task type {info.TypeId}");
//        }
//        var batchMeta = new BatchMeta(info.Id, info.TypeId, BatchState.Running, info.Priority, info.CreatedUtc) {
//            JobId = info.JobId == string.Empty ? null : info.JobId,
//            Completed = info.Completed == DateTime.MinValue ? null : info.Completed,
//            ErrorType = info.ErrorType == string.Empty ? null : info.ErrorType,
//            ErrorMessage = info.ErrorMessage == string.Empty ? null : info.ErrorMessage
//        };
//        var batch = runner.GetBatchFromMetaAndData(batchMeta, info.TaskData);
//        SetState(batch.Meta.BatchId, BatchState.Running);
//        return batch;
//    }
//    public void SetState(Guid[] batchIds, BatchState state) {
//        store.UpdateProperty<BatchModel, BatchState>(batchIds, x => x.State, state);
//    }
//    public void DeleteIfItExists(Guid taskId) => store.Delete(taskId);
//    public void DeleteMany(BatchState[] states, string[] typeIds) {
//        var q = store.Query<BatchModel>();
//        if (states.Length > 0) q.WhereIn(x => x.State, states);
//        if (typeIds.Length > 0) q.WhereIn(x => x.TypeId, typeIds);
//        var ids = q.SelectId().Execute();
//        store.Delete(ids);
//    }
//    public int CountBatch(BatchState state) => store.Query<BatchModel>().Where(x => x.State == state).Count();
//    public int CountTasks(BatchState state) => store.Query<BatchModel>().Where(x => x.State == state).Sum(x => x.TaskCount);
//    public BatchMetaWithCount[] GetBatchInfo(BatchState[] states, string[] typeIds, string[] jobIds, int page, int pageSize, out int totalCount) {
//        var q = store.Query<BatchModel>();
//        if (states.Length > 0) q.WhereIn(x => x.State, states);
//        if (typeIds.Length > 0) q.WhereIn(x => x.TypeId, typeIds);
//        if (jobIds.Length > 0) q.WhereIn(x => x.JobId, jobIds);
//        q.Page(page, pageSize);
//        var batchModels = q.Execute(out totalCount);
//        var batches = new List<BatchMetaWithCount>();
//        foreach (var batch in batchModels) {
//            var meta = new BatchMeta(batch.Id, batch.TypeId, batch.State, batch.Priority, batch.CreatedUtc) {
//                JobId = batch.JobId == string.Empty ? null : batch.JobId,
//                Completed = batch.Completed == DateTime.MinValue ? null : batch.Completed,
//                ErrorType = batch.ErrorType == string.Empty ? null : batch.ErrorType,
//                ErrorMessage = batch.ErrorMessage == string.Empty ? null : batch.ErrorMessage
//            };
//            batches.Add(new BatchMetaWithCount(meta, batch.TaskCount));
//        }
//        return [.. batches];
//    }
//    public void SetState(Guid batchId, BatchState state) => store.UpdateProperty<BatchModel, BatchState>(batchId, x => x.State, state);
//    public void SetState(string jobId, BatchState state) {
//        var ids = store.Query<BatchModel>().Where(x => x.JobId == jobId).SelectId().Execute();
//        store.UpdateProperty<BatchModel, BatchState>(ids, x => x.State, state);
//    }
//    public void SetFailedState(Guid batchId, Exception error) {
//        store.UpdateProperties<BatchModel>(batchId,
//            new(x => x.State, BatchState.Failed),
//            new(x => x.ErrorType, error.GetType().FullName ?? "Unknown"),
//            new(x => x.ErrorMessage, error.Message)
//            );
//    }
//    public int DeleteExpiredTasks(Dictionary<string, ITaskRunner> runners) {
//        int deletedCount = 0;
//        foreach (var runner in runners.Values) {
//            foreach (var state in Enum.GetValues<BatchState>()) {
//                var maxAge = runner.GetMaximumAgeInPersistedQueuePerState(state);
//                if (maxAge == TimeSpan.MaxValue) continue; // no expiration for this state
//                if (maxAge <= TimeSpan.Zero) maxAge = TimeSpan.Zero;
//                var cutoff = DateTime.UtcNow.SafeSubtract(maxAge);
//                var ids = store.Query<BatchModel>().Where(x => x.State == state && x.CreatedUtc < cutoff).SelectId().Execute();
//                if (ids.Count == 0) continue; // nothing to delete
//                store.Delete(ids);
//                deletedCount += ids.Count;
//            }
//        }
//        return deletedCount;
//    }
//    public void ReOpen() { }
//    public void Dispose() { }
//}