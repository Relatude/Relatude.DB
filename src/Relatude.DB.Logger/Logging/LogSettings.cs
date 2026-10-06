using Relatude.DB.IO;
using System.Globalization;
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
    /// <summary>Writes the settings through the IO provider, by default as the log's definition in the
    /// log folder: log/{key}.json (<see cref="FileKeyUtility.Logger_GetDefinition"/>).</summary>
    public void Save(IIOProvider io, string[]? fileKey = null) {
        io.WriteAllTextUTF8(fileKey ?? FileKeyUtility.Logger_GetDefinition(Key), ToJson());
    }
    public static LogSettings Load(IIOProvider io, string[] fileKey) => FromJson(io.ReadAllTextUTF8(fileKey));
    /// <summary>The settings of the log with this key, if they were saved to the default location.</summary>
    public static LogSettings? LoadIfSaved(IIOProvider io, string logKey) {
        var fileKey = FileKeyUtility.Logger_GetDefinition(logKey);
        return io.ExistsAndIsNotEmpty(fileKey) ? Load(io, fileKey) : null;
    }
    /// <summary>The settings of every log saved to the default location, ordered by file name. The key
    /// inside a file wins over the one in its name.</summary>
    public static List<LogSettings> LoadAll(IIOProvider io)
        => FileKeyUtility.Logger_GetAllDefinitionFileKeys(io).Where(io.ExistsAndIsNotEmpty).Select(k => Load(io, k)).ToList();
    /// <summary>Deletes the saved settings of the log in the default location.</summary>
    public static void DeleteSaved(IIOProvider io, string logKey) => io.DeleteFileIfItExists(FileKeyUtility.Logger_GetDefinition(logKey));

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
                if (stat != null && stat.ProblemWithParameters() is string problem)
                    throw new ArgumentException($"Log '{Key}': property '{kv.Key}': {problem}");
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
    /// <summary>A position, as a <see cref="Common.GeoCoordinate"/>: 8 bytes, on the store's own 1 cm grid.</summary>
    GeoCoordinate,
}
public enum StatisticsType {
    Count = 0,
    Sum = 1,
    AvgMinMax = 2,
    CountSumAvgMinMax = 3,
    UniqueCountWithValues = 4, // Exact but only small data sets, recommended <100
    UniqueCountHashedValues = 5, // Accurate, but medium size data set, recommended <10000
    UniqueCountEstimate = 6, // HyperLogLog: about 99% accurate, in fixed memory however many values there are
    // ---- positions (GeoCoordinate columns) ----
    GeoSpread = 7, // centre and spread: spherical mean, standard distance, deviational ellipse, bounding box
    GeoDistance = 8, // count, total, average, min and max of the distance to a reference point, in meters
    GeoDistanceBands = 9, // count per band of distance from a reference point
    GeoZones = 10, // count per named zone (a circle), the first one a position is in, or "Outside"
    GeoCoverage = 11, // estimated number of distinct grid cells with a position in them (HyperLogLog)
    GeoHeatmap = 12, // count per grid cell, cells merged where there is little in them to stay within a budget
}
public class StatisticsInfo {
    /// <summary>The cell level a coverage or a heatmap uses when none is given: about 300 m tall.</summary>
    public const int DefaultGeoLevel = 16;
    /// <summary>
    /// A statistic, and - for the ones about positions - what it is measured against. A parameter a
    /// statistic has no use for is kept, but ignored.
    /// </summary>
    /// <param name="reference">The point <see cref="StatisticsType.GeoDistance"/> and <see cref="StatisticsType.GeoDistanceBands"/> measure from.</param>
    /// <param name="bands">The edges between the bands of <see cref="StatisticsType.GeoDistanceBands"/>, in meters: [1000, 5000] is under 1 km, 1-5 km and over 5 km.</param>
    /// <param name="zones">The zones <see cref="StatisticsType.GeoZones"/> counts by, tested in this order.</param>
    /// <param name="level">The cell level of <see cref="StatisticsType.GeoCoverage"/> and <see cref="StatisticsType.GeoHeatmap"/> (see <see cref="Common.GeoCell"/>); 0 is <see cref="DefaultGeoLevel"/>.</param>
    [JsonConstructor] // a resolution left out of the json gets the default below
    public StatisticsInfo(StatisticsType statisticsType, int resolution = 3, Common.GeoCoordinate reference = default, double[]? bands = null, GeoZone[]? zones = null, int level = 0) {
        StatisticsType = statisticsType;
        if (resolution < 1) resolution = 1;
        Resolution = resolution;
        Reference = reference;
        // the edges in order, once each: the order they were typed in says nothing
        Bands = bands == null ? null : [.. bands.Where(b => double.IsFinite(b) && b > 0).Distinct().Order()];
        Zones = zones?.Where(z => z != null).ToArray();
        Level = level;
    }
    public StatisticsType StatisticsType { get; } = StatisticsType.Count;
    public int Resolution { get; } = 1;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Common.GeoCoordinate Reference { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double[]? Bands { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeoZone[]? Zones { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Level { get; }
    /// <summary>The level actually used: <see cref="Level"/>, or the default when none was given.</summary>
    [JsonIgnore]
    public int EffectiveLevel => Level <= 0 ? DefaultGeoLevel : Math.Min(Level, Common.GeoCell.MaxLevel);
    [JsonIgnore]
    public bool IsGeo => StatisticsType >= StatisticsType.GeoSpread;

    /// <summary>What is wrong with the parameters a statistic needs, or null when nothing is.</summary>
    public string? ProblemWithParameters() {
        switch (StatisticsType) {
            case StatisticsType.GeoDistance:
                return Reference.IsEmpty ? "distances need a reference point to be measured from." : null;
            case StatisticsType.GeoDistanceBands:
                if (Reference.IsEmpty) return "distance bands need a reference point to be measured from.";
                return Bands is not { Length: > 0 } ? "distance bands need at least one distance to divide the bands at." : null;
            case StatisticsType.GeoZones:
                if (Zones is not { Length: > 0 }) return "zones need at least one zone.";
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var z in Zones) {
                    if (string.IsNullOrWhiteSpace(z.Name)) return "every zone needs a name.";
                    if (string.Equals(z.Name, GeoZone.OutsideName, StringComparison.OrdinalIgnoreCase)) return $"'{GeoZone.OutsideName}' is what a position in no zone is counted as, so no zone can have that name.";
                    if (!names.Add(z.Name)) return $"two zones are named '{z.Name}'.";
                    if (z.Center.IsEmpty) return $"the zone '{z.Name}' needs a centre.";
                    if (!(z.RadiusMeters > 0) || !double.IsFinite(z.RadiusMeters)) return $"the zone '{z.Name}' needs a radius greater than zero.";
                }
                return null;
            case StatisticsType.GeoCoverage:
            case StatisticsType.GeoHeatmap:
                return Level < 0 || Level > Common.GeoCell.MaxLevel ? $"the cell level is 1 to {Common.GeoCell.MaxLevel} (or 0 for the default, {DefaultGeoLevel})." : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// The parameters as one string, empty when the statistic has none: a statistic kept with other
    /// parameters is another statistic, and its state is found by a key that includes these.
    /// </summary>
    internal string ParameterSignature() {
        return StatisticsType switch {
            StatisticsType.GeoDistance => "r" + Reference.StorageValue.ToString(CultureInfo.InvariantCulture),
            StatisticsType.GeoDistanceBands => "r" + Reference.StorageValue.ToString(CultureInfo.InvariantCulture)
                + "b" + string.Join(",", (Bands ?? []).Select(b => b.ToString("R", CultureInfo.InvariantCulture))),
            StatisticsType.GeoZones => "z" + string.Join("|", (Zones ?? []).Select(z =>
                z.Name.Length.ToString(CultureInfo.InvariantCulture) + ":" + z.Name + "@" + z.Center.StorageValue.ToString(CultureInfo.InvariantCulture) + "~" + z.RadiusMeters.ToString("R", CultureInfo.InvariantCulture))),
            StatisticsType.GeoCoverage or StatisticsType.GeoHeatmap => "l" + EffectiveLevel.ToString(CultureInfo.InvariantCulture),
            _ => string.Empty,
        };
    }
}
/// <summary>A named circle on the Earth: what <see cref="StatisticsType.GeoZones"/> counts positions by.</summary>
public sealed class GeoZone {
    /// <summary>What a position in none of the zones is counted as.</summary>
    public const string OutsideName = "Outside";
    [JsonConstructor]
    public GeoZone(string name, Common.GeoCoordinate center, double radiusMeters) {
        Name = name?.Trim() ?? string.Empty;
        Center = center;
        RadiusMeters = radiusMeters;
    }
    public string Name { get; }
    public Common.GeoCoordinate Center { get; }
    public double RadiusMeters { get; }
    public bool Contains(Common.GeoCoordinate position) => !position.IsEmpty && position.IsWithin(Center, RadiusMeters);
}
public class LogProperty {
    public string Name { get; set; } = string.Empty;
    public LogDataType DataType { get; set; } = LogDataType.String;
    public List<StatisticsInfo> Statistics { get; set; } = new();
}
