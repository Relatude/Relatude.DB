using Relatude.DB.DataStores;
using System.Text.Json.Serialization;

namespace Relatude.DB.Tasks;

/// <summary>
/// A piece of work a database knows by name, so a schedule in the settings file - or someone in the
/// admin UI - can ask for it without code: <c>"Job": "backup"</c>. The database registers its own
/// (<see cref="BuiltIn"/>); application code adds more with <see cref="Jobs.Add(string, Func{JobContext, Task}, string?, string?)"/>.
/// </summary>
public sealed class Job {
    internal Job(string name, Func<JobContext, Task> run, string? description, string? argumentHelp, bool builtIn) {
        Name = name;
        Run = run;
        Description = description;
        ArgumentHelp = argumentHelp;
        BuiltIn = builtIn;
    }
    /// <summary>What a schedule names it by. Not case sensitive.</summary>
    public string Name { get; }
    /// <summary>One or two sentences on what it does, for the scheduler page.</summary>
    public string? Description { get; }
    /// <summary>What the argument means to this job; null when it takes none.</summary>
    public string? ArgumentHelp { get; }
    /// <summary>Registered by the database itself rather than by the application's code.</summary>
    public bool BuiltIn { get; }
    internal Func<JobContext, Task> Run { get; }
    public override string ToString() => Name;
}

/// <summary>What started a run of a job.</summary>
public enum JobTrigger {
    /// <summary>Its schedule, on time.</summary>
    Schedule = 0,
    /// <summary>Its schedule, late: the time it was due came while the database was closed (<see cref="ScheduleSettings.RunMissed"/>).</summary>
    Missed = 1,
    /// <summary>Someone, from the admin UI or from code (<see cref="Jobs.Run"/>).</summary>
    Manual = 2,
}

public enum JobRunStatus {
    /// <summary>In the task queue, waiting for its turn.</summary>
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    /// <summary>Stopped before it finished, or taken out of the queue before it started.</summary>
    Cancelled = 4,
    /// <summary>Its time came and it was not started: the reason is in <see cref="JobRun.Message"/>.</summary>
    Skipped = 5,
    /// <summary>Queued or running when the database closed; the memory queue does not keep it.</summary>
    Interrupted = 6,
}

/// <summary>One run of a job, or one time a schedule came due and was skipped - what the scheduler
/// page lists under the runs, and keeps across a restart.</summary>
public sealed class JobRun {
    public Guid RunId { get; set; }
    public string JobName { get; set; } = "";
    public string? Argument { get; set; }
    /// <summary>The schedule that started it; null for a run started by hand that names no schedule.</summary>
    public Guid? ScheduleId { get; set; }
    public string? ScheduleName { get; set; }
    public JobTrigger Trigger { get; set; }
    public JobRunStatus Status { get; set; }
    /// <summary>When it was put in the queue - or, for a skipped one, when it was skipped.</summary>
    public DateTime QueuedUtc { get; set; }
    /// <summary>The time its schedule said it should run; null for a run started by hand.</summary>
    public DateTime? DueUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    /// <summary>What the job said about how it went (<see cref="JobContext.Report"/>), or why it was skipped.</summary>
    public string? Message { get; set; }
    public string? Error { get; set; }
    /// <summary>A schedule skipped again and again for the same reason - a job nobody has registered yet,
    /// every five seconds - is one entry, counted here, rather than a list that pushes out everything else.</summary>
    public int Times { get; set; } = 1;
    [JsonIgnore] public double? DurationMs => StartedUtc is DateTime s && FinishedUtc is DateTime f ? (f - s).TotalMilliseconds : null;
    [JsonIgnore] public bool IsActive => Status is JobRunStatus.Queued or JobRunStatus.Running;
    internal JobRun Copy() => (JobRun)MemberwiseClone();
}

/// <summary>Where one schedule stands: when it runs next, how it went last time, and whether it can run at all.</summary>
public sealed class ScheduleStatus {
    public Guid ScheduleId { get; init; }
    /// <summary>The next time it runs; null when it is turned off or cannot run (<see cref="Problem"/>).</summary>
    public DateTime? NextUtc { get; init; }
    /// <summary>What stops it from running as written - a time zone the server does not know, a job
    /// that is not registered - or null.</summary>
    public string? Problem { get; init; }
    /// <summary>Its newest run, skipped ones included.</summary>
    public JobRun? LastRun { get; init; }
    /// <summary>A run of it is queued or running right now.</summary>
    public bool Active { get; init; }
}

/// <summary>What a job is handed when it runs.</summary>
public sealed class JobContext {
    readonly Jobs _owner;
    readonly IDataStore _store;
    readonly long _activityId;
    internal JobContext(Jobs owner, IDataStore store, JobRun run, long activityId, CancellationToken cancellationToken) {
        _owner = owner;
        _store = store;
        _activityId = activityId;
        RunId = run.RunId;
        JobName = run.JobName;
        Argument = run.Argument;
        ScheduleId = run.ScheduleId;
        ScheduleName = run.ScheduleName;
        DueUtc = run.DueUtc;
        Trigger = run.Trigger;
        CancellationToken = cancellationToken;
    }
    public Guid RunId { get; }
    public string JobName { get; }
    /// <summary>The schedule's <see cref="ScheduleSettings.Argument"/>, or what was passed to <see cref="Jobs.Run"/>.</summary>
    public string? Argument { get; }
    public Guid? ScheduleId { get; }
    public string? ScheduleName { get; }
    /// <summary>The time the schedule said it should run; null when started by hand.</summary>
    public DateTime? DueUtc { get; }
    public JobTrigger Trigger { get; }
    /// <summary>
    /// Cancelled when the database closes or someone stops the run from the scheduler page. A job that
    /// takes more than a moment should pass it on or check it: a closing database waits only a few
    /// seconds for the jobs still running.
    /// </summary>
    public CancellationToken CancellationToken { get; }
    /// <summary>How it went, in a line: the scheduler page shows it beside the run, and the system log
    /// says it when the job finishes. The last call counts.</summary>
    public void Report(string? message) => _owner.Report(RunId, message);
    /// <summary>How far it has come, 0 to 100, with an optional line on what it is doing: shown with the
    /// database's other activities while it runs.</summary>
    public void Progress(int percentage, string? text = null) {
        if (_activityId <= 0) return;
        _store.UpdateActivity(_activityId, text == null ? "Job " + JobName : "Job " + JobName + ": " + text, Math.Clamp(percentage, 0, 100));
    }
}
