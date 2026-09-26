using Relatude.DB.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relatude.DB.Logging;
/// <summary>
/// The settings of one log. They can be written to and read from json (<see cref="ToJson"/>,
/// <see cref="FromJson"/>), either as a local file (<see cref="SaveToFile"/>, <see cref="LoadFromFile"/>)
/// or through an IO provider (<see cref="Save"/>, <see cref="Load"/>, <see cref="LoadAll"/>), by
/// default as log.[key].settings.json in the log folder beside the log's own files.
/// In the json the enums are written by name, and read by name or number.
/// </summary>
public class LogSettings {
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    /// <summary>What the log is for, in a sentence or two: shown where the log is listed. Optional.</summary>
    public string Description { get; set; } = string.Empty;
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

    static readonly JsonSerializerOptions _jsonOptions = new() {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip, // the files are meant to be editable by hand
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };
    public string ToJson() {
        Validate();
        return JsonSerializer.Serialize(this, _jsonOptions);
    }
    /// <summary>Reads settings written by <see cref="ToJson"/> or by hand. Throws a JsonException for
    /// malformed json and an ArgumentException for settings no log could be started with.</summary>
    public static LogSettings FromJson(string json) {
        json = json.TrimStart((char)0xFEFF); // a BOM left by an editor
        var settings = JsonSerializer.Deserialize<LogSettings>(json, _jsonOptions)
            ?? throw new ArgumentException("The log settings json is empty (null).");
        settings.normalize();
        settings.Validate();
        return settings;
    }
    /// <summary>Writes the settings to a file on the local disk, replacing any file already there.</summary>
    public void SaveToFile(string filePath) {
        var json = ToJson();
        var folder = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        // written aside and moved over, so a crash mid write never leaves a half file behind
        var temp = filePath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, filePath, true);
    }
    /// <summary>A copy that shares nothing with this one, so it can be changed without changing a log
    /// that is running on these settings.</summary>
    public LogSettings Clone() {
        var copy = JsonSerializer.Deserialize<LogSettings>(JsonSerializer.Serialize(this, _jsonOptions), _jsonOptions)!;
        copy.normalize();
        return copy;
    }
    public static LogSettings LoadFromFile(string filePath) => FromJson(File.ReadAllText(filePath));
    /// <summary>Writes the settings through the IO provider, by default to the log's settings file
    /// in the log folder (<see cref="FileKeyUtility.Logger_GetSettings"/>).</summary>
    public void Save(IIOProvider io, string[]? fileKey = null) {
        io.WriteAllTextUTF8(fileKey ?? FileKeyUtility.Logger_GetSettings(Key), ToJson());
    }
    public static LogSettings Load(IIOProvider io, string[] fileKey) => FromJson(io.ReadAllTextUTF8(fileKey));
    /// <summary>The settings of the log with this key, if they were saved to the default location.</summary>
    public static LogSettings? LoadIfSaved(IIOProvider io, string logKey) {
        var fileKey = FileKeyUtility.Logger_GetSettings(logKey);
        return io.ExistsAndIsNotEmpty(fileKey) ? Load(io, fileKey) : null;
    }
    /// <summary>The settings of every log saved to the default location, ordered by file name. The
    /// key inside a file wins over the one in its name.</summary>
    public static List<LogSettings> LoadAll(IIOProvider io) {
        return FileKeyUtility.Logger_GetAllSettingsFileKeys(io).Where(io.ExistsAndIsNotEmpty).Select(k => Load(io, k)).ToList();
    }
    public static void DeleteSaved(IIOProvider io, string logKey) => io.DeleteFileIfItExists(FileKeyUtility.Logger_GetSettings(logKey));

    // json leaves nulls where the code assumes collections, and the property dictionary loses its
    // case insensitive comparer
    void normalize() {
        Key ??= string.Empty;
        Name ??= string.Empty;
        Description ??= string.Empty;
        var properties = new Dictionary<string, LogProperty>(StringComparer.OrdinalIgnoreCase);
        if (Properties != null) {
            foreach (var kv in Properties) {
                if (kv.Value == null) throw new ArgumentException($"Log '{Key}': property '{kv.Key}' has no settings.");
                if (!properties.TryAdd(kv.Key, kv.Value))
                    throw new ArgumentException($"Log '{Key}': property '{kv.Key}' is defined more than once (property keys are case insensitive).");
                kv.Value.Name ??= string.Empty;
                kv.Value.Statistics = kv.Value.Statistics?.Where(s => s != null).ToList() ?? [];
            }
        }
        Properties = properties;
    }
    /// <summary>Throws an ArgumentException if the settings could not start a log: a key that is
    /// missing or unusable in a file name, or an enum value out of range.</summary>
    public void Validate() {
        if (string.IsNullOrWhiteSpace(Key)) throw new ArgumentException("A log must have a key.");
        if (Key.IndexOfAny(_invalidKeyChars) >= 0 || Key != Key.Trim())
            throw new ArgumentException($"The log key '{Key}' cannot be used in a file name.");
        // the parts of a log's file names are separated by dots (log.[key].day.[date].bin), so a key
        // holding one would let the files of one log match the search pattern of another
        if (Key.Contains('.')) throw new ArgumentException($"The log key '{Key}' cannot contain a dot.");
        if (!Enum.IsDefined(FileInterval)) throw new ArgumentException($"Log '{Key}': unknown file interval {(int)FileInterval}.");
        if (!Enum.IsDefined(FirstDayOfWeek)) throw new ArgumentException($"Log '{Key}': unknown first day of week {(int)FirstDayOfWeek}.");
        if (Properties == null) throw new ArgumentException($"Log '{Key}': properties missing.");
        foreach (var kv in Properties) {
            if (string.IsNullOrWhiteSpace(kv.Key)) throw new ArgumentException($"Log '{Key}': a property has no key.");
            if (kv.Value == null) throw new ArgumentException($"Log '{Key}': property '{kv.Key}' has no settings.");
            if (!Enum.IsDefined(kv.Value.DataType)) throw new ArgumentException($"Log '{Key}': property '{kv.Key}' has unknown data type {(int)kv.Value.DataType}.");
            if (kv.Value.Statistics == null) continue;
            foreach (var stat in kv.Value.Statistics) {
                if (stat != null && !Enum.IsDefined(stat.StatisticsType))
                    throw new ArgumentException($"Log '{Key}': property '{kv.Key}' has unknown statistics type {(int)stat.StatisticsType}.");
            }
        }
    }
    static readonly char[] _invalidKeyChars = [.. Path.GetInvalidFileNameChars(), '*', '?', '/', '\\'];
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
    [JsonConstructor] // a resolution left out of the json gets the default below
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
