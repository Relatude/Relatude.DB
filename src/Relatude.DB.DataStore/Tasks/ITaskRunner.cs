using Relatude.DB.Datamodels.Properties;

namespace Relatude.DB.Tasks;

public delegate void TaskLogger(bool success, string id, string details);
public class RemainingTasks {
    public RemainingTasks(IEnumerable<TaskData> tasks) {
        Tasks = [.. tasks];
    }
    public TaskData[] Tasks { get; }
    public static RemainingTasks None = new([]);
}
public interface ITaskRunner {
    string TaskTypeId { get; }
    BatchTaskPriority Priority { get; }
    Task<RemainingTasks> ExecuteAsyncGeneric(IBatch tasks, TaskLogger? taskLogger, CancellationToken cancellationToken);
    void LogTask(string id, string title, string category, string details, string error, bool success);
    byte[] TaskToBytesGeneric(TaskData task);
    TaskData TaskFromBytesGeneric(byte[] bytes);
    IBatch CreateBatchWithOneTask(TaskData task, BatchState state, string? jobId);
    IBatch GetBatchFromMetaAndData(BatchMeta batch, byte[] taskData);
    int MaxTaskCountPerBatch { get; }
    bool RestartTaskBatchesOnStartupThatStartedButNeverFailedOrCompleted { get; }
    TimeSpan GetMaximumAgeInQueuePerState(BatchState state);
    bool DeleteOnSuccess { get; }
    bool PersistToDisk { get; }
}
public abstract class TaskRunner<TTask> : ITaskRunner where TTask : TaskData {
    public string TaskTypeId { get; } = typeof(TTask).FullName ?? throw new InvalidOperationException("Task type must have a valid FullName.");
    public abstract BatchTaskPriority Priority { get; }
    public virtual bool RestartTaskBatchesOnStartupThatStartedButNeverFailedOrCompleted { get; } = true;
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
    public async Task<RemainingTasks> ExecuteAsyncGeneric(IBatch batch, TaskLogger? taskLogger, CancellationToken cancellationToken)
        => await ExecuteAsyncDetailed((Batch<TTask>)batch, taskLogger, cancellationToken);

    public virtual async Task<RemainingTasks> ExecuteAsyncDetailed(Batch<TTask> batch, TaskLogger? taskLogger, CancellationToken cancellationToken) {
        await ExecuteAsync(batch, taskLogger);
        return RemainingTasks.None;
    }
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