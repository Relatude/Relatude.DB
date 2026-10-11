using Relatude.DB.NodeServer.Settings;
using Relatude.DB.Tasks;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// The scheduler page of the Tasks section: a database's schedules - which job runs when - the
/// jobs it can run, and how the recent runs went.
///
/// The schedules are settings (<c>LocalSettings.Schedules</c>), and are saved exactly as the settings page
/// saves its lists, through <see cref="UISettings"/>: into relatude.db.overrides.json, refused when
/// configuration or the application's code decides the list, checked field by field against the
/// catalog. So every schedule says where it comes from - SHARED, THIS SERVER - and the page's Move to
/// shared button moves it like any other setting. What the scheduler has made of them - the next time,
/// the runs - comes from the open database's <see cref="Jobs"/>; a closed database still lists and
/// edits its schedules, with the next times worked out here.
/// </summary>
sealed class UIScheduler {
    const string listPath = "LocalSettings.Schedules";
    const int runsShown = 100;
    const int previewCount = 5;
    // the fields the page edits, by the names the catalog gives them; anything else posted is refused
    static readonly string[] fields = ["Job", "Name", "Argument", "Enabled", "Every", "Unit", "At", "Until", "Days", "DayOfMonth", "TimeZone", "RunMissed"];

    readonly RelatudeDBServer _server;
    readonly UISettings _settings;
    internal UIScheduler(RelatudeDBServer server, UISettings settings) {
        _server = server;
        _settings = settings;
    }

    internal void Register(UICommands commands) {
        commands.Register("scheduler", ctx => page(ctx.Payload<StorePayload>().StoreId));
        commands.Register("scheduler-add", ctx => add(ctx.Payload<SchedulePayload>()));
        commands.Register("scheduler-save", ctx => save(ctx.Payload<SchedulePayload>()));
        commands.Register("scheduler-remove", ctx => remove(ctx.Payload<ScheduleIdPayload>()));
        commands.Register("scheduler-run", ctx => run(ctx.Payload<RunPayload>()));
        commands.Register("scheduler-cancel", ctx => cancel(ctx.Payload<CancelPayload>()));
        commands.Register("scheduler-preview", ctx => preview(ctx.Payload<SchedulePayload>()));
    }

    // ---- the page ----

    object page(Guid storeId) {
        var c = container(storeId);
        var local = c.Settings.LocalSettings;
        var schedules = (local?.Schedules ?? []).Where(s => s != null).ToArray();
        var jobs = c.IsOpen() ? c.Store?.Datastore.Jobs : null;
        var now = DateTime.UtcNow;
        var file = _server.OverridesFile;
        var lockedBy = lockedKind(storeId);
        var runs = jobs?.Runs(runsShown) ?? [];
        return new {
            Open = jobs != null,
            State = c.StateName,
            // a run goes through the task queue: with the queue not worked through, schedules queue runs nothing ever takes
            AutoDequeue = local?.AutoDequeTasks ?? true,
            NowUtc = utc(now),
            ServerTimeZone = zoneView(TimeZoneInfo.Local, now),
            SettingsFile = _server.SettingsFileDisplay,
            DataFile = file?.Display,
            ConfigSection = _server.ConfigurationOverlay?.SectionName,
            // "appsettings" or "code" when the list is decided there and cannot be edited here
            Locked = lockedBy,
            Schedules = schedules.Select(s => scheduleView(storeId, s, jobs, now, file)),
            // removed on this server, still in relatude.db.json: said under the list, so the removal can
            // be found again and moved there or taken back
            RemovedHere = file == null ? [] : file.RemovedFrom(SettingsOverlay.OverridePath(storeId, listPath))
                .Select(e => removedLabel(file.FileValue(e.Path) as JsonObject)).ToArray(),
            Jobs = (jobs?.All ?? []).Select(j => new {
                j.Name,
                j.Description,
                j.ArgumentHelp,
                j.BuiltIn,
                Schedules = schedules.Count(s => string.Equals(s.Job?.Trim(), j.Name, StringComparison.OrdinalIgnoreCase)),
                Active = runs.Count(r => r.IsActive && string.Equals(r.JobName, j.Name, StringComparison.OrdinalIgnoreCase)),
                LastRun = runView(runs.FirstOrDefault(r => string.Equals(r.JobName, j.Name, StringComparison.OrdinalIgnoreCase) && r.Status != JobRunStatus.Skipped)),
            }),
            Runs = runs.Select(runView),
        };
    }

