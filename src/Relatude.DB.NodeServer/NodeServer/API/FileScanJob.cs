namespace Relatude.DB.NodeServer.API;
/// <summary>One background file store scan (unreferenced files, missing files). Scanning every file
/// value of a big database takes far longer than a request should, so the scan runs as a job the
/// client polls for progress and can cancel. Shared by both admin UIs.</summary>
internal class FileScanJob {
    public const string Running = "running";
    public const string Done = "done";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
    public Guid Id { get; } = Guid.NewGuid();
    public Guid StoreId;
    public string Kind = string.Empty;
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public CancellationTokenSource Cancellation { get; } = new();
    // written by the job task, read by polling requests; torn values are harmless for display
    public volatile string State = Running;
    public volatile string Description = "";
    public volatile int Percent;
    public volatile string? Error;
    // assigned before State turns to Done, so a poll that sees Done also sees the result
    public object? Result;
    public void SetProgress(string description, int percent) {
        Description = description;
        Percent = percent;
    }
}
internal static class FileScanJobs {
    static readonly Dictionary<Guid, FileScanJob> _jobs = [];
    public static FileScanJob Get(Guid jobId) {
        lock (_jobs) {
            if (_jobs.TryGetValue(jobId, out var job)) return job;
            throw new Exception("File scan job not found. ");
        }
    }
    /// <summary>Runs one file store scan in the background, at most one of each kind per store
    /// (across both admin UIs). Finished jobs are kept for an hour, so a client can still pick up
    /// the result.</summary>
    public static FileScanJob Start(Guid storeId, string kind, Func<FileScanJob, Task<object>> run) {
        var job = new FileScanJob { StoreId = storeId, Kind = kind };
        lock (_jobs) {
            if (_jobs.Values.Any(j => j.StoreId == storeId && j.Kind == kind && j.State == FileScanJob.Running))
                throw new Exception("A " + kind + " job is already running for this store. ");
            foreach (var old in _jobs.Values.Where(j => j.State != FileScanJob.Running && DateTime.UtcNow - j.StartedUtc > TimeSpan.FromHours(1)).ToArray())
                _jobs.Remove(old.Id);
            _jobs[job.Id] = job;
        }
        _ = Task.Run(async () => {
            try {
                job.Result = await run(job);
                job.State = FileScanJob.Done;
            } catch (OperationCanceledException) {
                job.State = FileScanJob.Cancelled;
            } catch (Exception e) {
                job.Error = e.Message;
                job.State = FileScanJob.Failed;
            }
        });
        return job;
    }
}
