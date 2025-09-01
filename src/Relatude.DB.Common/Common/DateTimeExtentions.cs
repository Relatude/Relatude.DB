namespace Relatude.DB.Common;
public static class DateTimeExtentions {
    public static DateTime SafeAdd(this DateTime dt, TimeSpan ts) =>
ts > TimeSpan.Zero ? (DateTime.MaxValue - dt < ts ? DateTime.MaxValue : dt + ts)
                   : (dt - DateTime.MinValue < -ts ? DateTime.MinValue : dt + ts);
    public static DateTime SafeSubtract(this DateTime dt, TimeSpan ts) =>
        ts > TimeSpan.Zero ? (dt - DateTime.MinValue < ts ? DateTime.MinValue : dt - ts)
                          : (DateTime.MaxValue - dt < -ts ? DateTime.MaxValue : dt - ts);
}