    object scheduleView(Guid storeId, ScheduleSettings s, Jobs? jobs, DateTime now, SettingsOverridesFile? file) {
        string? problem;
        DateTime? next;
        object? lastRun = null;
        var active = false;
        if (jobs != null) {
            var status = jobs.GetStatus(s, now);
            problem = status.Problem;
            next = status.NextUtc;
            lastRun = runView(status.LastRun);
            active = status.Active;
        } else {
            // closed: no scheduler to ask, so the times are worked out here and the job is taken on trust
            problem = ScheduleTimes.Problem(s);
            next = null;
            if (problem == null && s.Enabled) {
                try { next = ScheduleTimes.Next(s, now); } catch (ScheduleProblemException e) { problem = e.Message; }
            }
        }
        var elementPath = SettingsOverlay.OverridePath(storeId, listPath + "[" + s.Id + "]");
        // added here, or the whole list is this server's: relatude.db.json has none, so the overrides file
        // holds the list as one value rather than element by element
        var addedHere = file != null && (file.IsAdded(elementPath) || file.ValueEntryFor(SettingsOverlay.OverridePath(storeId, listPath)) != null);
        var changedHere = file == null || addedHere ? [] : fields.Where(f => file.ValueEntryFor(elementPath + "." + f) != null).ToArray();
        return new {
            s.Id,
            s.Name,
            s.Job,
            s.Argument,
            s.Enabled,
            s.Every,
            Unit = s.Unit.ToString(),
            s.At,
            s.Until,
            s.Days,
            s.DayOfMonth,
            s.TimeZone,
            s.RunMissed,
            Summary = ScheduleTimes.Describe(s),
            Problem = problem,
            NextUtc = utc(next),
            // the next time on the schedule's own clock, which is the clock its times are written in
            NextAt = next == null ? null : wallClock(next.Value, s.TimeZone),
            Active = active,
            LastRun = lastRun,
            AddedHere = addedHere,
            ChangedHere = changedHere,
        };
    }

    // ---- editing: through the settings, as the settings page does ----

    object add(SchedulePayload p) {
        requireUnlocked(p.StoreId);
        var values = checkedValues(p.Values);
        var (id, rejected) = _settings.AddListItem(p.StoreId, listPath, values);
        return new { Id = id, Rejected = rejected };
    }

    object save(SchedulePayload p) {
        requireUnlocked(p.StoreId);
        if (p.Id is not Guid id) throw new Exception("Which schedule? ");
        var local = container(p.StoreId).Settings.LocalSettings;
        if ((local?.Schedules ?? []).All(s => s?.Id != id)) throw new Exception("That schedule is gone - it was removed, here or in the settings. ");
        var values = checkedValues(p.Values).ToDictionary(kv => listPath + "[" + id + "]." + kv.Key, kv => kv.Value);
        var rejected = _settings.SaveDatabaseValues(p.StoreId, values);
        return new { Id = id, Rejected = rejected };
    }

    object remove(ScheduleIdPayload p) {
        requireUnlocked(p.StoreId);
        _settings.RemoveListItem(p.StoreId, listPath, p.Id);
        return new { Removed = p.Id };
    }

