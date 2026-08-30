using Relatude.DB.DataStores;
using Relatude.DB.Logging;
using Relatude.DB.Logging.Statistics;
using Relatude.DB.NodeServer.Settings;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// The logs section of the admin UI: the activity logs of one database, what they contain, and the
/// statistics kept alongside them.
///
/// Nothing here knows what a query log or a metrics log looks like. A log describes itself through
/// its <see cref="LogSettings"/> - a name, a property per column with the data type behind it, and
/// the statistics each property declares - and that description is what the client renders, both
/// for the tables and for the graphs. A log added to StoreLogger therefore shows up in the UI, with
/// its columns and its graphs, without a line changing here or in the browser.
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
sealed class UILogs {
    // no chart shows more than a few hundred points, and every point beyond that is walked, held
    // and serialized for nothing
    const int maxPoints = 400;
    // a breakdown with a hundred values is a wall of colour, not a graph: the rest becomes "Other"
    const int maxGroups = 10;
    const string otherGroup = "Other";

    readonly RelatudeDBServer _server;
    internal UILogs(RelatudeDBServer server) => _server = server;

    internal void Register(UICommands commands) {
        commands.Register("logs-info", ctx => info(ctx.Payload<StorePayload>().StoreId));
        commands.Register("logs-extract", ctx => extract(ctx.Payload<ExtractPayload>()));
        commands.Register("logs-series", ctx => series(ctx.Payload<SeriesPayload>()));
        commands.Register("logs-trace", ctx => trace(ctx.Payload<TracePayload>()));
        commands.Register("logs-enable", ctx => enable(ctx.Payload<EnablePayload>()));
        commands.Register("logs-clear", ctx => clear(ctx.Payload<ClearPayload>()));
        commands.Register("logs-rebuild-statistics", ctx => rebuild(ctx.Payload<LogPayload>()));
        commands.Register("logs-save", ctx => save(ctx.Payload<StorePayload>()));
        commands.Register("logs-restore", ctx => restore(ctx.Payload<StorePayload>()));
        commands.Register("logs-min-duration", ctx => minDuration(ctx.Payload<MinDurationPayload>()));
        commands.Register("logs-scans", ctx => scans(ctx.Payload<StorePayload>().StoreId));
        commands.Register("logs-scans-record", ctx => recordScans(ctx.Payload<ScanRecordPayload>()));
    }

    NodeStoreContainer container(Guid storeId) {
        if (!_server.Containers.TryGetValue(storeId, out var c)) throw new Exception("Database not found. ");
        return c;
    }
    // The logger of a closed database reads the same files, so everything but the live system trace
    // works while it is closed - which is exactly when the last errors before it stopped are worth
    // reading.
    IStoreLogger logger(Guid storeId) => container(storeId).GetLogger();

    // ---- what there is ----

