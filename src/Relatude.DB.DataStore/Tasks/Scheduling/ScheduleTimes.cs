using System.Globalization;
using System.Text;

namespace Relatude.DB.Tasks;

/// <summary>A schedule that cannot be run as written, with what is wrong in words a settings page can show.</summary>
public class ScheduleProblemException(string message) : Exception(message) { }

/// <summary>
/// When a schedule runs. Everything here is pure: a schedule and a moment in, the next moment out, so
/// the scheduler, the admin UI's preview and the tests all compute the same times.
/// <para>The times are worked out on the wall clock of the schedule's time zone and only then turned
/// into UTC, so a daily run at 02:00 stays at 02:00 across a change to or from summer time. A time the
/// clock skips (02:30 on the night it jumps from 02:00 to 03:00) runs at the first moment after the
/// jump; a time the clock passes twice runs the first time.</para>
/// </summary>
public static class ScheduleTimes {
    /// <summary>The most times <see cref="Upcoming"/> hands out in one call.</summary>
    public const int MaxUpcoming = 100;

    /// <summary>What is wrong with the schedule, or null when it can run.</summary>
    public static string? Problem(ScheduleSettings schedule) {
        try {
            var compiled = Compile(schedule);
            if (string.IsNullOrWhiteSpace(schedule.Job)) return "Choose the job to run.";
            if (compiled.Next(DateTime.UtcNow) == null) return "This schedule never runs: no day matches all of its rules.";
            return null;
        } catch (ScheduleProblemException e) {
            return e.Message;
        }
    }

    /// <summary>The first time the schedule runs after <paramref name="afterUtc"/>, or null when it never does.
    /// Throws <see cref="ScheduleProblemException"/> for a schedule that cannot be run as written.</summary>
    public static DateTime? Next(ScheduleSettings schedule, DateTime afterUtc) => Compile(schedule).Next(afterUtc);

    /// <summary>The next <paramref name="count"/> times the schedule runs after <paramref name="afterUtc"/>, in UTC.</summary>
    public static DateTime[] Upcoming(ScheduleSettings schedule, DateTime afterUtc, int count) {
        var compiled = Compile(schedule);
        var times = new List<DateTime>();
        var cursor = afterUtc;
        for (var i = 0; i < Math.Clamp(count, 0, MaxUpcoming); i++) {
            var next = compiled.Next(cursor);
            if (next == null) break;
            times.Add(next.Value);
            cursor = next.Value;
        }
        return [.. times];
    }

