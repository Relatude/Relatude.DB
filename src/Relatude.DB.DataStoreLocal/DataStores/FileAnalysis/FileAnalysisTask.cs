using Relatude.DB.Common;
using Relatude.DB.DataStores;

namespace Relatude.DB.Tasks.FileAnalysis;

public sealed class FileAnalysisTask(PropertyPath propertyPath, Guid fileId, FileAnalysisKinds kinds) : TaskData {
    public PropertyPath PropertyPath { get; } = propertyPath;
    public Guid FileId { get; } = fileId;
    public FileAnalysisKinds Kinds { get; } = kinds;
}

public sealed record FileAnalysisOutcome(bool Success, string Details);

internal sealed class FileAnalysisTaskRunner(DataStoreLocal db) : TaskRunner<FileAnalysisTask> {
    public override BatchTaskPriority Priority => BatchTaskPriority.Low;
    public override int MaxTaskCountPerBatch => 5;
    public override bool PersistToDisk => true;
    public override int MaxConcurrency => 2;
    public override string? GetConcurrencyKey(FileAnalysisTask task) => task.PropertyPath.ToString();
    public override string? GetTaskLogId(FileAnalysisTask task) => "file " + task.FileId;
    public override string? DescribeTask(FileAnalysisTask task) => "Analysing a file: " + describe(task.Kinds);
    public override bool DeleteOnSuccess => true;
    public override TimeSpan GetMaximumAgeInQueueAfterExecution() => TimeSpan.FromHours(1);
    public override bool RestartTaskBatchesOnStartupThatStartedButNeverFailedOrCompleted { get; } = true;

    public override async Task<RemainingTasks> ExecuteAsyncDetailed(Batch<FileAnalysisTask> batch, TaskLogger? taskLogger, CancellationToken cancellationToken) {
        var failures = new List<string>();
        foreach (var task in batch.Tasks) {
            cancellationToken.ThrowIfCancellationRequested();
            var id = GetTaskLogId(task)!;
            FileAnalysisOutcome outcome;
            try {
                outcome = await db.RunFileAnalysisTaskAsync(task, cancellationToken);
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                throw;
            } catch (Exception error) {
                outcome = new(false, error.Message);
            }
            taskLogger?.Invoke(outcome.Success, id, outcome.Details);
            if (!outcome.Success) failures.Add(id + ": " + outcome.Details);
        }
        if (failures.Count > 0) {
            db.LogWarning(failures.Count + " of " + batch.Tasks.Count() + " files could not be analysed. They are tried again at the next text reindex. ", string.Join(Environment.NewLine, failures));
        }
        return RemainingTasks.None;
    }
    public override Task ExecuteAsync(Batch<FileAnalysisTask> batch, TaskLogger? taskLogger) => ExecuteAsyncDetailed(batch, taskLogger, CancellationToken.None);

    public override byte[] TaskToBytes(FileAnalysisTask task) {
        var path = task.PropertyPath.ToBytes();
        var bytes = new byte[4 + 16 + path.Length];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), (int)task.Kinds);
        task.FileId.TryWriteBytes(bytes.AsSpan(4, 16));
        path.CopyTo(bytes, 20);
        return bytes;
    }
    public override FileAnalysisTask TaskFromBytes(byte[] bytes) {
        var kinds = (FileAnalysisKinds)BitConverter.ToInt32(bytes, 0);
        var fileId = new Guid(bytes.AsSpan(4, 16));
        var path = PropertyPath.FromBytes(bytes[20..]);
        return new FileAnalysisTask(path, fileId, kinds);
    }
    static string describe(FileAnalysisKinds kinds) {
        var parts = new List<string>();
        if (kinds.HasFlag(FileAnalysisKinds.ImageMeta)) parts.Add("image meta analysis");
        if (kinds.HasFlag(FileAnalysisKinds.ImageCropMode)) parts.Add("crop mode analysis");
        if (kinds.HasFlag(FileAnalysisKinds.Text)) parts.Add("text extraction");
        return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
    }
}