    object info(Guid storeId) {
        var c = container(storeId);
        var log = c.GetLogger();
        var store = log.LogStore;
        // sizes and timestamps are read off the files, and a log writes in batches: without this the
        // page reports a log that has just started recording as empty until the next flush
        log.FlushToDiskNow();
        var saved = (c.Settings.LocalSettings?.LogRecording ?? []).ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);
        var logs = log.GetLogKeysAndNames().Select(kv => {
            var setting = store.GetSetting(kv.Key);
            saved.TryGetValue(kv.Key, out var remembered);
            return new {
                Key = kv.Key,
                Name = kv.Value,
                EnabledLog = log.IsLogEnabled(kv.Key),
                EnabledStatistics = log.IsStatisticsEnabled(kv.Key),
                // what the settings file will hand back at the next start, so the page can say
                // which switches are only holding until this database closes
                SavedLog = remembered?.Log ?? false,
                SavedStatistics = remembered?.Statistics ?? false,
                FirstRecordUtc = utc(store.GetTimestampOfFirstRecord(kv.Key)),
                LastRecordUtc = utc(store.GetTimestampOfLastRecord(kv.Key)),
                LogBytes = store.GetLogFileSize(kv.Key),
                StatisticsBytes = store.GetStatisticsFileSize(kv.Key),
                TotalBytes = store.GetFileSize(kv.Key),
                MaxAgeInDays = setting.MaxAgeOfLogFilesInDays,
                MaxSizeInMb = setting.MaxTotalSizeOfLogFilesInMb,
                Columns = setting.Properties.Select(p => new {
                    Key = p.Key,
                    Name = p.Value.Name,
                    DataType = p.Value.DataType.ToString(),
                }),
                Series = seriesOf(setting),
            };
        }).ToArray();
        return new {
            Open = c.IsOpen(),
            State = c.HasFailed ? "Error" : c.Store?.State.ToString() ?? "Closed",
            ScansRecording = log.RecordingPropertyHits,
            // the one recording rule that is not on or off: a busy site records every query it
            // serves unless the fast ones are left out
            MinQueryDurationMs = log.MinDurationMsBeforeLogging,
            SavedMinQueryDurationMs = c.Settings.LocalSettings?.MinQueryDurationMsBeforeLogging ?? 0,
            // configuration decides these when it names them, and would win at the next start
            CanSave = c.Settings.LocalSettings != null && !savingIsOverridden(c.Settings.Id, out _),
            TotalBytes = logs.Sum(l => l.TotalBytes),
            Logs = logs,
        };
    }

    /// <summary>
    /// The graphs a log can draw. Every log can draw its entry count over time (the row statistic,
    /// which every log keeps); a property adds one series per statistic it declares, as long as
    /// that statistic is one its data type supports - the same test the log makes when it creates
    /// them, repeated here so the UI never offers a graph that could only come back empty.
    /// </summary>
    static object[] seriesOf(LogSettings setting) {
        var all = new List<object> {
            new {
                Property = (string?)null,
                Statistic = "Count",
                Kind = "count",
                Label = "Entries",
                DataType = "Integer",
            },
        };
        foreach (var property in setting.Properties) {
            foreach (var stat in property.Value.Statistics ?? []) {
                if (stat == null) continue;
                var kind = kindOf(stat.StatisticsType, property.Value.DataType);
                if (kind == null) continue; // the log would not create this statistic either
                all.Add(new {
                    Property = (string?)property.Key,
                    Statistic = stat.StatisticsType.ToString(),
                    Kind = kind,
                    Label = property.Value.Name + " · " + labelOf(stat.StatisticsType),
                    DataType = property.Value.DataType.ToString(),
                });
            }
        }
        return [.. all];
    }

    // what the client draws, and which Analyse* answers it. null = the log does not keep this
    // statistic for this data type (the same rules as Log.createStatisticsIfPossible)
    static string? kindOf(StatisticsType type, LogDataType dataType) {
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
    static string labelOf(StatisticsType type) => type switch {
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

    object extract(ExtractPayload p) {
        var log = logger(p.StoreId);
        // the log files are read by UTC timestamp and refuse anything else, so an unspecified kind
        // (an omitted bound, or a value that arrived without a marker) is taken as UTC here
        var from = asUtc(p.FromUtc) ?? DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
        var to = asUtc(p.ToUtc) ?? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
        var skip = Math.Max(0, p.Skip);
        var take = Math.Clamp(p.Take, 1, 1000);
        var entries = log.ExtractLog(p.LogKey, from, to, skip, take, true, out var total);
        return new {
            Total = total,
            Skip = skip,
            Take = take,
            Entries = entries.Select(e => new {
                TimestampUtc = utc(e.Timestamp),
                e.Values,
            }),
        };
    }

    // The trace is the last messages the running database kept in memory: the ones written before
    // the system log was ever turned on, and the only ones there are when it is off.
    object trace(TracePayload p) {
        var c = container(p.StoreId);
        if (c.Store == null || !c.IsOpenOrOpening()) {
            return new { Open = false, Entries = Array.Empty<object>(), StartupError = startupError(c) };
        }
        var take = Math.Clamp(p.Take, 1, 500);
        return new {
            Open = true,
            Entries = c.Store.Datastore.GetSystemTrace(0, take).Select(e => new {
                TimestampUtc = utc(e.Timestamp),
                Type = e.Type.ToString(),
                e.Text,
                e.Details,
            }),
            StartupError = startupError(c),
        };
    }
    static object? startupError(NodeStoreContainer c) {
        if (c.StartUpException == null) return null;
        return new {
            TimeUtc = utc(c.StartUpExceptionDateTimeUTC),
            c.StartUpException.Message,
            Details = c.StartUpException.StackTrace,
        };
    }

    // ---- statistics ----

    object series(SeriesPayload p) {
        var log = logger(p.StoreId);
        var store = log.LogStore;
        var setting = store.GetSetting(p.LogKey);
        var statistic = parseStatistic(p.Statistic);
        var interval = parseInterval(p.Interval);
        var to = asUtc(p.ToUtc) ?? DateTime.UtcNow;
        var requested = asUtc(p.FromUtc) ?? to.AddHours(-24);
        if (requested >= to) throw new Exception("The time range is empty. ");
        var from = clamp(requested, to, interval, resolutionOf(setting, p.Property, statistic), setting.FirstDayOfWeek, out var clamped);
        var property = p.Property;
        var kind = property == null ? "count"
            : kindOf(statistic, columnType(setting, property)) ?? throw new Exception("No " + p.Statistic + " statistic is kept for " + property + ". ");
        object[] points;
        object? summary = null;
        string[] groups = [];
        switch (kind) {
            case "count": {
                    var values = property == null
                        ? store.AnalyseRows(p.LogKey, interval, from, to, false, true)
                        : counts(store, p.LogKey, property, statistic, interval, from, to);
                    points = [.. values.Select(i => (object)new { FromUtc = utc(i.From), i.HasValue, Value = i.HasValue ? i.Value : (int?)null })];
                    // a unique count has no combined form - unique values cannot be added up - so
                    // only the plain counts carry a total
                    if (property == null) {
                        var combined = store.AnalyseCombinedRows(p.LogKey, interval, from, to);
                        summary = new { Total = combined.HasValue ? combined.Value : 0 };
                    } else if (statistic == StatisticsType.Count) {
                        var combined = store.AnalyseCombinedCounts(p.LogKey, property, interval, from, to);
                        summary = new { Total = combined.HasValue ? combined.Value : 0 };
                    }
                    break;
                }
            case "sum": {
                    if (columnType(setting, property!) == LogDataType.Integer) {
                        var values = store.AnalyseIntegerSums(p.LogKey, property!, interval, from, to, false, true);
                        points = [.. values.Select(i => (object)new { FromUtc = utc(i.From), i.HasValue, Value = i.HasValue ? i.Value : (double?)null })];
                        var combined = store.AnalyseCombinedIntegerSums(p.LogKey, property!, interval, from, to);
                        summary = new { Total = combined.HasValue ? (double)combined.Value : 0d };
                    } else {
                        var values = store.AnalyseFloatSums(p.LogKey, property!, interval, from, to, false, true);
                        points = [.. values.Select(i => (object)new { FromUtc = utc(i.From), i.HasValue, Value = i.HasValue ? i.Value : (double?)null })];
                        var combined = store.AnalyseCombinedFloatSums(p.LogKey, property!, interval, from, to);
                        summary = new { Total = combined.HasValue ? combined.Value : 0d };
                    }
                    break;
                }
            case "avgminmax": {
                    var values = store.AnalyseAvgMinMax(p.LogKey, property!, interval, from, to, false, true);
                    points = [.. values.Select(i => (object)new {
                        FromUtc = utc(i.From),
                        i.HasValue,
                        Value = i.HasValue ? i.Value.Avg : (double?)null,
                        Min = i.HasValue ? i.Value.Min : null,
                        Max = i.HasValue ? i.Value.Max : null,
                    })];
                    var combined = store.AnalyseCombinedAvgMinMax(p.LogKey, property!, interval, from, to);
                    if (combined.HasValue) summary = new { combined.Value.Avg, combined.Value.Min, combined.Value.Max };
                    break;
                }
            case "full": {
                    var values = store.AnalyseCountSumAvgMinMax(p.LogKey, property!, interval, from, to, false, true);
                    points = [.. values.Select(i => (object)new {
                        FromUtc = utc(i.From),
                        i.HasValue,
                        Value = i.HasValue ? i.Value.Avg : (double?)null,
                        Min = i.HasValue ? i.Value.Min : null,
                        Max = i.HasValue ? i.Value.Max : null,
                        Sum = i.HasValue ? i.Value.Sum : (double?)null,
                        Count = i.HasValue ? i.Value.Count : (int?)null,
                    })];
                    var combined = store.AnalyseCombinedCountSumAvgMinMax(p.LogKey, property!, interval, from, to);
                    if (combined.HasValue) summary = new { combined.Value.Count, combined.Value.Sum, combined.Value.Avg, combined.Value.Min, combined.Value.Max };
                    break;
                }
            case "groups": {
                    var values = store.AnalyseGroupCounts(p.LogKey, property!, interval, from, to, false, true).ToArray();
                    var combined = store.AnalyseCombinedGroupCounts(p.LogKey, property!, interval, from, to);
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
                        return (object)new { FromUtc = utc(i.From), i.HasValue, Values = buckets };
                    })];
                    if (hasOther) groups = [.. groups, otherGroup];
                    summary = new {
                        Total = totals.Sum(kv => kv.Value),
                        Groups = totals.OrderByDescending(kv => kv.Value).Take(maxGroups).Select(kv => new { Name = kv.Key, Count = kv.Value }),
                    };
                    break;
                }
            default: throw new Exception("Unknown statistic. ");
        }
        return new {
            p.LogKey,
            p.Property,
            p.Statistic,
            Kind = kind,
            Interval = interval.ToString(),
            FromUtc = utc(from),
            ToUtc = utc(to),
            Clamped = clamped,
            EnabledStatistics = log.IsStatisticsEnabled(p.LogKey),
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
    static int keptIntervals(IntervalType type, int resolution) => resolution * type switch {
        IntervalType.Second => 60,
        IntervalType.Minute => 60,
        IntervalType.Hour => 48,
        IntervalType.Day => 60,
        IntervalType.Week => 52,
        IntervalType.Month => 60,
        _ => 60,
    };
    static DateTime clamp(DateTime from, DateTime to, IntervalType interval, int resolution, DayOfWeek firstDayOfWeek, out bool clamped) {
        var allowed = Math.Min(maxPoints, keptIntervals(interval, resolution));
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

    // ---- switches and cleaning ----

    object enable(EnablePayload p) {
        var log = logger(p.StoreId);
        // either switch rebuilds the log store, so only the one that actually changed is touched
        if (p.Log is bool wantLog && wantLog != log.IsLogEnabled(p.LogKey)) log.EnableLog(p.LogKey, wantLog);
        if (p.Statistics is bool wantStats && wantStats != log.IsStatisticsEnabled(p.LogKey)) log.EnableStatistics(p.LogKey, wantStats);
        return new { Log = log.IsLogEnabled(p.LogKey), Statistics = log.IsStatisticsEnabled(p.LogKey) };
    }

    object clear(ClearPayload p) {
        var log = logger(p.StoreId);
        var keys = p.LogKey == null ? log.GetLogKeysAndNames().Select(kv => kv.Key).ToArray() : [p.LogKey];
        foreach (var key in keys) {
            if (p.Log) log.ClearLog(key);
            if (p.Statistics) log.ClearStatistics(key);
        }
        return new { Cleared = true };
    }

    // Reads the log files back and aggregates them again. The statistics of a log that was recorded
    // with statistics off are empty however far back the log itself goes; this is what fills them
    // in, so the graphs cover the whole log rather than starting where the switch was flipped.
    object rebuild(LogPayload p) {
        var log = logger(p.StoreId);
        if (!log.IsStatisticsEnabled(p.LogKey)) throw new Exception("Turn statistics on before rebuilding them. ");
        log.LogStore.RebuildStatistics(p.LogKey);
        return new { Rebuilt = true };
    }

    /// <summary>
    /// Writes what every log is recording right now into the settings file, so the next start brings
    /// it back. Only the recording switches and the query threshold are saved - a log's own limits
    /// (file interval, age, size) are its own, and the database is not reopened: the switches are
    /// already live, this is only what makes them survive.
    /// </summary>
    object save(StorePayload p) {
        var c = container(p.StoreId);
        var local = c.Settings.LocalSettings ?? throw new Exception("This database has no local settings to save into. ");
        if (savingIsOverridden(p.StoreId, out var section)) {
            throw new Exception("Log recording is set by the " + section + " configuration section and cannot be saved here. ");
        }
        var log = logger(p.StoreId);
        local.LogRecording = log.GetRecordingSettings();
        local.MinQueryDurationMsBeforeLogging = log.MinDurationMsBeforeLogging;
        _server.UpdateWAFServerSettingsFile();
        return new {
            Saved = true,
            Logs = local.LogRecording.Length,
            Recording = local.LogRecording.Count(s => s.Log || s.Statistics),
        };
    }
    /// <summary>
    /// Puts every switch back to what the settings file holds - the other half of saving. A log the
    /// settings do not mention was never saved, so it goes off: that is what the next start would
    /// give it too, which is the whole point of the button.
    /// </summary>
    object restore(StorePayload p) {
        var c = container(p.StoreId);
        var log = c.GetLogger();
        var saved = (c.Settings.LocalSettings?.LogRecording ?? []).ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);
        var wanted = log.GetLogKeysAndNames().Select(kv => saved.TryGetValue(kv.Key, out var s)
            ? new LogRecordingSettings { Key = kv.Key, Log = s.Log, Statistics = s.Statistics }
            : new LogRecordingSettings { Key = kv.Key });
        log.ApplyRecordingSettings(wanted); // one rebuild of the log store, not one per switch
        log.MinDurationMsBeforeLogging = c.Settings.LocalSettings?.MinQueryDurationMsBeforeLogging ?? 0;
        return new { Restored = true, Recording = log.GetRecordingSettings().Count(s => s.Log || s.Statistics) };
    }

    // configuration (appsettings.json, environment variables) overriding either path would win at
    // the next start, which is exactly what saving is for - so the page says so instead of writing a
    // file that changes nothing
    bool savingIsOverridden(Guid storeId, out string section) {
        var overlay = _server.ConfigurationOverlay;
        section = overlay?.SectionName ?? "";
        if (overlay == null) return false;
        return overlay.IsOverridden(SettingsOverlay.OverridePath(storeId, "LocalSettings.LogRecording"), out _)
            || overlay.IsOverridden(SettingsOverlay.OverridePath(storeId, "LocalSettings.MinQueryDurationMsBeforeLogging"), out _);
    }

    // Queries faster than this are not recorded at all. It is the query log's only volume control:
    // everything else about a log is on or off.
    object minDuration(MinDurationPayload p) {
        var log = logger(p.StoreId);
        log.MinDurationMsBeforeLogging = Math.Max(0, p.Ms);
        return new { Ms = log.MinDurationMsBeforeLogging };
    }

    // ---- property scans ----

    object scans(Guid storeId) {
        var log = logger(storeId);
        return new {
            Recording = log.RecordingPropertyHits,
            Open = container(storeId).IsOpen(),
            Hits = log.AnalyzePropertyHits().OrderByDescending(kv => kv.Value).Select(kv => new { Name = kv.Key, Count = kv.Value }),
        };
    }
    object recordScans(ScanRecordPayload p) {
        var log = logger(p.StoreId);
        log.RecordingPropertyHits = p.Enable;
        return new { Recording = log.RecordingPropertyHits };
    }

    // Timestamps read off the log files are UTC, but one that reaches the browser without the
    // marker is read there as local time, quietly moving it by the offset
    static DateTime? asUtc(DateTime? value) {
        if (value is not DateTime v) return null;
        return v.Kind switch {
            DateTimeKind.Utc => v,
            DateTimeKind.Local => v.ToUniversalTime(),
            _ => DateTime.SpecifyKind(v, DateTimeKind.Utc),
        };
    }

    static string? utc(DateTime? value) {
        if (value is not DateTime v) return null;
        return DateTime.SpecifyKind(v, DateTimeKind.Utc).ToString("o");
    }

    sealed record StorePayload(Guid StoreId);
    sealed record LogPayload(Guid StoreId, string LogKey);
    sealed record TracePayload(Guid StoreId, int Take = 200);
    sealed record ExtractPayload(Guid StoreId, string LogKey, DateTime? FromUtc, DateTime? ToUtc, int Skip = 0, int Take = 200);
    sealed record SeriesPayload(Guid StoreId, string LogKey, string? Property, string Statistic, string Interval, DateTime? FromUtc, DateTime? ToUtc);
    sealed record EnablePayload(Guid StoreId, string LogKey, bool? Log, bool? Statistics);
    sealed record ClearPayload(Guid StoreId, string? LogKey, bool Log, bool Statistics);
    sealed record ScanRecordPayload(Guid StoreId, bool Enable);
    sealed record MinDurationPayload(Guid StoreId, int Ms);
}