    /// <summary>
    /// The time zone a schedule's times are in: the server's own for an empty id, else the one named,
    /// by IANA or Windows id. Throws <see cref="ScheduleProblemException"/> for one this machine does
    /// not know.
    /// </summary>
    public static TimeZoneInfo ResolveTimeZone(string? id) {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Local;
        var trimmed = id.Trim();
        if (string.Equals(trimmed, "UTC", StringComparison.OrdinalIgnoreCase)) return TimeZoneInfo.Utc;
        try {
            return TimeZoneInfo.FindSystemTimeZoneById(trimmed);
        } catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) {
            throw new ScheduleProblemException("This server does not know the time zone \"" + trimmed + "\". Use an IANA id, like Europe/Oslo, or leave it empty for the server's own.");
        }
    }

    /// <summary>The schedule in words: "Every day at 02:00", "Every 15 minutes from 08:00 until 18:00, Mon-Fri".</summary>
    public static string Describe(ScheduleSettings schedule) {
        var every = Math.Max(1, schedule.Every);
        var sb = new StringBuilder();
        var unit = schedule.Unit switch {
            ScheduleUnit.Seconds => "second",
            ScheduleUnit.Minutes => "minute",
            ScheduleUnit.Hours => "hour",
            ScheduleUnit.Days => "day",
            ScheduleUnit.Weeks => "week",
            ScheduleUnit.Months => "month",
            _ => schedule.Unit.ToString().ToLowerInvariant(),
        };
        sb.Append(every == 1 ? "Every " + unit : "Every " + every + " " + unit + "s");
        var at = tryTime(schedule.At, out var a) ? a : (TimeSpan?)null;
        var until = tryTime(schedule.Until, out var u) ? u : (TimeSpan?)null;
        var days = tryDays(schedule.Days, out var mask) ? mask : 0;
        switch (schedule.Unit) {
            case ScheduleUnit.Seconds:
            case ScheduleUnit.Minutes:
            case ScheduleUnit.Hours:
                if (at is TimeSpan from && from > TimeSpan.Zero) sb.Append(" from " + formatTime(from));
                if (until != null) sb.Append(" until " + formatTime(until.Value));
                if (days != 0 && days != allDays) sb.Append(" on " + FormatDays(days));
                break;
            case ScheduleUnit.Days:
                sb.Append(" at " + formatTime(at ?? TimeSpan.Zero));
                if (days != 0 && days != allDays) sb.Append(" on " + FormatDays(days));
                break;
            case ScheduleUnit.Weeks:
                sb.Append(" on " + FormatDays(days == 0 ? dayBit(DayOfWeek.Monday) : days));
                sb.Append(" at " + formatTime(at ?? TimeSpan.Zero));
                break;
            case ScheduleUnit.Months:
                var day = schedule.DayOfMonth ?? 1;
                sb.Append(day >= 31 ? " on the last day" : " on day " + day);
                sb.Append(" at " + formatTime(at ?? TimeSpan.Zero));
                break;
        }
        if (!string.IsNullOrWhiteSpace(schedule.TimeZone)) sb.Append(" (" + schedule.TimeZone.Trim() + ")");
        return sb.ToString();
    }

    /// <summary>
    /// The longest a schedule normally waits between two runs, ignoring the days it skips: how far back
    /// the scheduler looks for the run it should have started, and what decides how late a run may be
    /// and still count as on time (see <see cref="Grace"/>).
    /// </summary>
    public static TimeSpan NominalPeriod(ScheduleSettings schedule) {
        var every = Math.Max(1, schedule.Every);
        return schedule.Unit switch {
            ScheduleUnit.Seconds => TimeSpan.FromSeconds(every),
            ScheduleUnit.Minutes => TimeSpan.FromMinutes(every),
            ScheduleUnit.Hours => TimeSpan.FromHours(every),
            ScheduleUnit.Days => TimeSpan.FromDays(every),
            ScheduleUnit.Weeks => TimeSpan.FromDays(7 * every),
            _ => TimeSpan.FromDays(31 * every),
        };
    }

    /// <summary>How late a run may start and still be on time rather than missed: half the period, from
    /// two seconds up to five minutes. A run later than that is a missed one, which
    /// <see cref="ScheduleSettings.RunMissed"/> decides about.</summary>
    public static TimeSpan Grace(ScheduleSettings schedule) {
        var half = NominalPeriod(schedule) / 2;
        if (half < TimeSpan.FromSeconds(2)) return TimeSpan.FromSeconds(2);
        if (half > TimeSpan.FromMinutes(5)) return TimeSpan.FromMinutes(5);
        return half;
    }

    // ---- days of the week ----

    const int allDays = 0b111_1111;
    static int dayBit(DayOfWeek day) => 1 << (int)day;
    // Monday first, the way a week is written in most of the places this runs
    static readonly DayOfWeek[] weekOrder = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    /// <summary>
    /// The days a <see cref="ScheduleSettings.Days"/> value names, one bit per <see cref="DayOfWeek"/>; 0 for
    /// an empty value, which means every day. Throws <see cref="ScheduleProblemException"/> for a word that is
    /// not a day.
    /// </summary>
    public static int ParseDays(string? days) {
        if (string.IsNullOrWhiteSpace(days)) return 0;
        // "Mon - Fri" and "Mon–Fri" (an en dash, as typed by some keyboards) are the range Mon-Fri
        var text = days.Replace((char)0x2013, '-');
        while (text.Contains(" -")) text = text.Replace(" -", "-");
        while (text.Contains("- ")) text = text.Replace("- ", "-");
        var mask = 0;
        foreach (var raw in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            var dash = raw.IndexOf('-');
            if (dash > 0 && dash < raw.Length - 1) {
                var from = Array.IndexOf(weekOrder, parseDay(raw[..dash]));
                var to = Array.IndexOf(weekOrder, parseDay(raw[(dash + 1)..]));
                // Mon-Fri, and also Sat-Mon across the end of the week
                for (var i = from; ; i = (i + 1) % 7) {
                    mask |= dayBit(weekOrder[i]);
                    if (i == to) break;
                }
            } else {
                mask |= dayBit(parseDay(raw));
            }
        }
        return mask;
    }

    /// <summary>The days in the form <see cref="ScheduleSettings.Days"/> is written in, Monday first, with runs of
    /// three or more days as a range: "Mon-Fri", "Mon,Wed,Fri", "Sat,Sun".</summary>
    public static string FormatDays(int mask) {
        if ((mask & allDays) == 0 || (mask & allDays) == allDays) return "every day";
        var parts = new List<string>();
        var i = 0;
        while (i < 7) {
            if ((mask & dayBit(weekOrder[i])) == 0) { i++; continue; }
            var start = i;
            while (i + 1 < 7 && (mask & dayBit(weekOrder[i + 1])) != 0) i++;
            if (i - start >= 2) parts.Add(shortName(weekOrder[start]) + "-" + shortName(weekOrder[i]));
            else for (var k = start; k <= i; k++) parts.Add(shortName(weekOrder[k]));
            i++;
        }
        return string.Join(",", parts);
    }

    static string shortName(DayOfWeek day) => day.ToString()[..3];

    static DayOfWeek parseDay(string word) {
        var w = word.Trim();
        if (w.Length >= 2) {
            foreach (var day in weekOrder) {
                var name = day.ToString();
                // "Mon", "Monday" and anything between; "Tu"/"Th" and "Sa"/"Su" need their second letter
                if (name.StartsWith(w, StringComparison.OrdinalIgnoreCase)) {
                    var ambiguous = weekOrder.Count(d => d.ToString().StartsWith(w, StringComparison.OrdinalIgnoreCase)) > 1;
                    if (!ambiguous) return day;
                }
            }
        }
        throw new ScheduleProblemException("\"" + word + "\" is not a day of the week. Write days as Mon, Tue, Wed, Thu, Fri, Sat and Sun, separated by commas, or as a range, like Mon-Fri.");
    }

    static bool tryDays(string? days, out int mask) {
        try {
            mask = ParseDays(days);
            return true;
        } catch (ScheduleProblemException) {
            mask = 0;
            return false;
        }
    }

    // ---- times of day ----

    static readonly string[] timeFormats = [@"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss"];

    static bool tryTime(string? text, out TimeSpan time) {
        time = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return TimeSpan.TryParseExact(text.Trim(), timeFormats, CultureInfo.InvariantCulture, out time) && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);
    }

    static TimeSpan? parseTime(string? text, string what) {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (tryTime(text, out var time)) return time;
        throw new ScheduleProblemException(what + " must be a time of day, like 02:00 or 18:30 - \"" + text.Trim() + "\" is not.");
    }

    static string formatTime(TimeSpan time) => time.Seconds == 0 ? time.ToString(@"hh\:mm") : time.ToString(@"hh\:mm\:ss");

    // ---- the calculation ----

    /// <summary>Checks the schedule and readies it for <see cref="CompiledSchedule.Next"/>. Throws
    /// <see cref="ScheduleProblemException"/> naming what is wrong.</summary>
    public static CompiledSchedule Compile(ScheduleSettings schedule) {
        if (schedule.Every < 1) throw new ScheduleProblemException("Every must be 1 or more.");
        if (!Enum.IsDefined(schedule.Unit)) throw new ScheduleProblemException("Unknown unit: " + schedule.Unit + ".");
        var subDay = schedule.Unit is ScheduleUnit.Seconds or ScheduleUnit.Minutes or ScheduleUnit.Hours;
        var at = parseTime(schedule.At, subDay ? "From" : "At") ?? TimeSpan.Zero;
        TimeSpan? until = null;
        if (subDay) {
            until = parseTime(schedule.Until, "Until");
            if (until != null && until.Value < at) throw new ScheduleProblemException("Until must be later in the day than From.");
        }
        var days = schedule.Unit == ScheduleUnit.Months ? 0 : ParseDays(schedule.Days);
        if (schedule.Unit == ScheduleUnit.Weeks && days == 0) days = dayBit(DayOfWeek.Monday);
        var dayOfMonth = schedule.DayOfMonth ?? 1;
        if (schedule.Unit == ScheduleUnit.Months && (dayOfMonth < 1 || dayOfMonth > 31)) throw new ScheduleProblemException("The day of the month must be from 1 to 31.");
        var zone = ResolveTimeZone(schedule.TimeZone);
        return new CompiledSchedule(schedule.Unit, schedule.Every, at, until, days, dayOfMonth, zone);
    }
}

