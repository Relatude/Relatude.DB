using Relatude.DB.IO;
using Relatude.DB.Logging;
using Relatude.DB.Logging.Statistics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// Reading a log for the admin UI: its entries a page at a time, a search over a range, a statistic
/// as a series of points, and the whole of it as a file.
///
/// Shared by the system logs (<see cref="UILogs"/>) and the custom logs (<see cref="UICustomLogs"/>).
/// Both are a <see cref="ILogStore"/>, and a log describes itself through its <see cref="LogSettings"/>
/// - a property per column with the data type behind it, and the statistics each property declares -
/// so nothing here needs to know which log it is reading, or what kind.
///
/// Two things about the statistics decide the shape of a series:
///   - which statistic a property declares decides what can be drawn (a count is a line, a
///     CountSumAvgMinMax is a line with a min/max band, a UniqueCountWithValues is a breakdown per
///     value), so the series carries that kind and the client picks the chart from it;
///   - statistics are kept per interval type with a limited number of intervals each, so a range is
///     only answerable as far back as the log kept it. The range is clamped to that, and to a point
///     cap: filling in blank intervals walks one interval at a time, so a year of seconds would
///     otherwise be thirty million of them.
/// </summary>
static class UILogReader {
    // no chart shows more than a few hundred points, and every point beyond that is walked, held
    // and serialized for nothing
    internal const int MaxPoints = 400;
    // a breakdown with a hundred values is a wall of colour, not a graph: the rest becomes "Other"
    const int maxGroups = 10;
    const string otherGroup = "Other";

    // ---- what a log can draw ----

    /// <summary>
    /// The graphs a log can draw. Every log can draw its entry count over time (the row statistic,
    /// which every log keeps); a property adds one series per statistic it declares, as long as
    /// that statistic is one its data type supports - the same test the log makes when it creates
    /// them, repeated here so the UI never offers a graph that could only come back empty.
    /// </summary>
    internal static object[] SeriesOf(LogSettings setting) {
        var all = new List<object> {
            new {
                Property = (string?)null,
                Statistic = "Count",
                Kind = "count",
                Label = "Entries",
                DataType = "Integer",
                Resolution = Math.Max(1, setting.ResolutionRowStats),
            },
        };
        foreach (var property in setting.Properties) {
            foreach (var stat in property.Value.Statistics ?? []) {
                if (stat == null) continue;
                var kind = KindOf(stat.StatisticsType, property.Value.DataType);
                if (kind == null) continue; // the log would not create this statistic either
                all.Add(new {
                    Property = (string?)property.Key,
                    Statistic = stat.StatisticsType.ToString(),
                    Kind = kind,
                    Label = (string.IsNullOrWhiteSpace(property.Value.Name) ? property.Key : property.Value.Name) + " · " + LabelOf(stat.StatisticsType),
                    DataType = property.Value.DataType.ToString(),
                    stat.Resolution,
                });
            }
        }
        return [.. all];
    }

    /// <summary>What the client draws, and which Analyse* answers it. null = the log does not keep
    /// this statistic for this data type (the same rules as Log.createStatisticsIfPossible).</summary>
    internal static string? KindOf(StatisticsType type, LogDataType dataType) {
        var numeric = dataType is LogDataType.Integer or LogDataType.Double;
        return type switch {
            StatisticsType.Count => "count",
            StatisticsType.Sum when numeric => "sum",
            StatisticsType.AvgMinMax when numeric => "avgminmax",
            StatisticsType.CountSumAvgMinMax when numeric => "full",
            StatisticsType.UniqueCountWithValues when dataType is not LogDataType.Bytes => "groups",
            StatisticsType.UniqueCountHashedValues when dataType is not LogDataType.Bytes => "count",
            StatisticsType.UniqueCountEstimate when dataType is not LogDataType.Bytes => "count",
            _ => null,
        };
    }
    internal static string LabelOf(StatisticsType type) => type switch {
        StatisticsType.Count => "count",
        StatisticsType.Sum => "total",
        StatisticsType.AvgMinMax => "avg, min, max",
        StatisticsType.CountSumAvgMinMax => "avg, min, max",
        StatisticsType.UniqueCountWithValues => "by value",
        StatisticsType.UniqueCountHashedValues => "unique",
        StatisticsType.UniqueCountEstimate => "unique, estimated",
        _ => type.ToString(),
    };

