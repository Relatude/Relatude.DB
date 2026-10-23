using System.Runtime.InteropServices;
using Relatude.DB.IO;
using Relatude.DB.NodeServer.API;

namespace Relatude.DB.NodeServer.UI;

sealed class UIStorageBenchmark {
    const string jobKind = "storage benchmark";
    const int minimumDurationMs = 1000;
    const int maximumDurationMs = 300_000;
    static readonly Dictionary<Guid, BenchmarkRun> _latest = [];
    readonly RelatudeDBServer _server;
    readonly UISharedTasks _shared;

    internal UIStorageBenchmark(RelatudeDBServer server, UISharedTasks shared) {
        _server = server;
        _shared = shared;
    }

    internal void Register(UICommands commands) {
        commands.Register("io-benchmark-start", ctx => start(ctx.Payload<BenchmarkStartPayload>(), ctx));
        commands.Register("io-benchmark-progress", ctx => progress(ctx.Payload<BenchmarkJobPayload>()));
        commands.Register("io-benchmark-cancel", ctx => {
            FileScanJobs.Get(ctx.Payload<BenchmarkJobPayload>().JobId).Cancellation.Cancel();
            return (object?)new { Cancelled = true };
        });
        commands.Register("io-benchmark-latest", ctx => latest(ctx.Payload<BenchmarkStorePayload>().StoreId));
    }

    object start(BenchmarkStartPayload p, UICommandContext ctx) {
        var choices = storagesOf(container(p.StoreId));
        var picked = (p.IoIds ?? []).Distinct()
            .Select(id => choices.FirstOrDefault(s => s.Id == id) ?? throw new Exception("One of the storages picked does not belong to this database. "))
            .ToList();
        if (picked.Count == 0) throw new Exception("Pick at least one storage to test. ");
        var durationMs = Math.Clamp(p.DurationMs, minimumDurationMs, maximumDurationMs);
        BenchmarkRun? finished = null;
        var job = FileScanJobs.Start(p.StoreId, jobKind, async j => finished = await runAsync(p.StoreId, picked, durationMs, j));
        var title = picked.Count == 1 ? "Benchmark " + picked[0].Name : $"Benchmark {picked.Count} storages";
        _shared.Attach(_shared.RefOf(ctx, p.StoreId, title), () => sharedProgress(job, finished), job.Cancellation.Cancel);
        return new { JobId = job.Id };
    }

    static SharedTaskProgress sharedProgress(FileScanJob job, BenchmarkRun? finished) => job.State switch {
        FileScanJob.Done when finished?.Cancelled == true => new(UISharedTasks.Cancelled, null, job.Percent, 100, null, "Cancelled. What was measured until then is in the report."),
        FileScanJob.Done => new(UISharedTasks.Done, null, 100, 100, null, "Done. The report is on the Storage page."),
        FileScanJob.Failed => new(UISharedTasks.Failed, null, job.Percent, 100, null, job.Error),
        FileScanJob.Cancelled => new(UISharedTasks.Cancelled, null, job.Percent, 100, null, "Cancelled."),
        _ => new(UISharedTasks.Running, job.Description, job.Percent, 100, job.Percent + "%", null),
    };

    object progress(BenchmarkJobPayload p) {
        var job = FileScanJobs.Get(p.JobId);
        var state = job.State;
        return new {
            State = state,
            job.Description,
            job.Percent,
            job.Error,
            Result = state == FileScanJob.Done ? job.Result : null,
        };
    }

    static object? latest(Guid storeId) {
        lock (_latest) return _latest.TryGetValue(storeId, out var run) ? run : null;
    }

    async Task<BenchmarkRun> runAsync(Guid storeId, List<StorageChoice> storages, int durationMs, FileScanJob job) {
        var startedUtc = DateTime.UtcNow;
        var results = new List<StorageResult>();
        for (var i = 0; i < storages.Count; i++) {
            if (job.Cancellation.IsCancellationRequested) break;
            var storage = storages[i];
            var index = i;
            var label = storages.Count > 1 ? $"{storage.Name} ({i + 1} of {storages.Count})" : storage.Name;
            job.SetProgress(label + ": starting", percentOf(index, 0, storages.Count));
            var progress = new JobProgress(p => job.SetProgress(label + ": " + p.Step, percentOf(index, p.Percent, storages.Count)));
            IOBenchmarkReport report;
            try {
                report = await _server.GetIO(storage.Id).BenchmarkPerformanceAsync(durationMs, progress, job.Cancellation.Token);
            } catch (Exception error) {
                report = new IOBenchmarkReport { Kind = storage.Type, StartedUtc = DateTime.UtcNow, TestDurationMs = durationMs, Error = error.Message };
            }
            results.Add(new StorageResult(storage.Id, storage.Name, storage.Type, report));
        }
        var run = new BenchmarkRun(storeId, startedUtc, DateTime.UtcNow, durationMs, job.Cancellation.IsCancellationRequested, machine(), results);
        if (results.Count > 0) {
            lock (_latest) _latest[storeId] = run;
        }
        return run;
    }

    static int percentOf(int index, double percent, int count) => (int)Math.Clamp((index + percent / 100) / count * 100, 0, 100);

    static MachineFacts machine() => new(Environment.MachineName, RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture.ToString(),
        Environment.ProcessorCount, RuntimeInformation.FrameworkDescription);

    List<StorageChoice> storagesOf(NodeStoreContainer c) {
        var list = (c.Settings.IOSettings ?? [])
            .Select(io => new StorageChoice(io.Id, string.IsNullOrEmpty(io.Name) ? io.IOType.ToString() : io.Name, io.IOType.ToString()))
            .ToList();
        list.Add(new StorageChoice(RelatudeDBServer.ProjectRootIOId, "[Server root]", IOTypes.LocalDisk.ToString()));
        return list;
    }

    NodeStoreContainer container(Guid storeId) {
        if (!_server.Containers.TryGetValue(storeId, out var c)) throw new Exception("Database not found. ");
        return c;
    }

    sealed class JobProgress(Action<IOBenchmarkProgress> report) : IProgress<IOBenchmarkProgress> {
        public void Report(IOBenchmarkProgress value) => report(value);
    }

    sealed record StorageChoice(Guid Id, string Name, string Type);
    sealed record StorageResult(Guid IoId, string Name, string Type, IOBenchmarkReport Report);
    sealed record MachineFacts(string Name, string Os, string Architecture, int Processors, string Runtime);
    sealed record BenchmarkRun(Guid StoreId, DateTime StartedUtc, DateTime FinishedUtc, int DurationMs, bool Cancelled, MachineFacts Machine, List<StorageResult> Storages);
    sealed record BenchmarkStartPayload(Guid StoreId, Guid[]? IoIds, int DurationMs = 5000);
    sealed record BenchmarkJobPayload(Guid JobId);
    sealed record BenchmarkStorePayload(Guid StoreId);
}