/// <summary>A checked schedule, ready to say when it runs next. See <see cref="ScheduleTimes"/>.</summary>
public sealed class CompiledSchedule {
    readonly ScheduleUnit _unit;
    readonly int _every;
    readonly TimeSpan _at;
    readonly TimeSpan? _until;
    readonly int _days; // one bit per DayOfWeek; 0 = every day
    readonly int _dayOfMonth;
    public TimeZoneInfo Zone { get; }
    // generous, and only reached by a schedule whose rules leave no day at all (every 7 days, on a
    // weekday the 7-day rhythm never lands on); Next then answers null rather than looping
    const int maxSteps = 5000;

    internal CompiledSchedule(ScheduleUnit unit, int every, TimeSpan at, TimeSpan? until, int days, int dayOfMonth, TimeZoneInfo zone) {
        _unit = unit;
        _every = every;
        _at = at;
        _until = until;
        _days = days;
        _dayOfMonth = dayOfMonth;
        Zone = zone;
    }

    /// <summary>The first run after <paramref name="afterUtc"/>, in UTC; null when the schedule never runs.</summary>
    public DateTime? Next(DateTime afterUtc) {
        afterUtc = DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);
        var cursor = TimeZoneInfo.ConvertTimeFromUtc(afterUtc, Zone);
        for (var i = 0; i < maxSteps; i++) {
            var candidate = nextLocalAfter(cursor);
            if (candidate == null) return null;
            var utc = toUtc(candidate.Value);
            // a wall-clock time can map to a moment before the one asked about when the clock went back
            // in between; the next candidate after it is tried instead
            if (utc > afterUtc) return utc;
            cursor = candidate.Value;
        }
        return null;
    }

    bool dayAllowed(DateTime date) => _days == 0 || (_days & (1 << (int)date.DayOfWeek)) != 0;

    // day 0 is Monday 1 January of the year 1, so whole weeks of day numbers are Monday-based weeks
    static int dayNumber(DateTime date) => DateOnly.FromDateTime(date).DayNumber;

    /// <summary>The first wall-clock time strictly after <paramref name="cursor"/> that the rules allow.</summary>
    DateTime? nextLocalAfter(DateTime cursor) {
        switch (_unit) {
            case ScheduleUnit.Seconds:
            case ScheduleUnit.Minutes:
            case ScheduleUnit.Hours: {
                    var step = (_unit == ScheduleUnit.Seconds ? TimeSpan.TicksPerSecond : _unit == ScheduleUnit.Minutes ? TimeSpan.TicksPerMinute : TimeSpan.TicksPerHour) * _every;
                    var day = cursor.Date;
                    for (var d = 0; d < maxSteps; d++) {
                        var date = day.AddDays(d);
                        if (!dayAllowed(date)) continue;
                        var first = date + _at;
                        var last = date + (_until ?? TimeSpan.FromDays(1) - TimeSpan.FromTicks(1));
                        DateTime candidate;
                        if (cursor < first) {
                            candidate = first;
                        } else {
                            var k = (cursor - first).Ticks / step + 1;
                            candidate = first + TimeSpan.FromTicks(k * step);
                        }
                        // the day's runs end at Until or midnight; the next day starts over at At
                        if (candidate <= last && candidate.Date == date) return candidate;
                    }
                    return null;
                }
            case ScheduleUnit.Days:
            case ScheduleUnit.Weeks: {
                    var day = cursor.Date;
                    for (var d = 0; d < maxSteps; d++) {
                        var date = day.AddDays(d);
                        var n = dayNumber(date);
                        // every other day, every third week: counted from a fixed point in the calendar, so
                        // the rhythm does not depend on when the server happened to start
                        var inRhythm = _unit == ScheduleUnit.Days ? n % _every == 0 : (n / 7) % _every == 0;
                        if (!inRhythm || !dayAllowed(date)) continue;
                        var candidate = date + _at;
                        if (candidate > cursor) return candidate;
                    }
                    return null;
                }
            case ScheduleUnit.Months: {
                    var start = cursor.Year * 12 + cursor.Month - 1;
                    for (var m = 0; m < maxSteps; m++) {
                        var index = start + m;
                        if (index % _every != 0) continue;
                        var year = index / 12;
                        var month = index % 12 + 1;
                        if (year > 9998) return null;
                        var day = Math.Min(_dayOfMonth, DateTime.DaysInMonth(year, month));
                        var candidate = new DateTime(year, month, day) + _at;
                        if (candidate > cursor) return candidate;
                    }
                    return null;
                }
            default:
                return null;
        }
    }

    DateTime toUtc(DateTime local) {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (Zone.IsInvalidTime(local)) {
            // skipped by the clock going forward: the first minute that exists after the jump
            var probe = local;
            for (var i = 0; i < 24 * 60 && Zone.IsInvalidTime(probe); i++) probe = probe.AddMinutes(1);
            local = probe;
        }
        if (Zone.IsAmbiguousTime(local)) {
            // passed twice as the clock goes back: the first time, which is the larger offset
            var offset = Zone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }
        return TimeZoneInfo.ConvertTimeToUtc(local, Zone);
    }
}