    // ---- reading entries ----

    /// <summary>
    /// One page of a range, or of the entries in it a search matched. The log files are read by UTC
    /// timestamp and refuse anything else, so an unspecified kind (an omitted bound, or a value that
    /// arrived without a marker) is taken as UTC here.
    /// </summary>
    internal static object Extract(ILogStore store, string logKey, long? lastMs, DateTime? fromUtc, DateTime? toUtc, int skip, int take, string? search, bool caseSensitive, bool newestFirst = true) {
        var (fromBound, toBound) = Window(lastMs, fromUtc, toUtc);
        var from = fromBound ?? DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
        var to = toBound ?? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
        skip = Math.Max(0, skip);
        // A page of the table is a hundred rows, but the column filters search what the browser
        // already holds, so filtering asks for a window of thousands in one call. Reading a range
        // reads every record in it whatever the take is, so a larger one costs a larger response
        // and no more work here.
        take = Math.Clamp(take, 1, 10000);
        // A search reads the same records, and tests each one: it costs the range, not the number
        // of matches, so what it is given is the range the page is already showing. The total then
        // counts matches rather than entries, which is what the page says it is.
        var parsed = LogSearch.Parse(search, caseSensitive);
        int total;
        var entries = parsed.IsEmpty
            ? store.ExtractLog(logKey, from, to, skip, take, newestFirst, out total)
            : store.SearchLog(logKey, parsed, from, to, skip, take, newestFirst, out total);
        return new {
            Total = total,
            Skip = skip,
            Take = take,
            Searched = !parsed.IsEmpty,
            Entries = entries.Select(e => new {
                TimestampUtc = Utc(e.Timestamp),
                e.Values,
            }).ToArray(),
        };
    }

    // ---- a log as a file ----

    /// <summary>
    /// A log written out as a file: the whole of it, or one range, as tab separated text ("tsv"),
    /// comma separated text ("csv") or one json object per line ("jsonl"). The timestamp no log
    /// declares comes first - ISO 8601 in UTC, so it sorts as text - and then one column per property
    /// the log declares, in the order its table shows them.
    ///
    /// The range is walked one slice at a time rather than asked for in one call, because
    /// extracting a range reads every record in it into memory: a whole log of a busy database
    /// would otherwise be held at once. A slice is never smaller than one of the log's own files,
    /// since a smaller one would only read the same file again, and the rows come out oldest
    /// first - the order the files are walked in.
    ///
    /// A search narrows the file to the entries matching it. It is tested here rather than asked
    /// of the log, because the slices are already being read one at a time: searching each of them
    /// would read the same records twice, once to count the matches and once to write them.
    /// </summary>
    internal static async Task WriteEntries(HttpContext http, ILogStore store, string logKey, DateTime? fromUtc, DateTime? toUtc, string? search, bool caseSensitive, string format) {
        var setting = store.GetSetting(logKey); // an unknown log throws here, before anything is written
        format = (format ?? "tsv").ToLowerInvariant();
        if (format is not ("tsv" or "csv" or "jsonl")) throw new Exception("Unknown file format: " + format + ". ");
        var parsed = LogSearch.Parse(search, caseSensitive);
        var first = AsUtc(store.GetTimestampOfFirstRecord(logKey));
        var last = AsUtc(store.GetTimestampOfLastRecord(logKey));
        var columns = setting.Properties.ToArray();
        var name = logKey + "-log-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "." + format;
        http.Response.ContentType = format switch {
            "csv" => "text/csv; charset=utf-8",
            "jsonl" => "application/x-ndjson; charset=utf-8",
            _ => "text/tab-separated-values; charset=utf-8",
        };
        http.Response.Headers.ContentDisposition = "attachment; filename=\"" + name + "\"";
        // BOM for the two a spreadsheet opens, so it reads the file as utf-8 without being told; none
        // for json lines, which a BOM would make the first line of something that is not json
        var writer = new StreamWriter(http.Response.Body, new UTF8Encoding(format != "jsonl"));
        await using (writer.ConfigureAwait(false)) {
            if (format != "jsonl") await writer.WriteAsync(row(format, ["Time", .. columns.Select(c => string.IsNullOrWhiteSpace(c.Value.Name) ? c.Key : c.Value.Name)]));
            // Nothing recorded: the header alone says what the file would have held.
            if (first is not DateTime firstRecord || last is not DateTime lastRecord) return;
            // An omitted bound is the whole log. A bound reaching past what the log holds is that
            // too, and is clamped rather than walked: a range starting at year one is half a
            // million empty day slices before the first record the log actually has.
            var rangeFrom = AsUtc(fromUtc) is DateTime f && f > firstRecord ? f : firstRecord;
            var rangeTo = AsUtc(toUtc) is DateTime t && t <= lastRecord ? t : lastRecord.AddTicks(1); // [from, to): the last record is in it
            var interval = setting.FileInterval;
            for (var sliceFrom = floorToSlice(rangeFrom, interval); sliceFrom < rangeTo; sliceFrom = nextSlice(sliceFrom, interval)) {
                if (http.RequestAborted.IsCancellationRequested) return;
                var sliceTo = nextSlice(sliceFrom, interval);
                var entries = store.ExtractLog(logKey, sliceFrom > rangeFrom ? sliceFrom : rangeFrom, sliceTo < rangeTo ? sliceTo : rangeTo,
                    0, int.MaxValue, false, out _);
                foreach (var entry in entries) {
                    if (http.RequestAborted.IsCancellationRequested) return;
                    if (!parsed.Matches(entry, setting)) continue;
                    if (format == "jsonl") {
                        await writer.WriteAsync(jsonLine(entry, columns));
                        continue;
                    }
                    await writer.WriteAsync(row(format, [
                        cell(entry.Timestamp),
                        .. columns.Select(c => cell(entry.Values.TryGetValue(c.Key, out var value) ? value : null)),
                    ]));
                }
            }
        }
    }

