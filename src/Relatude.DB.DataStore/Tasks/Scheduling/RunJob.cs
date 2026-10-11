using System.Text.Json;

namespace Relatude.DB.Tasks;

/// <summary>
/// One run of a job, as the task queue holds it. Queued by <see cref="Jobs"/> - for a
/// schedule that came due or a run started by hand - under the run's id as group key, one run per batch.
/// </summary>
public sealed class RunJob : TaskData {
    public Guid RunId { get; set; }
    public string JobName { get; set; } = "";
    public string? Argument { get; set; }
    public Guid? ScheduleId { get; set; }
    public string? ScheduleName { get; set; }
    public DateTime? DueUtc { get; set; }
    public JobTrigger Trigger { get; set; }
}

/// <summary>
/// Runs <see cref="RunJob"/> batches by handing them to the <see cref="Jobs"/> they came
/// from. In the memory queue: a run is not worth keeping across a restart, since its schedule comes
/// due again - and one that was missed is caught up by the scheduler, not by the queue.
/// <para>Up to four different jobs run at once (a setting, <c>LocalSettings.TaskConcurrency</c>, can
/// change that); two runs of the same job never do - the second waits in the queue for the first. A
/// run that takes hours holds its own slot and nothing else: the queue's later rounds start backups and
/// log rewrites beside it.</para>
/// <para>Each run writes one entry to the Tasks log of the Activity page, by the job's name, with how it
/// was started, how long it took and what it reported.</para>
/// </summary>
public sealed class RunJobRunner(Jobs jobs) : TaskRunner<RunJob> {
    public override BatchTaskPriority Priority => BatchTaskPriority.Medium;
    public override async Task ExecuteAsync(Batch<RunJob> batch, TaskLogger? taskLogger) {
        foreach (var run in batch.Tasks) await jobs.ExecuteAsync(run, taskLogger);
    }
    public override string? GetTaskLogId(RunJob run) => run.JobName;
    public override string? DescribeTask(RunJob run) => run.JobName;
    public override bool PersistToDisk => false;
    public override bool DeleteOnSuccess => true;
    public override int MaxTaskCountPerBatch => 1;
    public override int MaxConcurrency => 4;
    public override string? GetConcurrencyKey(RunJob run) => "job:" + run.JobName.Trim().ToLowerInvariant();
    public override bool RestartTaskBatchesOnStartupThatStartedButNeverFailedOrCompleted => false;
    // a failed run stays on the Tasks page for a day, where it can be put back in line
    public override TimeSpan GetMaximumAgeInQueueAfterExecution() => TimeSpan.FromHours(24);
    public override byte[] TaskToBytes(RunJob run) => JsonSerializer.SerializeToUtf8Bytes(run);
    public override RunJob TaskFromBytes(byte[] bytes) => JsonSerializer.Deserialize<RunJob>(bytes) ?? throw new Exception("Not a job run.");
}
