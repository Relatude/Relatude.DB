using Relatude.DB.Datamodels.Properties;

namespace Relatude.DB.Tasks;
public delegate void TaskLogger(bool success, string id, string details);
public interface ITaskRunner {
    string TaskTypeId { get; }
    BatchTaskPriority Priority { get; }
    Task ExecuteAsyncGeneric(IBatch tasks, TaskLogger? taskLogger);
    void LogTask(string id, string title, string category, string details, string error, bool success);
    byte[] TaskToBytesGeneric(TaskData task);
    TaskData TaskFromBytesGeneric(byte[] bytes);
    IBatch CreateBatchWithOneTask(TaskData task, BatchState state, string? jobId);
    IBatch GetBatchFromMetaAndData(BatchMeta batch, byte[] taskData);
    int MaxTaskCountPerBatch { get; }
    bool RestartIfAbortedDuringShutdown { get; }
    TimeSpan GetMaximumAgeInQueuePerState(BatchState state);
    bool DeleteOnSuccess { get; }
    bool PersistToDisk { get; }
}
public abstract class TaskRunner<TTask> : ITaskRunner where TTask : TaskData {
    public string TaskTypeId { get; } = typeof(TTask).FullName ?? throw new InvalidOperationException("Task type must have a valid FullName.");
    public abstract BatchTaskPriority Priority { get; }
    public virtual bool RestartIfAbortedDuringShutdown { get; set; } = true;
    public abstract TimeSpan GetMaximumAgeInQueueAfterExecution();
    public abstract bool PersistToDisk { get; }
    public virtual TimeSpan GetMaximumAgeInQueuePerState(BatchState state) {
        return state switch {
            BatchState.Completed => GetMaximumAgeInQueueAfterExecution(),
            BatchState.Failed => GetMaximumAgeInQueueAfterExecution(),
            BatchState.Cancelled => GetMaximumAgeInQueueAfterExecution(),
            BatchState.Pending => TimeSpan.MaxValue,
            BatchState.Running => TimeSpan.MaxValue,
            BatchState.Waiting => TimeSpan.MaxValue,
            _ => TimeSpan.MaxValue
        };
    }
    public async Task ExecuteAsyncGeneric(IBatch batch, TaskLogger? taskLogger) => await ExecuteAsync((Batch<TTask>)batch, taskLogger);
    public abstract Task ExecuteAsync(Batch<TTask> batch, TaskLogger? taskLogger);
    public byte[] TaskToBytesGeneric(TaskData task) => TaskToBytes((TTask)task);
    public TaskData TaskFromBytesGeneric(byte[] bytes) => TaskFromBytes(bytes);
    public abstract byte[] TaskToBytes(TTask task);
    public abstract TTask TaskFromBytes(byte[] bytes);
    public IBatch CreateBatchWithOneTask(TaskData task, BatchState state, string? jobId) {
        var batchMeta = new BatchMeta(Guid.NewGuid(), task.TaskTypeId, state, Priority, DateTime.UtcNow);
        if (jobId != null) batchMeta.JobId = jobId;
        var batch = new Batch<TTask>(batchMeta);
        batch.AddTask(task);
        return batch;
    }
    public IBatch GetBatchFromMetaAndData(BatchMeta batchMeta, byte[] taskData) {
        var batch = new Batch<TTask>(batchMeta);
        batch.AddTasksFromBytes(this, taskData);
        return batch;
    }
    public void LogTask(string id, string title, string category, string details, string error, bool success) {

    }
    public abstract bool DeleteOnSuccess { get; }
    public abstract int MaxTaskCountPerBatch { get; }
}