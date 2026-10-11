using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relatude.DB.Tasks;

/// <summary>
/// The jobs of one database and the scheduler that runs them at set intervals.
///
/// A job is work registered under a name (<see cref="Add(string, Func{JobContext, Task}, string?, string?)"/>);
/// a schedule (<see cref="ScheduleSettings"/>, in the database's settings) says which one to run and
/// when. Every run, scheduled or started by hand, goes through the database's task queue as a batch of
/// <see cref="RunJob"/>, so it waits for its turn, shows on the Tasks page, and is never run
/// beside another run of the same job.
///
/// The scheduler is ticked by the store while it is open. What it has to remember across a restart -
/// when each schedule last came due, and the recent runs - is a small JSON file in the state folder;
/// losing it costs nothing worse than a schedule waiting for its next time and an empty history.
///
/// <para>Thread-safe. Lock order: this object's lock, then the task queue's - never the other way
/// round, and never held while a job runs.</para>
/// </summary>
public sealed class Jobs {
    /// <summary>How many runs are remembered, newest kept: enough to see how the last days went.</summary>
    public const int MaxRunsKept = 200;
    /// <summary>The longest name a job may have.</summary>
    public const int MaxNameLength = 100;
    // a run still waiting in the queue this long after it was put there is checked for: someone may have
    // deleted or cancelled its batch on the Tasks page, which would otherwise leave it "queued" forever
    // and its schedule skipping every later run as "still running"
    static readonly TimeSpan queuedCheckAfter = TimeSpan.FromSeconds(15);
    static readonly TimeSpan minTimeBetweenSaves = TimeSpan.FromSeconds(5);

    readonly IDataStore _store;
    readonly Func<ScheduleSettings[]?> _schedules;
    readonly IIOProvider? _stateIO;
    readonly string[] _stateKey;
    readonly object _lock = new();
    readonly object _saveLock = new();
    readonly Dictionary<string, Job> _jobs = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<Guid, ScheduleState> _states = [];
    readonly List<JobRun> _runs = []; // oldest first
    readonly Dictionary<Guid, CancellationTokenSource> _runTokens = [];
    CancellationTokenSource _stop = new();
    bool _started;
    bool _stateLoaded;
    bool _dirty;
    bool _saveFailing;
    DateTime _lastSave = DateTime.MinValue;

    /// <param name="store">The database the runs are queued in and logged to.</param>
    /// <param name="schedules">The schedules as the settings hold them right now; asked at every tick, so an edit applies at once.</param>
    /// <param name="stateIO">Where the scheduler keeps what it remembers; null remembers nothing past the process.</param>
    /// <param name="stateKey">The file in <paramref name="stateIO"/>.</param>
    public Jobs(IDataStore store, Func<ScheduleSettings[]?> schedules, IIOProvider? stateIO = null, string[]? stateKey = null) {
        _store = store;
        _schedules = schedules;
        _stateIO = stateIO;
        _stateKey = stateKey ?? FileKeyUtility.Scheduler_GetStateFileKey();
    }

    // ---- the jobs ----

    /// <summary>
    /// Registers a job under a name, replacing one already registered under it. Can be called at any
    /// time; the usual place is <c>OnStoreInit</c>, which runs every time the database is set up, before
    /// it opens - so a schedule naming the job finds it from its first tick.
    /// <code>db.Jobs.Add("send-digest", async ctx => { var sent = await Digest.SendAsync(db, ctx.CancellationToken); ctx.Report(sent + " sent"); }, "Mails the daily digest");</code>
    /// </summary>
    /// <param name="name">What schedules call it by. Not case sensitive, at most <see cref="MaxNameLength"/> characters.</param>
    /// <param name="run">The work. It runs on a thread of the task queue, outside any lock of the database.</param>
    /// <param name="description">One or two sentences for the scheduler page.</param>
    /// <param name="argumentHelp">What <see cref="JobContext.Argument"/> means to it, if anything.</param>
    public Job Add(string name, Func<JobContext, Task> run, string? description = null, string? argumentHelp = null)
        => add(name, run, description, argumentHelp, builtIn: false);
    /// <summary>Registers a job whose work is not asynchronous. See <see cref="Add(string, Func{JobContext, Task}, string?, string?)"/>.</summary>
    public Job Add(string name, Action<JobContext> run, string? description = null, string? argumentHelp = null) {
        ArgumentNullException.ThrowIfNull(run);
        return add(name, ctx => { run(ctx); return Task.CompletedTask; }, description, argumentHelp, builtIn: false);
    }
    /// <summary>For the database's own jobs, which the scheduler page marks as built in.</summary>
    internal Job AddBuiltIn(string name, Func<JobContext, Task> run, string description, string? argumentHelp = null)
        => add(name, run, description, argumentHelp, builtIn: true);

