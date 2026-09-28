using Relatude.DB.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Relatude.DB.Logging;
/// <summary>
/// Turns a value into the type a log declares for it.
///
/// A log file stores seven types (<see cref="LogDataType"/>), and a value handed to a log is whatever
/// the code recording it happened to have: a long for a row count, a float, a decimal amount, a
/// DateTimeOffset, a bool. Stored as they come, those are written as the text they print as - a
/// long in an Integer column becomes the string "123" - and the statistics of the column never see
/// them. So a value is converted to the declared type when it is recorded, and a value read back is
/// converted to the type declared now, which is what keeps a log readable after one of its columns
/// changed type.
///
/// A value that does not convert is not forced: recording leaves it out rather than writing a zero
/// the statistics would count as a measurement, and reading keeps it as it was stored rather than
/// showing a zero that was never recorded.
/// </summary>
public static class LogValues {
    /// <summary>The value as the declared type, or false when it has no sensible reading as one.</summary>
    public static bool TryConvert(object? value, LogDataType type, [NotNullWhen(true)] out object? converted) {
        // An empty position is the way a GeoCoordinate says "no position": it is no value, whatever
        // the column is, the same as a null
        if (value is GeoCoordinate { IsEmpty: true }) {
            converted = null;
            return false;
        }
        // A value that already is what the column declares is handed back as it came: converting it
        // would only box it again, for every value of every entry recorded and read.
        converted = value == null ? null : type switch {
            LogDataType.String => value as string ?? toText(value),
            LogDataType.Integer => value is int ? value : toInteger(value),
            LogDataType.Double => value is double d && double.IsFinite(d) ? value : toDouble(value),
            LogDataType.DateTime => value is DateTime { Kind: DateTimeKind.Utc } ? value : toDateTime(value),
            LogDataType.TimeSpan => value is TimeSpan ? value : toTimeSpan(value),
            LogDataType.Bytes => value is byte[] ? value : toBytes(value),
            LogDataType.GeoCoordinate => value is GeoCoordinate ? value : toGeo(value),
            _ => null,
        };
        return converted != null;
    }

    /// <summary>
    /// The data type a value is stored as when a log declares nothing for it: the seven stored types
    /// are kept, anything else is written as its text.
    /// </summary>
    public static LogDataType StoredTypeOf(object value) => value switch {
        double => LogDataType.Double,
        int => LogDataType.Integer,
        string => LogDataType.String,
        DateTime => LogDataType.DateTime,
        TimeSpan => LogDataType.TimeSpan,
        byte[] => LogDataType.Bytes,
        GeoCoordinate => LogDataType.GeoCoordinate,
        _ => LogDataType.String,
    };

