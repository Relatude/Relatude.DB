using Relatude.DB.IO;
using Relatude.DB.Logging;

namespace Relatude.Logger;
// Shared helpers for the Relatude.DB.Logger test suite.
internal static class H {
    // Monday 2026-06-01 10:00 UTC, fixed so interval math is deterministic
    public static readonly DateTime T0 = new(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc);
    public static readonly FileKeyUtility FileKeys = new(null);
    public static LogSettings Settings(string key = "test", Action<LogSettings>? configure = null) {
        var s = new LogSettings {
            Key = key,
            Name = key,
            FileInterval = FileInterval.Day,
            EnableLog = true,
            EnableStatistics = true,
            FirstDayOfWeek = DayOfWeek.Monday,
        };
        configure?.Invoke(s);
        return s;
    }
    // one log with a property for every statistics type
    public static LogSettings RichSettings(string key = "test", Action<LogSettings>? configure = null) {
        return Settings(key, s => {
            s.Properties.Add("pInt", new LogProperty {
                Name = "Int",
                DataType = LogDataType.Integer,
                Statistics = [new(StatisticsType.Count), new(StatisticsType.Sum), new(StatisticsType.AvgMinMax), new(StatisticsType.CountSumAvgMinMax)],
            });
            s.Properties.Add("pDouble", new LogProperty {
                Name = "Double",
                DataType = LogDataType.Double,
                Statistics = [new(StatisticsType.Sum), new(StatisticsType.AvgMinMax), new(StatisticsType.CountSumAvgMinMax)],
            });
            s.Properties.Add("pGroup", new LogProperty {
                Name = "Group",
                DataType = LogDataType.String,
                Statistics = [new(StatisticsType.UniqueCountWithValues)],
            });
            s.Properties.Add("pUnique", new LogProperty {
                Name = "Unique",
                DataType = LogDataType.String,
                Statistics = [new(StatisticsType.UniqueCountHashedValues), new(StatisticsType.UniqueCountEstimate)],
            });
            configure?.Invoke(s);
        });
    }
    public static LogStore Store(IIOProvider io, params LogSettings[] settings) => new(io, settings, new FileKeyUtility(null));
    public static LogEntry Entry(DateTime timestamp, params (string Key, object Value)[] values) {
        var e = new LogEntry { Timestamp = timestamp };
        foreach (var (key, value) in values) e.Values.Add(key, value);
        return e;
    }
    // 4 records in hour 10:00 and 2 records in hour 11:00, fixed values referenced by many asserts:
    // pInt:    hour A = 1,2,3,4 (sum 10)      hour B = 10,20 (sum 30)
    // pDouble: hour A = 0.5,1.5,2.5,3.5 (8.0) hour B = 1.0,2.0 (3.0)
    // pGroup:  hour A = a,a,b,c               hour B = a,d
    // pUnique: hour A = u1,u2,u1,u3 (3 uniq)  hour B = u1,u1 (1 uniq)
    public static void RecordRichHours(LogStore store, string key = "test") {
        store.Record(key, Entry(T0.AddMinutes(0), ("pInt", 1), ("pDouble", 0.5), ("pGroup", "a"), ("pUnique", "u1")));
        store.Record(key, Entry(T0.AddMinutes(10), ("pInt", 2), ("pDouble", 1.5), ("pGroup", "a"), ("pUnique", "u2")));
        store.Record(key, Entry(T0.AddMinutes(20), ("pInt", 3), ("pDouble", 2.5), ("pGroup", "b"), ("pUnique", "u1")));
        store.Record(key, Entry(T0.AddMinutes(30), ("pInt", 4), ("pDouble", 3.5), ("pGroup", "c"), ("pUnique", "u3")));
        store.Record(key, Entry(T0.AddMinutes(60), ("pInt", 10), ("pDouble", 1.0), ("pGroup", "a"), ("pUnique", "u1")));
        store.Record(key, Entry(T0.AddMinutes(70), ("pInt", 20), ("pDouble", 2.0), ("pGroup", "d"), ("pUnique", "u1")));
    }
}