    Job add(string name, Func<JobContext, Task> run, string? description, string? argumentHelp, bool builtIn) {
        ArgumentNullException.ThrowIfNull(run);
        var problem = NameProblem(name);
        if (problem != null) throw new ArgumentException(problem, nameof(name));
        var job = new Job(name.Trim(), run, blankToNull(description), blankToNull(argumentHelp), builtIn);
        lock (_lock) _jobs[job.Name] = job;
        return job;
    }

    /// <summary>What is wrong with a job name, or null.</summary>
    public static string? NameProblem(string? name) {
        if (string.IsNullOrWhiteSpace(name)) return "A job needs a name.";
        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength) return "A job name can be at most " + MaxNameLength + " characters.";
        if (trimmed.Any(char.IsControl)) return "A job name cannot hold control characters.";
        return null;
    }

    /// <summary>Takes a job out of the registry. Schedules naming it skip their runs, and say why, until one is registered again.</summary>
    public bool Remove(string name) {
        lock (_lock) return _jobs.Remove(name.Trim());
    }
    public bool TryGet(string name, [MaybeNullWhen(false)] out Job job) {
        lock (_lock) return _jobs.TryGetValue(name.Trim(), out job);
    }
    public bool Contains(string name) => TryGet(name, out _);
    /// <summary>Every registered job, by name.</summary>
    public Job[] All {
        get { lock (_lock) return [.. _jobs.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)]; }
    }

    // ---- running ----

    /// <summary>
    /// Queues a run of a job now, whatever the schedules say. Returns at once with the run as it was
    /// queued; <see cref="GetRun"/> follows it. The database must be open.
    /// </summary>
    public JobRun Run(string name, string? argument = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        requireOpen();
        JobRun run;
        lock (_lock) {
            if (!_jobs.TryGetValue(name.Trim(), out var job)) throw new ArgumentException("No job named \"" + name.Trim() + "\" is registered on this database. ", nameof(name));
            run = newRun(job.Name, argument, null, null, JobTrigger.Manual, null, DateTime.UtcNow);
        }
        enqueue(run);
        return run.Copy();
    }

    /// <summary>Queues a run of a schedule's job with its argument now, as if it had come due - except that
    /// it counts as started by hand, and leaves the schedule's own times as they are.</summary>
    public JobRun RunSchedule(Guid scheduleId) {
        requireOpen();
        var schedule = (_schedules() ?? []).FirstOrDefault(s => s != null && s.Id == scheduleId) ?? throw new ArgumentException("No schedule has the id " + scheduleId + ". ", nameof(scheduleId));
        if (string.IsNullOrWhiteSpace(schedule.Job)) throw new ArgumentException("The schedule names no job to run. ", nameof(scheduleId));
        JobRun run;
        lock (_lock) {
            if (!_jobs.TryGetValue(schedule.Job.Trim(), out var job)) throw new ArgumentException("No job named \"" + schedule.Job.Trim() + "\" is registered on this database. ", nameof(scheduleId));
            run = newRun(job.Name, schedule.Argument, schedule.Id, displayName(schedule), JobTrigger.Manual, null, DateTime.UtcNow);
        }
        enqueue(run);
        return run.Copy();
    }

    /// <summary>
    /// Stops a run: one that is running has its <see cref="JobContext.CancellationToken"/> cancelled
    /// (stopping is up to the job), one still in the queue is taken out of it. False when the run is
    /// not queued or running.
    /// </summary>
    public bool Cancel(Guid runId) {
        CancellationTokenSource? token = null;
        lock (_lock) {
            var run = findRun(runId);
            if (run == null || !run.IsActive) return false;
            if (run.Status == JobRunStatus.Running) {
                if (!_runTokens.TryGetValue(runId, out token)) return false;
            }
        }
        if (token != null) {
            try { token.Cancel(); } catch (ObjectDisposedException) { return false; } // finished meanwhile
            return true;
        }
        // still queued: its batch out of the queue, found by the job id it was queued under
        var queue = _store.TaskQueue;
        var batches = queue.GetBatchMeta([], [typeof(RunJob).FullName!], [groupKeyOf(runId)], 0, 10, out _);
        if (batches.Length > 0) queue.DeleteById([.. batches.Select(b => b.BatchId)]);
        lock (_lock) {
            var run = findRun(runId);
            // it may have started in between, and then it runs: stopping it is a second click
            if (run == null || run.Status != JobRunStatus.Queued) return false;
            finishAs(run, JobRunStatus.Cancelled, "Taken out of the queue before it started.", null, DateTime.UtcNow);
        }
        return true;
    }

    /// <summary>The runs remembered, newest first: runs of every job and schedule, skipped ones included.</summary>
    public JobRun[] Runs(int max = MaxRunsKept) {
        lock (_lock) {
            var take = Math.Clamp(max, 0, _runs.Count);
            var result = new JobRun[take];
            for (var i = 0; i < take; i++) result[i] = _runs[_runs.Count - 1 - i].Copy();
            return result;
        }
    }
    public JobRun? GetRun(Guid runId) {
        lock (_lock) return findRun(runId)?.Copy();
    }
    /// <summary>The run a batch in the task queue stands for, by the group key its batch was queued under; null for any other batch.</summary>
    public JobRun? FindRunByGroupKey(string? groupKey) {
        if (groupKey == null || !Guid.TryParseExact(groupKey, "N", out var runId)) return null;
        return GetRun(runId);
    }

    /// <summary>
    /// Where a schedule stands. A schedule the scheduler has not looked at yet - the database is closed,
    /// or it was added a moment ago - gets its next time worked out here, from now.
    /// </summary>
    public ScheduleStatus GetStatus(ScheduleSettings schedule, DateTime nowUtc) {
        lock (_lock) {
            var last = lastRunOf(schedule.Id);
            var active = _runs.Any(r => r.ScheduleId == schedule.Id && r.IsActive);
            string? problem;
            DateTime? next = null;
            if (_states.TryGetValue(schedule.Id, out var state) && state.Signature == signature(schedule) && _started) {
                problem = state.Problem;
                next = schedule.Enabled ? state.Next : null;
            } else {
                problem = ScheduleTimes.Problem(schedule);
                if (problem == null && schedule.Enabled) {
                    try { next = ScheduleTimes.Next(schedule, nowUtc); } catch (ScheduleProblemException e) { problem = e.Message; }
                }
            }
            problem ??= jobProblem(schedule);
            return new ScheduleStatus { ScheduleId = schedule.Id, NextUtc = problem == null ? next : null, Problem = problem, LastRun = last?.Copy(), Active = active };
        }
    }

    // ---- the scheduler ----

    /// <summary>The scheduler starts: called by the store as it opens. Reads what was remembered the first time.</summary>
    internal void Start() {
        lock (_lock) {
            if (!_stateLoaded) {
                load();
                _stateLoaded = true;
            }
            // queued or running when the database last closed: the memory queue did not keep it
            var now = DateTime.UtcNow;
            foreach (var run in _runs) {
                if (run.IsActive && !_runTokens.ContainsKey(run.RunId)) finishAs(run, JobRunStatus.Interrupted, "The database closed before it finished.", null, now);
            }
            if (_stop.IsCancellationRequested) {
                _stop.Dispose();
                _stop = new CancellationTokenSource();
            }
            _started = true;
        }
    }

    /// <summary>The scheduler stops: called by the store as it closes. Jobs still running are asked to stop,
    /// and what the scheduler remembers is written.</summary>
    internal void Stop() {
        CancellationTokenSource stop;
        lock (_lock) {
            _started = false;
            stop = _stop;
        }
        // outside the lock: a job's own cancellation callbacks run here
        try { stop.Cancel(); } catch { }
        save(force: true);
    }

    /// <summary>
    /// One look at every schedule: the ones that have come due are queued, or skipped with the reason.
    /// Called by the store about once a second while it is open.
    /// </summary>
    internal void Tick(DateTime nowUtc) {
        if (!_started) return;
        var schedules = _schedules() ?? [];
        var queue = new List<JobRun>();
        lock (_lock) {
            if (!_started) return;
            checkQueuedRuns(nowUtc);
            var seen = new HashSet<Guid>();
            foreach (var schedule in schedules) {
                if (schedule == null || schedule.Id == Guid.Empty || !seen.Add(schedule.Id)) continue;
                var run = tick(schedule, nowUtc);
                if (run != null) queue.Add(run);
            }
            // what a removed schedule left behind is not needed any more; its runs stay in the history
            foreach (var id in _states.Keys.Where(id => !seen.Contains(id)).ToArray()) {
                _states.Remove(id);
                _dirty = true;
            }
        }
        foreach (var run in queue) enqueue(run);
        save(force: false);
    }

    /// <summary>One schedule's look, under the lock: the run to queue, or null.</summary>
    JobRun? tick(ScheduleSettings schedule, DateTime now) {
        if (!_states.TryGetValue(schedule.Id, out var state)) {
            state = new ScheduleState();
            _states[schedule.Id] = state;
        }
        var sig = signature(schedule);
        if (state.Signature != sig) {
            // new, or its timing changed: it starts from now, so an edit that moves a daily run to an
            // earlier hour does not count the hour just passed as a run missed. Turned on again likewise
            state.Signature = sig;
            state.LastSlotUtc = now;
            state.Next = null;
            state.Compiled = null;
            state.Problem = null;
            _dirty = true;
        }
        if (!schedule.Enabled) return null;
        if (state.Compiled == null && state.Problem == null) {
            try {
                state.Compiled = ScheduleTimes.Compile(schedule);
            } catch (ScheduleProblemException e) {
                state.Problem = e.Message;
            }
        }
        // a schedule that cannot run says so until it is edited, which starts it over (above)
        if (state.Compiled == null || state.Problem != null) return null;
        state.LastSlotUtc ??= now;
        if (state.Next == null) {
            state.Next = state.Compiled.Next(state.LastSlotUtc.Value);
            if (state.Next == null) state.Problem = "This schedule never runs: no day matches all of its rules.";
        }
        if (state.Next is not DateTime due || now < due) return null;
        // due. After a stop the first time missed is far back; the one that counts is the latest one
        // that has passed, and the schedule then goes on from it
        var latest = latestDue(schedule, state.Compiled, due, now);
        state.LastSlotUtc = latest;
        state.Next = state.Compiled.Next(latest);
        _dirty = true;
        var onTime = now - latest <= ScheduleTimes.Grace(schedule);
        string? skip = null;
        if (!onTime && !schedule.RunMissed) {
            skip = "Missed: the database was not open when it was due, and the schedule does not run missed runs.";
        } else if (string.IsNullOrWhiteSpace(schedule.Job)) {
            skip = "The schedule names no job to run.";
        } else if (!_jobs.ContainsKey(schedule.Job.Trim())) {
            skip = "No job named \"" + schedule.Job.Trim() + "\" is registered on this database.";
        } else if (_runs.Any(r => r.ScheduleId == schedule.Id && r.IsActive)) {
            skip = "The previous run had not finished.";
        }
        if (skip != null) {
            recordSkipped(schedule, latest, skip, now);
            return null;
        }
        var name = _jobs[schedule.Job!.Trim()].Name;
        return newRun(name, schedule.Argument, schedule.Id, displayName(schedule), onTime ? JobTrigger.Schedule : JobTrigger.Missed, latest, now);
    }

    /// <summary>
    /// The latest time the schedule was due at or before <paramref name="now"/>, given that <paramref name="firstDue"/>
    /// is one it was due at. Looked for within one period back, which holds the latest one unless the
    /// schedule skipped days; then the first one missed stands in, and is late either way.
    /// </summary>
    static DateTime latestDue(ScheduleSettings schedule, CompiledSchedule compiled, DateTime firstDue, DateTime now) {
        var latest = firstDue;
        var lookFrom = now - ScheduleTimes.NominalPeriod(schedule);
        if (lookFrom > latest) {
            var recent = compiled.Next(lookFrom);
            if (recent is DateTime r && r <= now) latest = r;
        }
        // walk forward to the last one not after now; a handful of steps at most
        for (var i = 0; i < 1000; i++) {
            var next = compiled.Next(latest);
            if (next is not DateTime n || n > now) break;
            latest = n;
        }
        return latest;
    }

    void recordSkipped(ScheduleSettings schedule, DateTime due, string reason, DateTime now) {
        var last = lastRunOf(schedule.Id);
        if (last != null && last.Status == JobRunStatus.Skipped && last.Message == reason) {
            last.Times++;
            last.DueUtc = due;
            last.QueuedUtc = now;
            last.FinishedUtc = now;
            return;
        }
        var run = new JobRun {
            RunId = Guid.NewGuid(),
            JobName = schedule.Job?.Trim() ?? "",
            Argument = blankToNull(schedule.Argument),
            ScheduleId = schedule.Id,
            ScheduleName = displayName(schedule),
            Trigger = JobTrigger.Schedule,
            Status = JobRunStatus.Skipped,
            QueuedUtc = now,
            DueUtc = due,
            FinishedUtc = now,
            Message = reason,
        };
        addRun(run);
        _store.LogInfo("Schedule \"" + run.ScheduleName + "\" skipped a run: " + reason);
    }

    /// <summary>Runs left "queued" whose batch is gone from the queue, or cancelled there, are cancelled here too.</summary>
    void checkQueuedRuns(DateTime now) {
        foreach (var run in _runs) {
            if (run.Status != JobRunStatus.Queued || now - run.QueuedUtc < queuedCheckAfter) continue;
            var batches = _store.TaskQueue.GetBatchMeta([], [typeof(RunJob).FullName!], [groupKeyOf(run.RunId)], 0, 1, out var count);
            if (count == 0) {
                finishAs(run, JobRunStatus.Cancelled, "Taken out of the task queue before it started.", null, now);
            } else if (batches.Length > 0 && batches[0].State is BatchState.Cancelled or BatchState.Failed or BatchState.AbortedOnStartup) {
                finishAs(run, JobRunStatus.Cancelled, "Its batch was " + batches[0].State.ToString().Decamelize().ToLowerInvariant() + " in the task queue.", null, now);
            }
        }
    }

    // ---- a run, from the queue ----

    /// <summary>Called by <see cref="RunJobRunner"/>: runs the job and records how it went - in the run
    /// history and, when the Tasks log is on, as one entry there. Throws when the job failed, so the queue
    /// marks its batch as failed with the reason.</summary>
    internal async Task ExecuteAsync(RunJob data, TaskLogger? taskLogger = null) {
        Job? job;
        JobRun run;
        JobRun snapshot;
        CancellationTokenSource token;
        lock (_lock) {
            run = findRun(data.RunId) ?? addRun(new JobRun {
                // queued before the history was cleared, or by code that wrote the task data itself
                RunId = data.RunId == Guid.Empty ? Guid.NewGuid() : data.RunId,
                JobName = data.JobName,
                Argument = data.Argument,
                ScheduleId = data.ScheduleId,
                ScheduleName = data.ScheduleName,
                Trigger = data.Trigger,
                DueUtc = data.DueUtc,
                QueuedUtc = DateTime.UtcNow,
            });
            _jobs.TryGetValue(data.JobName.Trim(), out job);
            // a batch put back in line on the Tasks page runs again under the same run
            run.Status = JobRunStatus.Running;
            run.StartedUtc = DateTime.UtcNow;
            run.FinishedUtc = null;
            run.Message = null;
            run.Error = null;
            token = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            _runTokens[run.RunId] = token;
            snapshot = run.Copy();
            _dirty = true;
        }
        var sw = Stopwatch.StartNew();
        // the activity the queue lists this batch under, which the job's progress is shown on; run
        // outside a queue, the job gets one of its own
        var activityId = TaskQueue.CurrentBatchActivityId;
        var ownActivity = false;
        Exception? failure = null;
        var cancelled = false;
        try {
            if (job == null) throw new Exception("No job named \"" + data.JobName + "\" is registered on this database.");
            if (activityId <= 0) {
                activityId = _store.RegisterActvity(DataStoreActivityCategory.RunningTask, "Job " + job.Name);
                ownActivity = true;
            }
            await job.Run(new JobContext(this, _store, snapshot, activityId, token.Token));
        } catch (OperationCanceledException) when (token.IsCancellationRequested) {
            cancelled = true;
        } catch (Exception e) {
            failure = e;
        } finally {
            if (ownActivity) {
                try { _store.DeRegisterActivity(activityId); } catch { }
            }
        }
        string? message;
        JobRun outcome;
        var closing = _stop.IsCancellationRequested;
        lock (_lock) {
            _runTokens.Remove(run.RunId);
            var now = DateTime.UtcNow;
            if (failure != null) {
                finishAs(run, JobRunStatus.Failed, run.Message, failure.Message, now);
            } else if (cancelled) {
                finishAs(run, JobRunStatus.Cancelled, closing ? "Stopped: the database closed before it finished." : "Stopped before it finished.", null, now);
            } else {
                finishAs(run, JobRunStatus.Succeeded, run.Message, null, now);
            }
            message = run.Message;
            outcome = run.Copy();
        }
        token.Dispose();
        var took = sw.ElapsedMilliseconds.To1000N() + "ms";
        try {
            // a stop is not a failure: only a job that threw counts as one in the log's statistics
            taskLogger?.Invoke(failure == null, data.JobName, logDetails(outcome, took));
        } catch {
            // the log is not the run
        }
        if (failure != null) {
            // the queue logs the error itself, with this message and the exception under it
            throw new Exception("Job \"" + data.JobName + "\" failed after " + took + ": " + failure.Message, failure);
        }
        _store.LogInfo("Job \"" + data.JobName + "\" " + (cancelled ? "was stopped after " : "finished in ") + took + (message == null ? "." : ": " + message));
    }

    /// <summary>A run in a line, for the Tasks log: "by hand · count · 6ms · Found 0 unreferenced files".</summary>
    static string logDetails(JobRun run, string took) {
        var parts = new List<string>();
        var schedule = run.ScheduleName != null && !string.Equals(run.ScheduleName, run.JobName, StringComparison.OrdinalIgnoreCase) ? run.ScheduleName : null;
        parts.Add(run.Trigger switch {
            JobTrigger.Manual => schedule == null ? "by hand" : "by hand, " + schedule,
            JobTrigger.Missed => (schedule ?? "schedule") + ", missed run",
            _ => schedule ?? "on schedule",
        });
        if (run.Argument != null) parts.Add(run.Argument);
        parts.Add(took);
        if (run.Status == JobRunStatus.Cancelled) parts.Add("stopped");
        var said = run.Error ?? run.Message;
        if (!string.IsNullOrEmpty(said)) parts.Add(said);
        return string.Join(" · ", parts);
    }

    internal void Report(Guid runId, string? message) {
        lock (_lock) {
            var run = findRun(runId);
            if (run == null) return;
            run.Message = message == null ? null : message.Length > 1000 ? message[..1000] + "…" : message;
            _dirty = true;
        }
    }

    // ---- pieces ----

    void requireOpen() {
        if (_store.State != DataStoreState.Open) throw new InvalidOperationException("The database must be open to run a job. ");
    }

    JobRun newRun(string jobName, string? argument, Guid? scheduleId, string? scheduleName, JobTrigger trigger, DateTime? due, DateTime now) {
        return addRun(new JobRun {
            RunId = Guid.NewGuid(),
            JobName = jobName,
            Argument = blankToNull(argument),
            ScheduleId = scheduleId,
            ScheduleName = scheduleName,
            Trigger = trigger,
            Status = JobRunStatus.Queued,
            QueuedUtc = now,
            DueUtc = due,
        });
    }

    /// <summary>Puts a run in the task queue, outside the lock. A run the queue refuses is a failed run.</summary>
    void enqueue(JobRun run) {
        try {
            _store.EnqueueTask(new RunJob {
                RunId = run.RunId,
                JobName = run.JobName,
                Argument = run.Argument,
                ScheduleId = run.ScheduleId,
                ScheduleName = run.ScheduleName,
                DueUtc = run.DueUtc,
                Trigger = run.Trigger,
            }, groupKeyOf(run.RunId));
        } catch (Exception e) {
            lock (_lock) {
                var live = findRun(run.RunId);
                if (live != null) finishAs(live, JobRunStatus.Failed, null, "Could not be queued: " + e.Message, DateTime.UtcNow);
            }
            _store.LogError("Job \"" + run.JobName + "\" could not be queued", e);
        }
    }

    static string groupKeyOf(Guid runId) => runId.ToString("N");

    JobRun addRun(JobRun run) {
        _runs.Add(run);
        // the oldest finished ones make room; a run still queued or running is kept whatever its age
        for (var i = 0; _runs.Count > MaxRunsKept && i < _runs.Count;) {
            if (_runs[i].IsActive) i++;
            else _runs.RemoveAt(i);
        }
        _dirty = true;
        return run;
    }

    void finishAs(JobRun run, JobRunStatus status, string? message, string? error, DateTime now) {
        run.Status = status;
        run.Message = message;
        run.Error = error;
        run.FinishedUtc = now;
        _dirty = true;
    }

    JobRun? findRun(Guid runId) {
        for (var i = _runs.Count - 1; i >= 0; i--) if (_runs[i].RunId == runId) return _runs[i];
        return null;
    }

    JobRun? lastRunOf(Guid scheduleId) {
        for (var i = _runs.Count - 1; i >= 0; i--) if (_runs[i].ScheduleId == scheduleId) return _runs[i];
        return null;
    }

    string? jobProblem(ScheduleSettings schedule) {
        if (string.IsNullOrWhiteSpace(schedule.Job)) return "Choose the job to run.";
        // the registry is only filled while the database exists; a closed one has no say yet
        if (_jobs.Count > 0 && !_jobs.ContainsKey(schedule.Job.Trim())) return "No job named \"" + schedule.Job.Trim() + "\" is registered on this database.";
        return null;
    }

    static string displayName(ScheduleSettings schedule) => string.IsNullOrWhiteSpace(schedule.Name) ? schedule.Job?.Trim() ?? "" : schedule.Name.Trim();

    /// <summary>The fields that decide when a schedule runs; a change to any of them starts it over from now.</summary>
    static string signature(ScheduleSettings s) => string.Join("|", s.Enabled, s.Every, s.Unit, s.At?.Trim(), s.Until?.Trim(), s.Days?.Trim(), s.DayOfMonth, s.TimeZone?.Trim());

    static string? blankToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    sealed class ScheduleState {
        public string? Signature;
        public DateTime? LastSlotUtc;
        public DateTime? Next;
        public CompiledSchedule? Compiled;
        public string? Problem;
    }

    // ---- what is remembered across a restart ----

    static readonly JsonSerializerOptions stateJson = new() {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    sealed class StateFile {
        public int Version { get; set; } = 1;
        public List<ScheduleStateFile> Schedules { get; set; } = [];
        public List<JobRun> Runs { get; set; } = [];
    }
    sealed class ScheduleStateFile {
        public Guid Id { get; set; }
        public string? Signature { get; set; }
        public DateTime? LastSlotUtc { get; set; }
    }

    void load() {
        if (_stateIO == null) return;
        try {
            if (_stateIO.DoesNotExistsOrIsEmpty(_stateKey)) return;
            var file = JsonSerializer.Deserialize<StateFile>(_stateIO.ReadAllTextUTF8(_stateKey), stateJson);
            if (file == null) return;
            foreach (var s in file.Schedules) {
                if (s.Id == Guid.Empty) continue;
                _states[s.Id] = new ScheduleState { Signature = s.Signature, LastSlotUtc = s.LastSlotUtc is DateTime d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null };
            }
            foreach (var run in file.Runs.Where(r => r.RunId != Guid.Empty).TakeLast(MaxRunsKept)) _runs.Add(run);
        } catch (Exception e) {
            // a schedule then waits for its next time, and the history starts over: nothing worse
            _store.LogWarning("The scheduler's state file " + _stateKey.AsKeyString() + " could not be read and is started over: " + e.Message);
        }
    }

    void save(bool force) {
        if (_stateIO == null) return;
        string json;
        lock (_lock) {
            if (!_dirty) return;
            if (!force && DateTime.UtcNow - _lastSave < minTimeBetweenSaves) return;
            var file = new StateFile {
                Schedules = [.. _states.Select(kv => new ScheduleStateFile { Id = kv.Key, Signature = kv.Value.Signature, LastSlotUtc = kv.Value.LastSlotUtc })],
                Runs = [.. _runs.Select(r => r.Copy())],
            };
            json = JsonSerializer.Serialize(file, stateJson);
            _dirty = false;
            _lastSave = DateTime.UtcNow;
        }
        lock (_saveLock) {
            try {
                _stateIO.EnsureFolder(_stateKey[..^1]);
                _stateIO.WriteAllTextUTF8(_stateKey, json);
                _saveFailing = false;
            } catch (Exception e) {
                lock (_lock) _dirty = true; // tried again at a later tick
                if (!_saveFailing) _store.LogWarning("The scheduler's state file " + _stateKey.AsKeyString() + " could not be written: " + e.Message);
                _saveFailing = true;
            }
        }
    }
}
