using Relatude.DB.IO;

namespace Relatude.DB.Logging;
public class LogSettings {
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public Dictionary<string, LogProperty> Properties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public FileInterval FileInterval { get; set; } = FileInterval.Day;
    public bool IsEnabled() => EnableLog || EnableStatistics;
    public bool EnableLog { get; set; } = true;
    public bool EnableStatistics { get; set; } = true;
    public bool EnableLogTextFormat { get; set; } = false;
    /// <summary>
    /// Indicates how many items are kept for each interval type.
    /// A value of 1 gives the following:
    /// Second => 60,
    /// Minute => 60,
    /// Hour => 48,
    /// Day => 60,
    /// Week => 52,
    /// Month => 60
    /// The value given is multiplied by these numbers.
    /// As an example. If the value is 2 you are able to query statistics for the last 120 (60x2) days when you group by days.
    /// </summary>
    public int ResolutionRowStats { get; set; } = 10;
    public DayOfWeek FirstDayOfWeek { get; set; } 
    public int MaxAgeOfLogFilesInDays { get; set; } = 100;
    public int MaxTotalSizeOfLogFilesInMb { get; set; } = 100;
    public bool Compressed { get; set; }
}
public enum LogDataType {
    DateTime,
    TimeSpan,
    String,
    Integer,
    Double,
    Bytes,
}
public enum StatisticsType {
    Count = 0,
    Sum = 1,
    AvgMinMax = 2,
    CountSumAvgMinMax = 3,
    UniqueCountWithValues = 4, // Exact but only small data sets, recommended <100
    UniqueCountHashedValues = 5, // Accurate, but medium size data set, recommended <10000
    UniqueCountEstimate = 6, // 99% accurate but infinite data set size
}
public class StatisticsInfo {
    public StatisticsInfo(StatisticsType statisticsType, int resolution = 3) {
        StatisticsType = statisticsType;
        if (resolution < 1) resolution = 1;
        Resolution = resolution;
    }
    public StatisticsType StatisticsType { get; } = StatisticsType.Count;
    public int Resolution { get; } = 1;
}
public class LogProperty {
    public string Name { get; set; } = string.Empty;
    public LogDataType DataType { get; set; } = LogDataType.String;
    public List<StatisticsInfo> Statistics { get; set; } = new();
}
