namespace Relatude.DB.Tasks;

/// <summary>
/// One entry of a database's timetable, <c>LocalSettings.Schedules</c>: which job to run (see
/// <see cref="Jobs"/>) and when. A schedule is settings, kept in relatude.db.json - or, when it is
/// added or changed in the admin UI, in relatude.db.overrides.json - and read again at every tick, so an
/// edit applies to the next run without reopening the database.
/// <para>When it runs is a repeating interval: <see cref="Every"/> of a <see cref="Unit"/>, laid out on
/// the clock of <see cref="TimeZone"/>. Below a day the runs start at <see cref="At"/> and follow every
/// interval through the day until <see cref="Until"/> - every 15 minutes from 08:00 until 18:00 - and the
/// next day starts over at <see cref="At"/>, the way cron does it. From a day up there is one run, at
/// <see cref="At"/>. <see cref="Days"/> keeps the runs to some days of the week.</para>
/// </summary>
public class ScheduleSettings {
    /// <summary>Identifies the schedule in the settings file, so the admin UI can edit and remove it, and
    /// keys what the scheduler remembers about it (when it last ran).</summary>
    public Guid Id { get; set; }
    /// <summary>What the schedule is called on the scheduler page; empty shows the job's name.</summary>
    public string? Name { get; set; }
    /// <summary>The job it runs, by name. Names are not case sensitive.</summary>
    public string? Job { get; set; }
    /// <summary>Handed to the job as <see cref="JobContext.Argument"/>. What it means, if
    /// anything, is the job's business - the backup job takes "keep" and "truncate".</summary>
    public string? Argument { get; set; }
    /// <summary>Off keeps the schedule in the settings without running it.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>How many <see cref="Unit"/>s from one run to the next. 1 or more.</summary>
    public int Every { get; set; } = 1;
    public ScheduleUnit Unit { get; set; } = ScheduleUnit.Days;
    /// <summary>
    /// The time of day, as HH:mm or HH:mm:ss. For a schedule of days, weeks or months it is when the run
    /// happens; for seconds, minutes and hours it is the first run of the day, which the rest follow.
    /// Empty is midnight.
    /// </summary>
    public string? At { get; set; }
    /// <summary>Seconds, minutes and hours only: the latest time of day a run may start. Empty runs until
    /// midnight.</summary>
    public string? Until { get; set; }
    /// <summary>
    /// The days of the week it runs on, as a list - "Mon,Wed,Fri" or a range, "Mon-Fri". Empty is every
    /// day; a weekly schedule without days runs on Mondays. Not used by monthly schedules.
    /// </summary>
    public string? Days { get; set; }
    /// <summary>Months only: the day of the month, 1 to 31. A month without that day runs on its last
    /// day, so 31 is the last day of every month. Empty is the 1st.</summary>
    public int? DayOfMonth { get; set; }
    /// <summary>The time zone the times are in - an IANA id ("Europe/Oslo") or a Windows one. Empty is
    /// the server's own time zone.</summary>
    public string? TimeZone { get; set; }
    /// <summary>
    /// A run that fell due while the database was closed, or the server stopped, is run once as soon as
    /// it is open again - however many were missed. Off, missed runs are skipped and the schedule waits
    /// for its next time.
    /// </summary>
    public bool RunMissed { get; set; } = true;
    public override string ToString() => (string.IsNullOrWhiteSpace(Name) ? Job ?? "?" : Name) + ": " + Every + " " + Unit;
}

/// <summary>What <see cref="ScheduleSettings.Every"/> counts.</summary>
public enum ScheduleUnit {
    Seconds = 0,
    Minutes = 1,
    Hours = 2,
    Days = 3,
    Weeks = 4,
    Months = 5,
}