    /// <summary>
    /// A position as text, "latitude, longitude" in degrees. Six decimals is about ten centimetres,
    /// and rounds away what snapping to the store's grid adds: 60.3913 reads back as 60.3913 and not
    /// as the 60.391299998 the grid holds, which is what lets a search find what was typed.
    /// </summary>
    public static string PositionText(GeoCoordinate position, int decimals = 6) {
        if (position.IsEmpty) return string.Empty;
        var format = "0." + new string('#', Math.Clamp(decimals, 0, 9));
        return position.Latitude.ToString(format, CultureInfo.InvariantCulture) + ", " + position.Longitude.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A position from what the code recording it had: a GeoCoordinate, "59.91, 10.75" (or with a
    /// semicolon or a space between the two), the json a GeoCoordinate is written as, a (latitude,
    /// longitude) tuple, or an array of the two. Latitude first, always, the way a GeoCoordinate and
    /// every map service write it. A latitude past a pole or a longitude past the antimeridian is not
    /// a place, and is left out rather than folded back onto the globe.
    /// </summary>
    public static bool TryParsePosition(object? value, out GeoCoordinate position) {
        position = toGeo(value) is GeoCoordinate g ? g : GeoCoordinate.Empty;
        return !position.IsEmpty;
    }
    static object? toGeo(object? value) {
        switch (value) {
            case GeoCoordinate g: return g.IsEmpty ? null : g;
            case ValueTuple<double, double> t: return place(t.Item1, t.Item2);
            case ValueTuple<float, float> t: return place(t.Item1, t.Item2);
            case ValueTuple<decimal, decimal> t: return place((double)t.Item1, (double)t.Item2);
            case Tuple<double, double> t: return place(t.Item1, t.Item2);
            case double[] { Length: 2 } a: return place(a[0], a[1]);
            case float[] { Length: 2 } a: return place(a[0], a[1]);
            case string text: return parsePosition(text);
            default: return null;
        }
    }
    static object? place(double latitude, double longitude) {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude)) return null;
        if (latitude < -90 || latitude > 90 || longitude < -180 || longitude > 180) return null;
        return new GeoCoordinate(latitude, longitude);
    }
    static object? parsePosition(string text) {
        text = text.Trim();
        if (text.Length == 0) return null;
        if (text[0] == '{') {
            try {
                var g = System.Text.Json.JsonSerializer.Deserialize<GeoCoordinate>(text);
                return g.IsEmpty ? null : place(g.Latitude, g.Longitude);
            } catch (System.Text.Json.JsonException) {
                return null;
            }
        }
        // "(59.91, 10.75)" and "[59.91, 10.75]" are the same two numbers
        text = text.TrimStart('(', '[').TrimEnd(')', ']');
        var parts = text.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return null;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) return null;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) return null;
        return place(lat, lon);
    }

    static string toText(object value) => value switch {
        string s => s,
        GeoCoordinate g => PositionText(g),
        // a moment is written the way it sorts, in UTC, whatever clock it was taken on
        DateTime dt => asUtc(dt).ToString("o", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),
        // numbers read the same on every server: a comma for a decimal point is a Norwegian server's
        // idea of 1.5, and the search and the export would disagree with the one next to it
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    static object? toInteger(object value) {
        switch (value) {
            case int i: return i;
            case long l: return clamp(l);
            case short s: return (int)s;
            case ushort us: return (int)us;
            case byte b: return (int)b;
            case sbyte sb: return (int)sb;
            case uint ui: return ui > int.MaxValue ? int.MaxValue : (int)ui;
            case ulong ul: return ul > int.MaxValue ? int.MaxValue : (int)ul;
            case bool flag: return flag ? 1 : 0;
            case Enum e: return clamp(Convert.ToInt64(e, CultureInfo.InvariantCulture));
            case string text:
                if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
                return toDouble(text) is double d ? round(d) : null;
            default:
                return toDouble(value) is double number ? round(number) : null;
        }
    }
    static int clamp(long value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);
    static object round(double value) => (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), int.MinValue, int.MaxValue);

    static object? toDouble(object value) {
        double? d = value switch {
            double v => v,
            float f => f,
            decimal m => (double)m,
            int i => i,
            long l => l,
            short s => s,
            ushort us => us,
            byte b => b,
            sbyte sb => sb,
            uint ui => ui,
            ulong ul => ul,
            bool flag => flag ? 1 : 0,
            // a duration measured is a number of milliseconds, which is what every duration column
            // of the system logs holds too
            TimeSpan ts => ts.TotalMilliseconds,
            Enum e => Convert.ToInt64(e, CultureInfo.InvariantCulture),
            string text => double.TryParse(text.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
            _ => null,
        };
        // not a number is not a measurement: one NaN makes every average it is part of NaN
        if (d is not double number || double.IsNaN(number) || double.IsInfinity(number)) return null;
        return number;
    }

    static object? toDateTime(object value) => value switch {
        DateTime dt => asUtc(dt),
        DateTimeOffset dto => dto.UtcDateTime,
        DateOnly date => DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
        string text => DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed.UtcDateTime : null,
        _ => null,
    };
    // log files hold UTC: an unspecified moment is taken to be one already, a local one is moved
    static DateTime asUtc(DateTime dt) => dt.Kind switch {
        DateTimeKind.Utc => dt,
        DateTimeKind.Local => dt.ToUniversalTime(),
        _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
    };

    static object? toTimeSpan(object value) {
        switch (value) {
            case TimeSpan ts: return ts;
            case TimeOnly time: return time.ToTimeSpan();
            case string text:
                if (TimeSpan.TryParse(text.Trim(), CultureInfo.InvariantCulture, out var parsed)) return parsed;
                return toDouble(text) is double ms ? fromMs(ms) : null;
            default:
                // a plain number for a duration is taken as milliseconds, the other way round of the above
                return value is not bool && toDouble(value) is double number ? fromMs(number) : null;
        }
    }
    static object? fromMs(double ms) => Math.Abs(ms) < TimeSpan.MaxValue.TotalMilliseconds ? TimeSpan.FromMilliseconds(ms) : null;

    static object? toBytes(object value) => value switch {
        byte[] bytes => bytes,
        ReadOnlyMemory<byte> memory => memory.ToArray(),
        Memory<byte> memory => memory.ToArray(),
        ArraySegment<byte> segment => segment.ToArray(),
        string text => Encoding.UTF8.GetBytes(text),
        _ => null,
    };
}