    // a schedule that cannot run is saved anyway - it says what is wrong on the page, the way an unfinished
    // one in the settings file does - but a field the page does not edit is not let through
    static Dictionary<string, JsonElement> checkedValues(Dictionary<string, JsonElement>? values) {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values ?? []) {
            var field = fields.FirstOrDefault(f => string.Equals(f, key, StringComparison.OrdinalIgnoreCase)) ?? throw new Exception("\"" + key + "\" is not a field of a schedule. ");
            result[field] = value;
        }
        return result;
    }

    void requireUnlocked(Guid storeId) {
        var decidedBy = _server.DecidedOutsideTheSettingsFiles(SettingsOverlay.OverridePath(storeId, listPath));
        if (decidedBy != null) throw new Exception("The schedules are set by " + decidedBy + " and cannot be changed here. ");
    }

    string? lockedKind(Guid storeId) {
        var path = SettingsOverlay.OverridePath(storeId, listPath);
        if (_server.ConfigurationOverlay?.IsOverridden(path, out _) == true) return "appsettings";
        if (_server.IsSetByCode(path)) return "code";
        return null;
    }

    static string removedLabel(JsonObject? element) {
        if (element == null) return "a schedule";
        var name = element.TryGetPropertyValue("Name", out var n) ? n?.ToString() : null;
        var job = element.TryGetPropertyValue("Job", out var j) ? j?.ToString() : null;
        return !string.IsNullOrWhiteSpace(name) ? name! : !string.IsNullOrWhiteSpace(job) ? job! : "a schedule";
    }

    // ---- running ----

    object run(RunPayload p) {
        var jobs = openJobs(p.StoreId);
        var started = p.ScheduleId is Guid scheduleId ? jobs.RunSchedule(scheduleId) : jobs.Run(p.Job ?? throw new Exception("Which job? "), p.Argument);
        return new { Run = runView(started) };
    }

    object cancel(CancelPayload p) {
        var jobs = openJobs(p.StoreId);
        return new { Cancelled = jobs.Cancel(p.RunId) };
    }

    // ---- the editor's preview: when a schedule as it stands in the editor would run ----

    object preview(SchedulePayload p) {
        var schedule = new ScheduleSettings();
        var problems = new List<string>();
        foreach (var (field, value) in checkedValues(p.Values)) {
            try {
                SettingsAccessor.Write(schedule, field, value);
            } catch (Exception e) {
                problems.Add(e.Message);
            }
        }
        var now = DateTime.UtcNow;
        DateTime[] upcoming = [];
        if (problems.Count == 0) {
            try {
                upcoming = ScheduleTimes.Upcoming(schedule, now, previewCount);
                if (upcoming.Length == 0) problems.Add("This schedule never runs: no day matches all of its rules.");
            } catch (ScheduleProblemException e) {
                problems.Add(e.Message);
            }
        }
        string? zone = null;
        try { zone = zoneName(ScheduleTimes.ResolveTimeZone(schedule.TimeZone)); } catch (ScheduleProblemException) { }
        return new {
            Summary = ScheduleTimes.Describe(schedule),
            Problem = problems.FirstOrDefault(),
            Zone = zone,
            Upcoming = upcoming.Select(u => new { Utc = utc(u), At = wallClock(u, schedule.TimeZone) }),
        };
    }

    // ---- pieces ----

    static object? runView(JobRun? r) => r == null ? null : new {
        r.RunId,
        r.JobName,
        r.Argument,
        r.ScheduleId,
        r.ScheduleName,
        Trigger = r.Trigger.ToString(),
        Status = r.Status.ToString(),
        QueuedUtc = utc(r.QueuedUtc),
        DueUtc = utc(r.DueUtc),
        StartedUtc = utc(r.StartedUtc),
        FinishedUtc = utc(r.FinishedUtc),
        r.DurationMs,
        r.Message,
        r.Error,
        r.Times,
    };

    static object zoneView(TimeZoneInfo zone, DateTime nowUtc) => new {
        zone.Id,
        Name = zoneName(zone),
        OffsetMinutes = (int)zone.GetUtcOffset(nowUtc).TotalMinutes,
    };

    // the IANA id where there is one, which is what a schedule's TimeZone takes
    static string zoneName(TimeZoneInfo zone) => zone.HasIanaId ? zone.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : zone.Id;

    static string? wallClock(DateTime utcTime, string? zoneId) {
        try {
            var zone = ScheduleTimes.ResolveTimeZone(zoneId);
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcTime, DateTimeKind.Utc), zone);
            return local.ToString(local.Second == 0 ? "ddd d MMM HH:mm" : "ddd d MMM HH:mm:ss", CultureInfo.InvariantCulture);
        } catch (ScheduleProblemException) {
            return null;
        }
    }

    static string? utc(DateTime? value) {
        if (value is not DateTime v) return null;
        return DateTime.SpecifyKind(v, DateTimeKind.Utc).ToString("o");
    }

    NodeStoreContainer container(Guid storeId) {
        if (!_server.Containers.TryGetValue(storeId, out var c)) throw new Exception("Database not found. ");
        return c;
    }

    Jobs openJobs(Guid storeId) {
        var c = container(storeId);
        if (!c.IsOpen()) throw new Exception("The database must be open to run a job. ");
        return c.Store!.Datastore.Jobs;
    }

    sealed record StorePayload(Guid StoreId);
    /// <summary>Values are by field name ("Every", "Unit", ...), as the catalog names a schedule's fields.</summary>
    sealed record SchedulePayload(Guid StoreId, Guid? Id, Dictionary<string, JsonElement>? Values);
    sealed record ScheduleIdPayload(Guid StoreId, Guid Id);
    /// <summary>Either a schedule, run with its job and argument, or a job by name with an argument.</summary>
    sealed record RunPayload(Guid StoreId, Guid? ScheduleId, string? Job, string? Argument);
    sealed record CancelPayload(Guid StoreId, Guid RunId);
}
