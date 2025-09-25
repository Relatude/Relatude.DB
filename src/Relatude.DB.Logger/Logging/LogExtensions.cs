using Relatude.DB.IO;

namespace Relatude.DB.Logging;
internal static class LogExtensions {
    public static DateTime Floor(this DateTime d, FileInterval res) {
        return res switch {
            FileInterval.Minute => new(d.Year, d.Month, d.Day, d.Hour, d.Minute, 0, DateTimeKind.Utc),
            FileInterval.Hour => new(d.Year, d.Month, d.Day, d.Hour, 0, 0, DateTimeKind.Utc),
            FileInterval.Day => new(d.Year, d.Month, d.Day, 0, 0, 0, DateTimeKind.Utc),
            FileInterval.Month => new(d.Year, d.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            _ => throw new NotImplementedException(),
        };
    }
    public static DateTime Ceiling(this DateTime d, FileInterval res) {
        return d.Floor(res).AddInterval(res).AddTicks(-1);
    }
    public static DateTime AddInterval(this DateTime d, FileInterval res) {
        return res switch {
            FileInterval.Minute => d.AddMinutes(1),
            FileInterval.Hour => d.AddHours(1),
            FileInterval.Day => d.AddDays(1),
            FileInterval.Month => d.AddMonths(1),
            _ => throw new NotImplementedException(),
        };
    }
}