    // A slice covers whole log files: one day, or one month for a log keeping a file per month.
    // A log writing a file per minute or per hour reads several of them per slice, which is the
    // point - the slice is there to bound memory, not to read as little as possible.
    static DateTime floorToSlice(DateTime at, FileInterval interval) => interval == FileInterval.Month
        ? new DateTime(at.Year, at.Month, 1, 0, 0, 0, DateTimeKind.Utc)
        : new DateTime(at.Year, at.Month, at.Day, 0, 0, 0, DateTimeKind.Utc);
    static DateTime nextSlice(DateTime at, FileInterval interval) => interval == FileInterval.Month ? at.AddMonths(1) : at.AddDays(1);

    static string row(string format, IEnumerable<string> cells) => format == "csv"
        ? string.Join(',', cells.Select(csvQuote)) + "\r\n"
        : string.Join('\t', cells.Select(tsvClean)) + "\r\n";
    // Tab separated text has no escape - a value with a tab in it would become another column, and
    // one with a newline another row - so those become spaces.
    static string tsvClean(string text) => text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    // Comma separated text does have one (RFC 4180): a value holding a comma, a quote or a line break
    // is quoted, with its quotes doubled, and keeps its line breaks
    static string csvQuote(string text) => text.IndexOfAny([',', '"', '\r', '\n']) < 0 ? text : "\"" + text.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// One value as a text file holds it. Nothing is dressed up: the numbers are the numbers, and
    /// the timestamps sort as text.
    /// </summary>
    static string cell(object? value) => value switch {
        null => string.Empty,
        DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        byte[] bytes => bytes.Length + " bytes", // the log holds them, a text file cannot
        _ => value.ToString() ?? string.Empty,
    };

