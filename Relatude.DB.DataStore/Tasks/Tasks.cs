using System.Data;
using Relatude.DB.Query.Data;
using Relatude.DB.Serialization;

namespace Relatude.DB.Tasks;
public enum BatchState {
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4,
    Waiting = 5,
    AbortedOnStartup = 10,
}
public abstract class TaskData() {
    public string TaskTypeId => GetType().FullName ?? throw new InvalidOperationException("Task type ID cannot be null");
}
public class BatchMetaWithCount : BatchMeta {
    public BatchMetaWithCount(BatchMeta info, int count)
        : base(info.BatchId, info.TaskTypeId, info.State, info.Priority, info.CreatedUtc) {
        TaskCount = count;
        Completed = info.Completed;
        ErrorType = info.ErrorType;
        ErrorMessage = info.ErrorMessage;
    }
    public int TaskCount { get; }
}
public class BatchMeta(Guid batchId, string typeId, BatchState state, BatchTaskPriority priority, DateTime created) {
    public Guid BatchId { get; } = batchId;
    public string TaskTypeId { get; } = typeId;
    public BatchState State { get; set; } = state;
    public BatchTaskPriority Priority { get; set; } = priority;
    public DateTime CreatedUtc { get; set; } = created;
    public DateTime? Completed { get; set; }
    public string? JobId { get; set; }
    public string? ErrorType { get; set; }
    public string? ErrorMessage { get; set; }
    public byte[] ToBytes() {
        using var ms = new MemoryStream();
        using BinaryWriter w = new(ms);
        w.Write(BatchId.ToByteArray());
        w.Write(TaskTypeId);
        w.Write((int)State);
        w.Write((int)Priority);
        w.Write(CreatedUtc.ToBinary());
        w.Write(Completed.HasValue ? Completed.Value.ToBinary() : 0L);
        w.Write(JobId ?? string.Empty);
        w.Write(ErrorType ?? string.Empty);
        w.Write(ErrorMessage ?? string.Empty);
        return ms.ToArray();
    }
    public static BatchMeta FromBytes(byte[] bytes) {
        using var ms = new MemoryStream(bytes);
        using BinaryReader br = new(ms);
        var batchId = new Guid(br.ReadBytes(16));
        var typeId = br.ReadString();
        var state = (BatchState)br.ReadInt32();
        var priority = (BatchTaskPriority)br.ReadInt32();
        var createdUtc = DateTime.FromBinary(br.ReadInt64());
        DateTime? completed = null;
        long completedBinary = br.ReadInt64();
        if (completedBinary != 0) completed = DateTime.FromBinary(completedBinary);
        string? jobId = br.ReadString();
        string? errorType = br.ReadString();
        string? errorMessage = br.ReadString();
        return new BatchMeta(batchId, typeId, state, priority, createdUtc) {
            Completed = completed,
            JobId = jobId,
            ErrorType = errorType,
            ErrorMessage = errorMessage
        };
    }
}
public interface IBatch {
    BatchMeta Meta { get; }
    void AddTask(TaskData task);
    int TaskCount { get; }
    IEnumerable<TaskData> GenericTasks { get; }
    byte[] TasksToBytes(ITaskRunner runner);
    void AddTasksFromBytes(ITaskRunner runner, byte[] bytes);
}
public class Batch<TTask>(BatchMeta meta) : IBatch where TTask : TaskData {
    public BatchMeta Meta { get; } = meta;
    private readonly List<TTask> _tasks = [];
    public void AddTask(TaskData task) => _tasks.Add((TTask)task);
    public IReadOnlyList<TTask> Tasks => _tasks.AsReadOnly();
    public int TaskCount => _tasks.Count;
    public IEnumerable<TaskData> GenericTasks => _tasks.Cast<TaskData>();
    void writeTasks(BinaryWriter w, ITaskRunner runner) {
        w.Write(TaskCount);
        foreach (var task in GenericTasks) {
            var taskBytes = runner.TaskToBytesGeneric(task);
            w.Write(taskBytes.Length);
            w.Write(taskBytes);
        }
    }
    public byte[] TasksToBytes(ITaskRunner runner) {
        using MemoryStream ms = new();
        using BinaryWriter w = new(ms);
        writeTasks(w, runner);
        return ms.ToArray();
    }
    public void AddTasksFromBytes(ITaskRunner runner, byte[] bytes) {
        using var ms = new MemoryStream(bytes);
        using (BinaryReader br = new(ms)) {
            int count = br.ReadInt32();
            for (int i = 0; i < count; i++) {
                int length = br.ReadInt32();
                byte[] taskBytes = br.ReadBytes(length);
                var task = runner.TaskFromBytesGeneric(taskBytes);
                this.AddTask(task);
            }
        }
    }

}
public enum BatchTaskPriority {
    Low = 0, // typically for not time critical long running tasks like email sending, data processing, etc.
    Medium = 1, // for tasks that should be processed in a reasonable time frame, like user notifications
    High = 2, // for tasks that need to be processed immediately, like UI sensitive operations
}
public class BatchTaskResult(string taskTypeName, double durationMs, DateTime startedUTC, int taskCount, Exception? error = null) {
    public string TaskTypeName { get; } = taskTypeName;
    public double DurationMs { get; } = durationMs;
    public DateTime StartedUTC { get; } = startedUTC;
    public Exception? Error { get; } = error;
    public int TaskCount { get; } = taskCount;
}