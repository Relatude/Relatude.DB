using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Logging;
using Relatude.DB.Tasks;
using Relatude.Utils;

namespace Relatude.Store;

/// <summary>
/// The scheduler: when a schedule runs (<see cref="ScheduleTimes"/>, pure, checked on fixed dates in
/// fixed time zones), and what <see cref="Jobs"/> does when one comes due - queues a run, skips it
/// and says why, catches up on one missed while the database was closed, remembers it all across a
/// reopen. The scheduler's timer is off in these tests (<c>TickSchedulesInBackground</c>), so the clock
/// only moves when a test calls <see cref="Jobs.Tick"/>, and the queue only runs when a test drains it.
/// </summary>
[TestClass]
public class SchedulerTests {

    // ---- when a schedule runs ----

    static DateTime utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0) => new(year, month, day, hour, minute, second, DateTimeKind.Utc);

    static ScheduleSettings schedule(ScheduleUnit unit, int every = 1, string? at = null, string? until = null, string? days = null, int? dayOfMonth = null, string zone = "UTC") => new() {
        Id = Guid.NewGuid(),
        Job = "probe",
        Unit = unit,
        Every = every,
        At = at,
        Until = until,
        Days = days,
        DayOfMonth = dayOfMonth,
        TimeZone = zone,
    };

    [TestMethod]
    public void Daily_RunsAtTheTimeOfDay_StrictlyAfterTheMomentAsked() {
        var s = schedule(ScheduleUnit.Days, at: "02:00");
        Assert.AreEqual(utc(2026, 10, 12, 2), ScheduleTimes.Next(s, utc(2026, 10, 12, 1)));
        Assert.AreEqual(utc(2026, 10, 13, 2), ScheduleTimes.Next(s, utc(2026, 10, 12, 2)), "a run exactly at the moment asked is not after it");
    }

    [TestMethod]
    public void BelowADay_FollowsTheIntervalWithinTheWindow_OnTheDaysGiven() {
        // 2026-10-16 is a Friday
        var s = schedule(ScheduleUnit.Minutes, every: 15, at: "08:00", until: "18:00", days: "Mon-Fri");
        Assert.AreEqual(utc(2026, 10, 16, 18), ScheduleTimes.Next(s, utc(2026, 10, 16, 17, 50)), "Until is the last start allowed, and is allowed");
        Assert.AreEqual(utc(2026, 10, 19, 8), ScheduleTimes.Next(s, utc(2026, 10, 16, 18)), "past the window on a Friday: Monday morning");
        Assert.AreEqual(utc(2026, 10, 19, 8, 15), ScheduleTimes.Next(s, utc(2026, 10, 19, 8, 7)));
    }

    [TestMethod]
    public void AnIntervalThatDoesNotDivideTheDay_StartsOverAtMidnight() {
        var s = schedule(ScheduleUnit.Hours, every: 7);
        CollectionAssert.AreEqual(
            new[] { utc(2026, 10, 12, 7), utc(2026, 10, 12, 14), utc(2026, 10, 12, 21), utc(2026, 10, 13, 0), utc(2026, 10, 13, 7) },
            ScheduleTimes.Upcoming(s, utc(2026, 10, 12, 1), 5));
    }

    [TestMethod]
    public void EveryOtherWeek_OnTwoDays_KeepsTheTwoWeekRhythm() {
        var s = schedule(ScheduleUnit.Weeks, every: 2, at: "07:30", days: "Mon,Thu");
        var times = ScheduleTimes.Upcoming(s, utc(2026, 10, 1), 6);
        Assert.AreEqual(6, times.Length);
        Assert.IsTrue(times.All(t => t.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Thursday));
        // Monday to Thursday in a week that runs, then on to the Monday two weeks after the last one
        for (var i = 1; i < times.Length; i++) {
            var gap = (times[i] - times[i - 1]).TotalDays;
            Assert.IsTrue(gap == 3 || gap == 11, "a gap of " + gap + " days after " + times[i - 1]);
        }
        Assert.AreEqual(TimeSpan.FromDays(14), times[2] - times[0], "every second week");
        Assert.IsTrue(times.All(t => t.TimeOfDay == new TimeSpan(7, 30, 0)));
    }

    [TestMethod]
    public void Monthly_OnADayAMonthDoesNotHave_RunsOnItsLastDay() {
        var s = schedule(ScheduleUnit.Months, dayOfMonth: 31);
        CollectionAssert.AreEqual(new[] { utc(2027, 1, 31), utc(2027, 2, 28), utc(2027, 3, 31) }, ScheduleTimes.Upcoming(s, utc(2027, 1, 15), 3));
    }

    [TestMethod]
    public void SummerTime_ATimeTheClockSkipsRunsAfterTheJump_AndATimeItPassesTwiceRunsOnce() {
        var s = schedule(ScheduleUnit.Days, at: "02:30", zone: "Europe/Oslo");
        // 29 March 2026 the clock jumps from 02:00 to 03:00: 02:30 does not exist, and the run is at 03:00 CEST
        Assert.AreEqual(utc(2026, 3, 29, 1, 0), ScheduleTimes.Next(s, utc(2026, 3, 28, 12)));
        Assert.AreEqual(utc(2026, 3, 30, 0, 30), ScheduleTimes.Next(s, utc(2026, 3, 29, 1, 0)), "and back at 02:30, now CEST, the next day");
        // 25 October 2026 the clock goes back from 03:00 to 02:00: 02:30 is passed twice, and runs the first time
        Assert.AreEqual(utc(2026, 10, 25, 0, 30), ScheduleTimes.Next(s, utc(2026, 10, 24, 12)));
        Assert.AreEqual(utc(2026, 10, 26, 1, 30), ScheduleTimes.Next(s, utc(2026, 10, 25, 0, 30)), "not again an hour later, at the second 02:30");
    }

    [TestMethod]
    public void WhatIsWrong_IsSaidInWords() {
        StringAssert.Contains(ScheduleTimes.Problem(schedule(ScheduleUnit.Days, at: "25:00")), "time of day");
        StringAssert.Contains(ScheduleTimes.Problem(schedule(ScheduleUnit.Minutes, at: "18:00", until: "08:00")), "Until must be later");
        StringAssert.Contains(ScheduleTimes.Problem(schedule(ScheduleUnit.Days, days: "Mon,Funday")), "\"Funday\" is not a day");
        StringAssert.Contains(ScheduleTimes.Problem(schedule(ScheduleUnit.Days, zone: "Mars/Olympus_Mons")), "does not know the time zone");
        StringAssert.Contains(ScheduleTimes.Problem(schedule(ScheduleUnit.Days, every: 0)), "Every must be 1");
        // every seventh day is always the same weekday - a Monday, counted from 1 January of the year 1
        StringAssert.Contains(ScheduleTimes.Problem(schedule(ScheduleUnit.Days, every: 7, days: "Tue")), "never runs");
        var noJob = schedule(ScheduleUnit.Days);
        noJob.Job = " ";
        StringAssert.Contains(ScheduleTimes.Problem(noJob), "Choose the job");
        Assert.IsNull(ScheduleTimes.Problem(schedule(ScheduleUnit.Days, at: "02:00", days: "Mon-Fri")));
    }

    [TestMethod]
    public void Days_AreReadLeniently_AndWrittenTheOneWay() {
        Assert.AreEqual("Mon-Fri", ScheduleTimes.FormatDays(ScheduleTimes.ParseDays("Mon-Fri")));
        Assert.AreEqual("Mon-Wed", ScheduleTimes.FormatDays(ScheduleTimes.ParseDays("monday - Wed")));
        Assert.AreEqual("Mon,Wed,Fri", ScheduleTimes.FormatDays(ScheduleTimes.ParseDays("mon, wed ,fri")));
        Assert.AreEqual("Mon,Sat,Sun", ScheduleTimes.FormatDays(ScheduleTimes.ParseDays("Sat-Mon")), "a range across the end of the week");
        Assert.AreEqual("Tue", ScheduleTimes.FormatDays(ScheduleTimes.ParseDays("Tu")));
        Assert.AreEqual(0, ScheduleTimes.ParseDays(" "), "empty is every day");
        Assert.ThrowsExactly<ScheduleProblemException>(() => ScheduleTimes.ParseDays("T"), "T could be Tuesday or Thursday");
    }

    [TestMethod]
    public void Describe_SaysItTheWayAPersonWould() {
        Assert.AreEqual("Every day at 02:00", ScheduleTimes.Describe(new ScheduleSettings { Unit = ScheduleUnit.Days, At = "02:00" }));
        Assert.AreEqual("Every 15 minutes from 08:00 until 18:00 on Mon-Fri",
            ScheduleTimes.Describe(new ScheduleSettings { Unit = ScheduleUnit.Minutes, Every = 15, At = "8:00", Until = "18:00", Days = "mon-fri" }));
        Assert.AreEqual("Every week on Mon at 00:00", ScheduleTimes.Describe(new ScheduleSettings { Unit = ScheduleUnit.Weeks }));
        Assert.AreEqual("Every 3 months on the last day at 03:00 (UTC)",
            ScheduleTimes.Describe(new ScheduleSettings { Unit = ScheduleUnit.Months, Every = 3, DayOfMonth = 31, At = "03:00", TimeZone = "UTC" }));
    }

    // ---- the scheduler ----

    static DataStoreLocal open(SettingsLocal settings, IIOProvider? io = null) {
        settings.AutoDequeTasks = false; // the tests drain the queue themselves
        var store = new DataStoreLocal(Helper.GetDatamodel(), settings, io);
        store.TickSchedulesInBackground = false; // and move the clock themselves
        store.Open();
        return store;
    }

    static Task<BatchTaskResult[]> drain(DataStoreLocal store) => store.TaskQueue.ExecuteTasksAsync(60_000, () => false);

    static ScheduleSettings everyMinute(string job) => new() { Id = Guid.NewGuid(), Job = job, Unit = ScheduleUnit.Minutes, TimeZone = "UTC" };
    static ScheduleSettings dailyAt(string job, string at, bool runMissed = true) => new() { Id = Guid.NewGuid(), Job = job, Unit = ScheduleUnit.Days, At = at, TimeZone = "UTC", RunMissed = runMissed };

    static int pending(DataStoreLocal store) => store.TaskQueue.CountBatch(BatchState.Pending);

    [TestMethod]
    public async Task ADueSchedule_QueuesARun_WhichRunsTheTask() {
        var s = everyMinute("count");
        var store = open(new SettingsLocal { Schedules = [s] });
        try {
            var ran = 0;
            store.Jobs.Add("count", ctx => { Interlocked.Increment(ref ran); ctx.Report("counted " + ctx.ScheduleName); });
            var t0 = utc(2030, 1, 7, 10, 0, 30);
            store.Jobs.Tick(t0);
            Assert.AreEqual(0, pending(store), "a schedule seen for the first time waits for its next time");
            store.Jobs.Tick(t0.AddSeconds(20));
            Assert.AreEqual(0, pending(store));
            store.Jobs.Tick(t0.AddSeconds(31));
            Assert.AreEqual(1, pending(store));
            var results = await drain(store);
            Assert.IsTrue(results.All(r => r.Error == null));
            Assert.AreEqual(1, ran);
            var run = store.Jobs.Runs().First();
            Assert.AreEqual(JobRunStatus.Succeeded, run.Status);
            Assert.AreEqual(JobTrigger.Schedule, run.Trigger);
            Assert.AreEqual(s.Id, run.ScheduleId);
            Assert.AreEqual(utc(2030, 1, 7, 10, 1), run.DueUtc);
            Assert.AreEqual("counted count", run.Message);
            Assert.AreEqual(0, pending(store), "a run is deleted from the queue once it succeeds");
            Assert.AreEqual(utc(2030, 1, 7, 10, 2), store.Jobs.GetStatus(s, t0.AddSeconds(32)).NextUtc);
        } finally {
            store.Dispose();
        }
    }

    [TestMethod]
    public async Task MissedRuns_RunOnceLate_OrAreSkipped_AsTheScheduleSays() {
        var catchUp = dailyAt("count", "02:00");
        var skip = dailyAt("count", "02:00", runMissed: false);
        var store = open(new SettingsLocal { Schedules = [catchUp, skip] });
        try {
            store.Jobs.Add("count", _ => { });
            store.Jobs.Tick(utc(2030, 1, 7, 10));
            // three days on: three runs were missed, and the one that counts is the latest
            store.Jobs.Tick(utc(2030, 1, 10, 9));
            Assert.AreEqual(1, pending(store), "one run for three missed, and none for the schedule that skips");
            await drain(store);
            var runs = store.Jobs.Runs();
            var late = runs.Single(r => r.ScheduleId == catchUp.Id);
            Assert.AreEqual(JobTrigger.Missed, late.Trigger);
            Assert.AreEqual(utc(2030, 1, 10, 2), late.DueUtc);
            var skipped = runs.Single(r => r.ScheduleId == skip.Id);
            Assert.AreEqual(JobRunStatus.Skipped, skipped.Status);
            StringAssert.StartsWith(skipped.Message, "Missed");
            Assert.AreEqual(utc(2030, 1, 11, 2), store.Jobs.GetStatus(catchUp, utc(2030, 1, 10, 9)).NextUtc);
        } finally {
            store.Dispose();
        }
    }

    [TestMethod]
    public void ARunStillQueued_SkipsTheNextOne_AndSkipsInARowAreOneEntry() {
        var s = everyMinute("count");
        var store = open(new SettingsLocal { Schedules = [s] });
        try {
            store.Jobs.Add("count", _ => { });
            var t0 = utc(2030, 1, 7, 10, 0, 30);
            store.Jobs.Tick(t0);
            store.Jobs.Tick(t0.AddMinutes(1)); // queued, and never drained
            store.Jobs.Tick(t0.AddMinutes(2));
            store.Jobs.Tick(t0.AddMinutes(3));
            Assert.AreEqual(1, pending(store));
            var newest = store.Jobs.Runs().First();
            Assert.AreEqual(JobRunStatus.Skipped, newest.Status);
            Assert.AreEqual("The previous run had not finished.", newest.Message);
            Assert.AreEqual(2, newest.Times);
            Assert.AreEqual(2, store.Jobs.Runs().Length, "the queued run and one entry for the two skips");
            Assert.IsTrue(store.Jobs.GetStatus(s, t0.AddMinutes(3)).Active);
        } finally {
            store.Dispose();
        }
    }

    [TestMethod]
    public void ATaskNobodyRegistered_IsSkippedWithTheReason_UntilItIs() {
        var s = everyMinute("later");
        var store = open(new SettingsLocal { Schedules = [s] });
        try {
            var t0 = utc(2030, 1, 7, 10, 0, 30);
            store.Jobs.Tick(t0);
            StringAssert.Contains(store.Jobs.GetStatus(s, t0).Problem, "No job named \"later\"");
            store.Jobs.Tick(t0.AddMinutes(1));
            store.Jobs.Tick(t0.AddMinutes(2));
            var skipped = store.Jobs.Runs().Single();
            Assert.AreEqual(JobRunStatus.Skipped, skipped.Status);
            Assert.AreEqual(2, skipped.Times);
            store.Jobs.Add("later", _ => { });
            Assert.IsNull(store.Jobs.GetStatus(s, t0.AddMinutes(2)).Problem);
            store.Jobs.Tick(t0.AddMinutes(3));
            Assert.AreEqual(1, pending(store));
        } finally {
            store.Dispose();
        }
    }

    [TestMethod]
    public void MovingADailyRunToAnEarlierHour_DoesNotRunItAtOnce() {
        var s = dailyAt("count", "14:00");
        var store = open(new SettingsLocal { Schedules = [s] });
        try {
            store.Jobs.Add("count", _ => { });
            store.Jobs.Tick(utc(2030, 1, 7, 10));
            s.At = "08:00"; // edited in the admin UI at half past ten: 08:00 today has passed, and was never due
            store.Jobs.Tick(utc(2030, 1, 7, 10, 30));
            Assert.AreEqual(0, pending(store));
            Assert.AreEqual(utc(2030, 1, 8, 8), store.Jobs.GetStatus(s, utc(2030, 1, 7, 10, 30)).NextUtc);
        } finally {
            store.Dispose();
        }
    }

    [TestMethod]
    public void ADisabledSchedule_DoesNotRun_AndTurnedOnAgainWaitsForItsNextTime() {
        var s = everyMinute("count");
        s.Enabled = false;
        var store = open(new SettingsLocal { Schedules = [s] });
        try {
            store.Jobs.Add("count", _ => { });
            var t0 = utc(2030, 1, 7, 10, 0, 30);
            store.Jobs.Tick(t0);
            store.Jobs.Tick(t0.AddMinutes(5));
            Assert.AreEqual(0, pending(store));
            Assert.IsNull(store.Jobs.GetStatus(s, t0.AddMinutes(5)).NextUtc);
            s.Enabled = true;
            store.Jobs.Tick(t0.AddMinutes(5));
            Assert.AreEqual(0, pending(store), "the minutes it was off are not caught up on");
            store.Jobs.Tick(t0.AddMinutes(6));
            Assert.AreEqual(1, pending(store));
        } finally {
            store.Dispose();
        }
    }

    [TestMethod]
    public async Task WhatTheSchedulerRemembers_SurvivesAReopen() {
        var io = new IOProviderMemory();
        var s = dailyAt("count", "02:00");
        var first = open(new SettingsLocal { Schedules = [s] }, io);
        try {
            first.Jobs.Add("count", ctx => ctx.Report("by hand"));
            first.Jobs.Tick(utc(2030, 1, 7, 10));
            first.Jobs.Run("count");
            await drain(first);
        } finally {
            first.Dispose();
        }
        Assert.IsTrue(io.Exists(FileKeyUtility.Scheduler_GetStateFileKey()));
        var second = open(new SettingsLocal { Schedules = [s] }, io);
        try {
            second.Jobs.Add("count", _ => { });
            Assert.AreEqual("by hand", second.Jobs.Runs().Single().Message, "the history is kept");
            // the server was down over two nights: the schedule knows it last came due on the 7th
            second.Jobs.Tick(utc(2030, 1, 9, 10));
            Assert.AreEqual(1, pending(second));
            await drain(second);
            Assert.AreEqual(JobTrigger.Missed, second.Jobs.Runs().First().Trigger);
        } finally {
            second.Dispose();
        }
    }

    [TestMethod]
    public void ARunStillInTheQueue_CanBeTakenOutOfIt() {
        var store = open(new SettingsLocal());
        try {
            store.Jobs.Add("count", _ => { });
            var run = store.Jobs.Run("count");
            Assert.AreEqual(JobRunStatus.Queued, run.Status);
            Assert.AreEqual(1, pending(store));
            Assert.IsTrue(store.Jobs.Cancel(run.RunId));
            Assert.AreEqual(0, pending(store));
            Assert.AreEqual(JobRunStatus.Cancelled, store.Jobs.GetRun(run.RunId)!.Status);
            Assert.IsFalse(store.Jobs.Cancel(run.RunId), "not twice");
        } finally {
            store.Dispose();
        }
    }

    [TestMethod]
    public async Task ATaskThatThrows_FailsItsRun_AndItsBatch_WithTheReason() {
        var store = open(new SettingsLocal());
        try {
            store.Jobs.Add("boom", (Action<JobContext>)(_ => throw new InvalidOperationException("kaput")));
            var run = store.Jobs.Run("boom");
            var results = await drain(store);
            StringAssert.Contains(results.Single().Error!.Message, "\"boom\"");
            StringAssert.Contains(results.Single().Error!.Message, "kaput");
            var after = store.Jobs.GetRun(run.RunId)!;
            Assert.AreEqual(JobRunStatus.Failed, after.Status);
            Assert.AreEqual("kaput", after.Error);
            Assert.AreEqual(1, store.TaskQueue.CountBatch(BatchState.Failed), "kept in the queue, where it can be put back in line");
        } finally {
            store.Dispose();
        }
    }

    [TestMethod]
    public async Task ARunningTask_IsStoppedThroughItsCancellationToken() {
        var store = open(new SettingsLocal());
        try {
            var started = new TaskCompletionSource();
            store.Jobs.Add("wait", async ctx => {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
            });
            var run = store.Jobs.Run("wait");
            var draining = drain(store);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.AreEqual(JobRunStatus.Running, store.Jobs.GetRun(run.RunId)!.Status);
            Assert.IsTrue(store.Jobs.Cancel(run.RunId));
            var results = await draining.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.IsNull(results.Single().Error, "a stop is not a failure");
            var after = store.Jobs.GetRun(run.RunId)!;
            Assert.AreEqual(JobRunStatus.Cancelled, after.Status);
            Assert.AreEqual(0, store.TaskQueue.CountBatch(BatchState.Failed));
        } finally {
            store.Dispose();
        }
    }

    [TestMethod]
    public async Task TheDatabasesOwnTasks_AreThere_AndRun() {
        var store = open(new SettingsLocal());
        try {
            var names = store.Jobs.All.Select(t => t.Name).ToArray();
            CollectionAssert.IsSubsetOf(new[] { "backup", "delete-old-backups", "truncate-log", "delete-old-log-files", "save-state", "purge-caches", "reindex-text", "delete-unreferenced-files" }, names);
            Assert.IsTrue(store.Jobs.All.All(t => t.BuiltIn && t.Description != null));

            var backup = store.Jobs.Run("backup");
            var nonsense = store.Jobs.Run("backup", "forever");
            await drain(store);
            Assert.AreEqual(JobRunStatus.Succeeded, store.Jobs.GetRun(backup.RunId)!.Status, store.Jobs.GetRun(backup.RunId)!.Error);
            Assert.AreEqual(1, FileKeyUtility.WAL_GetAllBackUpFileKeys(store.IOBackup).Length, "one backup written");
            var refused = store.Jobs.GetRun(nonsense.RunId)!;
            Assert.AreEqual(JobRunStatus.Failed, refused.Status);
            StringAssert.Contains(refused.Error, "takes keep, truncate, copy");

            var save = store.Jobs.Run("save-state");
            await drain(store);
            Assert.AreEqual(JobRunStatus.Succeeded, store.Jobs.GetRun(save.RunId)!.Status);
        } finally {
            store.Dispose();
        }
    }

    /// <summary>The Tasks log of the Activity page gets an entry per task: a job's run by its name,
    /// with how it was started and what it reported, and the tasks of a runner that writes no entries of
    /// its own - a backup - from the queue.</summary>
    [TestMethod]
    public async Task TaskRuns_AreWrittenToTheTasksLog() {
        var store = open(new SettingsLocal { LogRecording = [new LogRecordingSettings { Key = "task", Log = true }] });
        try {
            store.Jobs.Add("count", ctx => ctx.Report("counted 3"));
            store.Jobs.Add("boom", (Action<JobContext>)(_ => throw new InvalidOperationException("kaput")));
            store.Jobs.Run("count", "fast");
            store.Jobs.Run("boom");
            store.BackUpNow(truncate: false, keepForever: false);
            await drain(store);
            store.Logger.FlushToDiskNow();
            var entries = store.Logger.LogStore.ExtractLog("task", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(5), 0, 100, false, out _).ToList();
            string value(LogEntry e, string key) => e.Values.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

            var count = entries.Single(e => value(e, "taskId") == "count");
            Assert.AreEqual(typeof(RunJob).FullName, value(count, "taskTypeName"));
            Assert.AreEqual("Success", value(count, "success"));
            StringAssert.StartsWith(value(count, "details"), "by hand · fast · ");
            StringAssert.EndsWith(value(count, "details"), "counted 3");

            var boom = entries.Single(e => value(e, "taskId") == "boom");
            Assert.AreEqual("Error", value(boom, "success"));
            StringAssert.EndsWith(value(boom, "details"), "kaput");

            var backup = entries.Single(e => value(e, "taskTypeName") == typeof(RewriteTask).FullName);
            Assert.AreEqual("Success", value(backup, "success"));
            Assert.AreEqual("backup", value(backup, "details"));
            StringAssert.StartsWith(value(backup, "taskId"), "backup/");
        } finally {
            store.Dispose();
        }
    }

    [TestMethod]
    public void ATaskNeedsAProperName() {
        var store = open(new SettingsLocal());
        try {
            Assert.ThrowsExactly<ArgumentException>(() => store.Jobs.Add(" ", _ => { }));
            Assert.ThrowsExactly<ArgumentException>(() => store.Jobs.Add(new string('x', Jobs.MaxNameLength + 1), _ => { }));
            Assert.ThrowsExactly<ArgumentException>(() => store.Jobs.Run("nobody"));
            store.Jobs.Add("  Mixed Case  ", _ => { });
            Assert.IsTrue(store.Jobs.Contains("mixed case"), "trimmed, and found whatever the case");
        } finally {
            store.Dispose();
        }
    }
}
