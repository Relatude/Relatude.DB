namespace WAF.Logging;
internal static class LogExtensions {
    public static DateTime Floor(this DateTime d, FileResolution res) {
        return res switch {
            FileResolution.Minute => new(d.Year, d.Month, d.Day, d.Hour, d.Minute, 0, DateTimeKind.Utc),
            FileResolution.Hour => new(d.Year, d.Month, d.Day, d.Hour, 0, 0, DateTimeKind.Utc),
            FileResolution.Day => new(d.Year, d.Month, d.Day, 0, 0, 0, DateTimeKind.Utc),
            FileResolution.Month => new(d.Year, d.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            _ => throw new NotImplementedException(),
        };
    }
    public static DateTime Ceiling(this DateTime d, FileResolution res) {
        return d.Floor(res).AddInterval(res).AddTicks(-1);
    }
    public static DateTime AddInterval(this DateTime d, FileResolution res) {
        return res switch {
            FileResolution.Minute => d.AddMinutes(1),
            FileResolution.Hour => d.AddHours(1),
            FileResolution.Day => d.AddDays(1),
            FileResolution.Month => d.AddMonths(1),
            _ => throw new NotImplementedException(),
        };
    }
}
