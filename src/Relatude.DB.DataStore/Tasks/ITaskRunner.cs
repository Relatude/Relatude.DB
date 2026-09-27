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
    /// <summary>
    /// How many batches of this type may run at the same time when the database's settings say
    /// nothing about the type (<c>LocalSettings.TaskConcurrency</c> can). 1 runs them one after
    /// another, which is what a runner written without concurrency in mind needs, so that is the
    /// default. A runner that raises it has to be safe to run beside itself - see
    /// <see cref="GetConcurrencyKeyGeneric"/> for work that reads something and writes a result back.
    /// </summary>
    int MaxConcurrency => 1;
    /// <summary>
    /// The most batches of this type that may ever run at once, whatever the settings say: 1 for work
    /// that must never overlap with itself, such as a log rewrite. The queue also caps every type at
    /// <see cref="TaskQueue.MaxSupportedConcurrency"/>.
    /// </summary>
    int MaxConcurrencyLimit => TaskQueue.MaxSupportedConcurrency;
    /// <summary>
    /// What a task works on, for a type that may run more than one batch at once. A batch holding a
    /// task whose key a running batch of the same type also holds is not started until that one is
    /// done, so two tasks with the same key never run at the same time. Work that reads the current
    /// state of something and writes a result back needs this: two batches indexing the same node,
    /// finishing in the wrong order, would leave the older result in place. Null, the default, lets
    /// any two tasks run together.
    /// </summary>
    string? GetConcurrencyKeyGeneric(TaskData task) => null;
}
public abstract class TaskRunner<TTask> : ITaskRunner where TTask : TaskData {
    public string TaskTypeId { get; } = typeof(TTask).FullName ?? throw new InvalidOperationException("Task type must have a valid FullName.");
    public abstract BatchTaskPriority Priority { get; }
    public virtual bool RestartTaskBatchesOnStartupThatStartedButNeverFailedOrCompleted { get; } = true;
    public abstract TimeSpan GetMaximumAgeInQueueAfterExecution();
    public abstract bool PersistToDisk { get; }
    /// <inheritdoc cref="ITaskRunner.MaxConcurrency"/>
    public virtual int MaxConcurrency => 1;
    /// <inheritdoc cref="ITaskRunner.MaxConcurrencyLimit"/>
    public virtual int MaxConcurrencyLimit => TaskQueue.MaxSupportedConcurrency;
    public string? GetConcurrencyKeyGeneric(TaskData task) => GetConcurrencyKey((TTask)task);
    /// <inheritdoc cref="ITaskRunner.GetConcurrencyKeyGeneric"/>
    public virtual string? GetConcurrencyKey(TTask task) => null;
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