    // One entry as a line of json: its timestamp under a name no column can have (a key is letters,
    // digits, '-' and '_'), then its values as the json they are - the declared columns first, in
    // their order, and whatever else the entry carries after them. Bytes are base64, as json has it.
    static string jsonLine(LogEntry entry, KeyValuePair<string, LogProperty>[] columns) {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer)) {
            json.WriteStartObject();
            json.WriteString("@timestamp", DateTime.SpecifyKind(entry.Timestamp, DateTimeKind.Utc));
            foreach (var column in columns) {
                if (entry.Values.TryGetValue(column.Key, out var value)) writeJsonValue(json, column.Key, value);
            }
            foreach (var kv in entry.Values) {
                if (columns.Any(c => string.Equals(c.Key, kv.Key, StringComparison.OrdinalIgnoreCase))) continue;
                writeJsonValue(json, kv.Key, kv.Value);
            }
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }
    static void writeJsonValue(Utf8JsonWriter json, string name, object? value) {
        switch (value) {
            case null: json.WriteNull(name); break;
            case int i: json.WriteNumber(name, i); break;
            case double d: json.WriteNumber(name, d); break;
            case DateTime dt: json.WriteString(name, DateTime.SpecifyKind(dt, DateTimeKind.Utc)); break;
            // a duration as a number of milliseconds, the unit every duration column is read in
            case TimeSpan ts: json.WriteNumber(name, ts.TotalMilliseconds); break;
            case byte[] bytes: json.WriteBase64String(name, bytes); break;
            default: json.WriteString(name, value.ToString()); break;
        }
    }

    // ---- statistics ----

    /// <summary>
    /// One statistic of one log as points over a range. <paramref name="property"/> null is the log's
    /// own entry count. The range is clamped to what the statistic keeps and to <see cref="MaxPoints"/>.
    /// </summary>
    internal static object Series(ILogStore store, string logKey, string? property, string statistic, string interval, long? lastMs, DateTime? fromUtc, DateTime? toUtc, bool enabledStatistics) {
        var setting = store.GetSetting(logKey);
        var statisticType = parseStatistic(statistic);
        var intervalType = parseInterval(interval);
        var (fromBound, toBound) = Window(lastMs, fromUtc, toUtc);
        var to = toBound ?? DateTime.UtcNow;
        var requested = fromBound ?? to.AddHours(-24);
        if (requested >= to) throw new Exception("The time range is empty. ");
        var from = clamp(requested, to, intervalType, resolutionOf(setting, property, statisticType), setting.FirstDayOfWeek, out var clamped);
        var kind = property == null ? "count"
            : KindOf(statisticType, columnType(setting, property)) ?? throw new Exception("No " + statistic + " statistic is kept for " + property + ". ");
        object[] points;
        object? summary = null;
        string[] groups = [];
        switch (kind) {
            case "count": {
                    var values = property == null
                        ? store.AnalyseRows(logKey, intervalType, from, to, false, true)
                        : counts(store, logKey, property, statisticType, intervalType, from, to);
                    points = [.. values.Select(i => (object)new { FromUtc = Utc(i.From), i.HasValue, Value = i.HasValue ? i.Value : (int?)null })];
                    // a unique count has no combined form - unique values cannot be added up - so
                    // only the plain counts carry a total
                    if (property == null) {
                        var combined = store.AnalyseCombinedRows(logKey, intervalType, from, to);
                        summary = new { Total = combined.HasValue ? combined.Value : 0 };
                    } else if (statisticType == StatisticsType.Count) {
                        var combined = store.AnalyseCombinedCounts(logKey, property, intervalType, from, to);
                        summary = new { Total = combined.HasValue ? combined.Value : 0 };
                    }
                    break;
                }
            case "sum": {
                    if (columnType(setting, property!) == LogDataType.Integer) {
                        var values = store.AnalyseIntegerSums(logKey, property!, intervalType, from, to, false, true);
                        points = [.. values.Select(i => (object)new { FromUtc = Utc(i.From), i.HasValue, Value = i.HasValue ? i.Value : (double?)null })];
                        var combined = store.AnalyseCombinedIntegerSums(logKey, property!, intervalType, from, to);
                        summary = new { Total = combined.HasValue ? (double)combined.Value : 0d };
                    } else {
                        var values = store.AnalyseFloatSums(logKey, property!, intervalType, from, to, false, true);
                        points = [.. values.Select(i => (object)new { FromUtc = Utc(i.From), i.HasValue, Value = i.HasValue ? i.Value : (double?)null })];
                        var combined = store.AnalyseCombinedFloatSums(logKey, property!, intervalType, from, to);
                        summary = new { Total = combined.HasValue ? combined.Value : 0d };
                    }
                    break;
                }
            case "avgminmax": {
                    var values = store.AnalyseAvgMinMax(logKey, property!, intervalType, from, to, false, true);
                    points = [.. values.Select(i => (object)new {
                        FromUtc = Utc(i.From),
                        i.HasValue,
                        Value = i.HasValue ? i.Value.Avg : (double?)null,
                        Min = i.HasValue ? i.Value.Min : null,
                        Max = i.HasValue ? i.Value.Max : null,
                    })];
                    var combined = store.AnalyseCombinedAvgMinMax(logKey, property!, intervalType, from, to);
                    if (combined.HasValue) summary = new { combined.Value.Avg, combined.Value.Min, combined.Value.Max };
                    break;
                }
            case "full": {
                    var values = store.AnalyseCountSumAvgMinMax(logKey, property!, intervalType, from, to, false, true);
                    points = [.. values.Select(i => (object)new {
                        FromUtc = Utc(i.From),
                        i.HasValue,
                        Value = i.HasValue ? i.Value.Avg : (double?)null,
                        Min = i.HasValue ? i.Value.Min : null,
                        Max = i.HasValue ? i.Value.Max : null,
                        Sum = i.HasValue ? i.Value.Sum : (double?)null,
                        Count = i.HasValue ? i.Value.Count : (int?)null,
                    })];
                    var combined = store.AnalyseCombinedCountSumAvgMinMax(logKey, property!, intervalType, from, to);
                    if (combined.HasValue) summary = new { combined.Value.Count, combined.Value.Sum, combined.Value.Avg, combined.Value.Min, combined.Value.Max };
                    break;
                }
            case "groups": {
                    var values = store.AnalyseGroupCounts(logKey, property!, intervalType, from, to, false, true).ToArray();
                    var combined = store.AnalyseCombinedGroupCounts(logKey, property!, intervalType, from, to);
                    var totals = combined.HasValue ? combined.Value : [];
                    // the graph keeps the values that carry the shape and folds the tail into one.
                    // Ordered by name so a value keeps its colour between refreshes even when the
                    // order by size changes under it.
                    groups = [.. totals.OrderByDescending(kv => kv.Value).Take(maxGroups).Select(kv => kv.Key).Order(StringComparer.Ordinal)];
                    var named = groups.ToHashSet(StringComparer.Ordinal);
                    var hasOther = totals.Count > groups.Length;
                    points = [.. values.Select(i => {
                        var buckets = new Dictionary<string, int>(StringComparer.Ordinal);
                        var other = 0;
                        if (i.HasValue) {
                            foreach (var kv in i.Value) {
                                if (named.Contains(kv.Key)) buckets[kv.Key] = kv.Value;
                                else other += kv.Value;
                            }
                        }
                        if (hasOther) buckets[otherGroup] = other;
                        return (object)new { FromUtc = Utc(i.From), i.HasValue, Values = buckets };
                    })];
                    if (hasOther) groups = [.. groups, otherGroup];
                    summary = new {
                        Total = totals.Sum(kv => kv.Value),
                        Distinct = totals.Count,
                        Groups = totals.OrderByDescending(kv => kv.Value).Take(maxGroups).Select(kv => new { Name = kv.Key, Count = kv.Value }),
                    };
                    break;
                }
            default: throw new Exception("Unknown statistic. ");
        }
        return new {
            LogKey = logKey,
            Property = property,
            Statistic = statistic,
            Kind = kind,
            Interval = intervalType.ToString(),
            FromUtc = Utc(from),
            ToUtc = Utc(to),
            Clamped = clamped,
            EnabledStatistics = enabledStatistics,
            Groups = groups,
            Summary = summary,
            Points = points,
        };
    }

    static IEnumerable<Interval<int>> counts(ILogStore store, string logKey, string property, StatisticsType type, IntervalType interval, DateTime from, DateTime to) {
        return type switch {
            StatisticsType.Count => store.AnalyseCounts(logKey, property, interval, from, to, false, true),
            StatisticsType.UniqueCountHashedValues => store.AnalyseUniqueCounts(logKey, property, interval, from, to, false, true),
            StatisticsType.UniqueCountEstimate => store.AnalyseEstimatedUniqueCounts(logKey, property, interval, from, to, false, true),
            _ => throw new Exception("Unknown count statistic. "),
        };
    }

    static LogDataType columnType(LogSettings setting, string property) {
        if (!setting.Properties.TryGetValue(property, out var p)) throw new Exception("Unknown log property: " + property + ". ");
        return p.DataType;
    }
    // How many intervals of one type the statistic keeps: the row statistic and every property
    // statistic carry their own resolution, and the oldest interval is dropped as new ones arrive,
    // so asking further back than that can only produce blanks.
    static int resolutionOf(LogSettings setting, string? property, StatisticsType statistic) {
        if (property == null) return Math.Max(1, setting.ResolutionRowStats);
        if (!setting.Properties.TryGetValue(property, out var p)) return 1;
        var info = (p.Statistics ?? []).FirstOrDefault(s => s != null && s.StatisticsType == statistic);
        return Math.Max(1, info?.Resolution ?? 1);
    }
    /// <summary>How many intervals of a type a statistic of the given resolution keeps.</summary>
    internal static int KeptIntervals(IntervalType type, int resolution) => resolution * type switch {
        IntervalType.Second => 60,
        IntervalType.Minute => 60,
        IntervalType.Hour => 48,
        IntervalType.Day => 60,
        IntervalType.Week => 52,
        IntervalType.Month => 60,
        _ => 60,
    };
    static DateTime clamp(DateTime from, DateTime to, IntervalType interval, int resolution, DayOfWeek firstDayOfWeek, out bool clamped) {
        var allowed = Math.Min(MaxPoints, KeptIntervals(interval, resolution));
        var oldest = IntervalUtils.Floor(to, interval, firstDayOfWeek);
        for (var i = 0; i < allowed; i++) oldest = IntervalUtils.SubtractOne(oldest, interval);
        clamped = from < oldest;
        return clamped ? oldest : from;
    }
    static IntervalType parseInterval(string value) {
        return Enum.TryParse<IntervalType>(value, true, out var t) ? t : throw new Exception("Unknown interval: " + value + ". ");
    }
    static StatisticsType parseStatistic(string value) {
        return Enum.TryParse<StatisticsType>(value, true, out var t) ? t : throw new Exception("Unknown statistic: " + value + ". ");
    }

    // ---- time ----

    /// <summary>
    /// The range a page is asking about. A window given as "the last so many milliseconds" ends now,
    /// which is what a page following a log wants: the range moves with each sample instead of
    /// staying where it was when the page subscribed (see <see cref="UILiveFeeds"/>). Absolute
    /// bounds are left exactly as they were given.
    /// </summary>
    internal static (DateTime? From, DateTime? To) Window(long? lastMs, DateTime? fromUtc, DateTime? toUtc) {
        // a year in milliseconds is past what an int holds, and a year is one of the ranges the logs
        // page offers
        if (lastMs is not long ms || ms <= 0) return (AsUtc(fromUtc), AsUtc(toUtc));
        var to = DateTime.UtcNow;
        return (to.AddMilliseconds(-ms), to);
    }
    internal static DateTime? AsUtc(DateTime? value) {
        if (value is not DateTime v) return null;
        return v.Kind switch {
            DateTimeKind.Utc => v,
            DateTimeKind.Local => v.ToUniversalTime(),
            _ => DateTime.SpecifyKind(v, DateTimeKind.Utc),
        };
    }
    // Timestamps read off the log files are UTC, but one that reaches the browser without the
    // marker is read there as local time, quietly moving it by the offset
    internal static string? Utc(DateTime? value) {
        if (value is not DateTime v) return null;
        return DateTime.SpecifyKind(v, DateTimeKind.Utc).ToString("o");
    }
}
