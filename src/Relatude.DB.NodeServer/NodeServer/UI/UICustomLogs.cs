using Relatude.DB.Common;
using Relatude.DB.Logging;
using Relatude.DB.Logging.Statistics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// The custom logs section of the admin UI: logs a person defines for a database here, rather than
/// logs the database defines in code (<see cref="UILogs"/>). Everything about one is in the page's
/// hands - its definition, its switches, what it has recorded - and reading it goes through the same
/// <see cref="UILogReader"/> as the system logs, so a custom log draws the same graphs from the same
/// statistics.
///
/// A definition is a <see cref="LogSettings"/>, kept as a json file in the log folder by
/// <see cref="ICustomLogs"/>. The page edits it as a form, where the columns are a list in the order
/// they are shown; the file keeps them as an object keyed by column, in that same order. Changing
/// one is asked about first: <c>custom-logs-plan</c> says, in sentences, what the change would do to
/// what the log has recorded, and <c>custom-logs-save</c> does it.
///
/// The logger of a closed database reads and writes the same files, so all of this works while the
/// database is closed - a log can be defined before the application that records into it is started.
/// </summary>
sealed class UICustomLogs {
    // how many entries a distribution reads before it stops and says it did: the answer is an
    // estimate beyond that, and reading on would hold the request for the rest of a busy month
    const int defaultAnalyseEntries = 200_000;
    const int maxAnalyseEntries = 2_000_000;
    // a breakdown by value keeps this many values, the rest are counted as "other"
    const int topValues = 40;
    const int maxDistinctTracked = 100_000;
    const int histogramBins = 30;

    readonly RelatudeDBServer _server;
    internal UICustomLogs(RelatudeDBServer server) => _server = server;

    internal void Register(UICommands commands) {
        commands.Register("custom-logs-info", ctx => info(ctx.Payload<StorePayload>().StoreId));
        commands.Register("custom-logs-definition", ctx => definition(ctx.Payload<LogPayload>()));
        commands.Register("custom-logs-plan", ctx => plan(ctx.Payload<DefinitionPayload>()));
        commands.Register("custom-logs-save", ctx => save(ctx.Payload<DefinitionPayload>()));
        commands.Register("custom-logs-enable", ctx => enable(ctx.Payload<EnablePayload>()));
        commands.Register("custom-logs-delete", ctx => delete(ctx.Payload<DeletePayload>()));
        commands.Register("custom-logs-clear", ctx => clear(ctx.Payload<ClearPayload>()));
        commands.Register("custom-logs-rebuild-statistics", ctx => rebuild(ctx.Payload<LogPayload>()));
        commands.Register("custom-logs-extract", ctx => extract(ctx.Payload<ExtractPayload>()));
        commands.Register("custom-logs-series", ctx => series(ctx.Payload<SeriesPayload>()));
        commands.Register("custom-logs-analyse", ctx => analyse(ctx.Payload<AnalysePayload>(), ctx.Http.RequestAborted));
        commands.Register("custom-logs-record", ctx => record(ctx.Payload<RecordPayload>()));
        commands.Register("custom-logs-sample", ctx => sample(ctx.Payload<SamplePayload>()));
        commands.Register("custom-logs-files", ctx => files(ctx.Payload<LogPayload>()));
        commands.Register("custom-logs-flush", ctx => flush(ctx.Payload<StorePayload>().StoreId));
        commands.Register("custom-logs-reload", ctx => reload(ctx.Payload<StorePayload>().StoreId));
        commands.Register("custom-logs-broken-read", ctx => brokenRead(ctx.Payload<BrokenPayload>()));
        commands.Register("custom-logs-broken-repair", ctx => brokenRepair(ctx.Payload<BrokenPayload>()));
        commands.Register("custom-logs-broken-delete", ctx => brokenDelete(ctx.Payload<BrokenPayload>()));
    }

    NodeStoreContainer container(Guid storeId) {
        if (!_server.Containers.TryGetValue(storeId, out var c)) throw new Exception("Database not found. ");
        return c;
    }
    ICustomLogs logs(Guid storeId) => container(storeId).GetLogger().CustomLogs;

    // ---- what there is ----

    object info(Guid storeId) {
        var c = container(storeId);
        var custom = c.GetLogger().CustomLogs;
        var summaries = custom.GetDefinitions().Select(s => summary(custom, s)).ToArray();
        return new {
            Open = c.IsOpen(),
            State = c.HasFailed ? "Error" : c.Store?.State.ToString() ?? "Closed",
            // the provider the log folder is in, for downloading a log's files as they are
            IoId = logIoId(c),
            ReservedKeys = custom.ReservedKeys.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            TotalBytes = summaries.Sum(s => s.TotalBytes),
            LoadErrors = custom.LoadErrors.Select(e => new { e.FileKey, e.Message }).ToArray(),
            Logs = summaries,
        };
    }
    static Guid? logIoId(NodeStoreContainer c) {
        var s = c.Settings;
        if (s.IoLog is Guid log && log != Guid.Empty) return log;
        if (s.IoDatabase is Guid database && database != Guid.Empty) return database;
        return null;
    }

    sealed record LogSummary(
        string Key, string Name, string Description,
        bool EnabledLog, bool EnabledStatistics, bool EnabledText, bool Compressed,
        string FileInterval, int MaxAgeInDays, int MaxSizeInMb, int ResolutionRowStats, string FirstDayOfWeek,
        string? FirstRecordUtc, string? LastRecordUtc, long LogBytes, long StatisticsBytes, long TotalBytes,
        int? EntriesLastDay, int[]? Activity, object[] Columns, object[] Series);

    static LogSummary summary(ICustomLogs custom, LogSettings s) {
        var store = custom.LogStore;
        int? lastDay = null;
        int[]? activity = null;
        if (s.EnableStatistics) {
            // the row statistic answers both without reading a single entry: the entries of the last
            // day, and how they were spread over its hours - the line the overview draws for a log
            var now = DateTime.UtcNow;
            var from = IntervalUtils.Floor(now, IntervalType.Hour, s.FirstDayOfWeek).AddHours(-23);
            activity = [.. store.AnalyseRows(s.Key, IntervalType.Hour, from, now, false, true).Select(i => i.HasValue ? i.Value : 0)];
            lastDay = activity.Sum();
        }
        var logBytes = store.GetLogFileSize(s.Key);
        var statisticsBytes = store.GetStatisticsFileSize(s.Key);
        return new LogSummary(
            s.Key, s.Name, s.Description,
            s.EnableLog, s.EnableStatistics, s.EnableLogTextFormat, s.Compressed,
            s.FileInterval.ToString(), s.MaxAgeOfLogFilesInDays, s.MaxTotalSizeOfLogFilesInMb, s.ResolutionRowStats, s.FirstDayOfWeek.ToString(),
            UILogReader.Utc(store.GetTimestampOfFirstRecord(s.Key)), UILogReader.Utc(store.GetTimestampOfLastRecord(s.Key)),
            logBytes, statisticsBytes, logBytes + statisticsBytes,
            lastDay,
            activity,
            [.. s.Properties.Select(p => (object)new {
                Key = p.Key,
                Name = string.IsNullOrWhiteSpace(p.Value.Name) ? p.Key : p.Value.Name,
                DataType = p.Value.DataType.ToString(),
            })],
            UILogReader.SeriesOf(s));
    }

    // ---- definitions ----

    object definition(LogPayload p) {
        var custom = logs(p.StoreId);
        var s = custom.GetDefinition(p.LogKey) ?? throw new Exception("There is no log with the key '" + p.LogKey + "'. ");
        return new { Settings = dto(s), Json = s.ToJson() };
    }

    /// <summary>A definition the way the page edits it: the columns as a list, in their order.</summary>
    static object dto(LogSettings s) => new {
        s.Key,
        s.Name,
        s.Description,
        s.EnableLog,
        s.EnableStatistics,
        s.EnableLogTextFormat,
        FileInterval = s.FileInterval.ToString(),
        s.Compressed,
        s.MaxAgeOfLogFilesInDays,
        s.MaxTotalSizeOfLogFilesInMb,
        s.ResolutionRowStats,
        FirstDayOfWeek = s.FirstDayOfWeek.ToString(),
        Properties = s.Properties.Select(p => new {
            Key = p.Key,
            p.Value.Name,
            DataType = p.Value.DataType.ToString(),
            Statistics = p.Value.Statistics.Select(x => new { StatisticsType = x.StatisticsType.ToString(), x.Resolution }),
        }),
    };

    /// <summary>
    /// The settings a payload holds: either the page's form (the columns as a list) or the json of a
    /// settings file, as it was typed or uploaded. Both end in <see cref="LogSettings.FromJson"/>, which
    /// says what is wrong with them in a way that can be shown as it is.
    /// </summary>
    static LogSettings settingsOf(DefinitionPayload p) {
        if (!string.IsNullOrWhiteSpace(p.Json)) return LogSettings.FromJson(p.Json);
        if (p.Settings is not JsonElement form || form.ValueKind != JsonValueKind.Object) throw new ArgumentException("No settings were sent.");
        var node = JsonNode.Parse(form.GetRawText())!.AsObject();
        if (node["properties"] is JsonArray list) {
            // the file keeps the columns as an object keyed by column, in the order the list has them
            var columns = new JsonObject();
            foreach (var item in list) {
                if (item is not JsonObject column) continue;
                var key = column["key"]?.GetValue<string>()?.Trim() ?? string.Empty;
                if (key.Length == 0) throw new ArgumentException("Every column needs a key.");
                if (columns.ContainsKey(key)) throw new ArgumentException($"The column '{key}' is there twice.");
                var copy = (JsonObject)column.DeepClone();
                copy.Remove("key");
                columns[key] = copy;
            }
            node["properties"] = columns;
        }
        return LogSettings.FromJson(node.ToJsonString());
    }

    /// <summary>
    /// What saving would do, without doing it: whether the settings are usable at all, and - for a
    /// log that exists - what the change does to what it has recorded. An error is an answer here,
    /// not a failure, so the page can say it next to the form while it is being filled in.
    /// </summary>
    object plan(DefinitionPayload p) {
        var custom = logs(p.StoreId);
        LogSettings settings;
        try {
            settings = settingsOf(p);
        } catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException or FormatException) {
            return new { Valid = false, Error = error.Message, Changed = false, Notes = Array.Empty<string>() };
        }
        if (p.IsNew) {
            var problem = custom.CheckNewKey(settings.Key);
            return new { Valid = problem == null, Error = problem, Changed = true, Notes = Array.Empty<string>() };
        }
        if (!custom.HasLog(settings.Key)) {
            return new { Valid = false, Error = (string?)("There is no log with the key '" + settings.Key + "'. The key of a log cannot be changed."), Changed = false, Notes = Array.Empty<string>() };
        }
        var changes = custom.PlanUpdate(settings);
        return new {
            Valid = true,
            Error = (string?)null,
            changes.Changed,
            changes.MovesEntries,
            changes.DiscardsStatistics,
            changes.CanRebuildStatistics,
            Notes = changes.Notes.ToArray(),
        };
    }

    object save(DefinitionPayload p) {
        var custom = logs(p.StoreId);
        var settings = settingsOf(p);
        if (p.IsNew) {
            custom.Create(settings);
            return new { Created = true, Key = settings.Key, Changed = true, EntriesMoved = 0, StatisticsRebuilt = false, Notes = Array.Empty<string>() };
        }
        var changes = custom.Update(settings, p.RebuildStatistics);
        return new {
            Created = false,
            Key = settings.Key,
            changes.Changed,
            changes.EntriesMoved,
            changes.StatisticsRebuilt,
            Notes = changes.Notes.ToArray(),
        };
    }

    object enable(EnablePayload p) {
        var custom = logs(p.StoreId);
        custom.SetEnabled(p.LogKey, p.Log, p.Statistics);
        var s = custom.GetDefinition(p.LogKey)!;
        return new { Log = s.EnableLog, Statistics = s.EnableStatistics };
    }

    object delete(DeletePayload p) {
        logs(p.StoreId).Delete(p.LogKey, p.DeleteRecorded);
        return new { Deleted = true };
    }

    // Entries go by whole files: a log is kept as a file per minute, hour, day or month, and a file
    // is deleted once the whole of it is older than the moment given - never half of one.
    object clear(ClearPayload p) {
        var custom = logs(p.StoreId);
        var store = custom.LogStore;
        if (!custom.HasLog(p.LogKey)) throw new Exception("There is no log with the key '" + p.LogKey + "'. ");
        if (p.Entries) {
            if (UILogReader.AsUtc(p.OlderThanUtc) is DateTime before) store.DeleteLogOlderThan(p.LogKey, before);
            else store.DeleteLog(p.LogKey);
        }
        if (p.Statistics) store.DeleteStatistics(p.LogKey);
        return new { Cleared = true, LogBytes = store.GetLogFileSize(p.LogKey), StatisticsBytes = store.GetStatisticsFileSize(p.LogKey) };
    }

    object rebuild(LogPayload p) {
        var custom = logs(p.StoreId);
        if (!custom.IsEnabled(p.LogKey) || custom.GetDefinition(p.LogKey)?.EnableStatistics != true) throw new Exception("Turn statistics on before rebuilding them. ");
        custom.LogStore.RebuildStatistics(p.LogKey);
        return new { Rebuilt = true };
    }

    object files(LogPayload p) {
        var c = container(p.StoreId);
        var custom = c.GetLogger().CustomLogs;
        return new {
            IoId = logIoId(c),
            Files = custom.GetFiles(p.LogKey).Select(f => new { f.FileKey, f.Kind, f.Size }).ToArray(),
        };
    }

    object flush(Guid storeId) {
        logs(storeId).FlushToDiskNow();
        return new { Flushed = true };
    }
    object reload(Guid storeId) {
        var custom = logs(storeId);
        custom.Reload();
        return new { Reloaded = true, Logs = custom.GetDefinitions().Count, Errors = custom.LoadErrors.Count };
    }
    object brokenRead(BrokenPayload p) => new { Text = logs(p.StoreId).ReadBrokenDefinition(p.FileKey) };
    object brokenRepair(BrokenPayload p) {
        logs(p.StoreId).RepairBrokenDefinition(p.FileKey, p.Json ?? string.Empty);
        return new { Repaired = true };
    }
    object brokenDelete(BrokenPayload p) {
        logs(p.StoreId).DeleteBrokenDefinition(p.FileKey);
        return new { Deleted = true };
    }

    // ---- reading ----

    object extract(ExtractPayload p) {
        var custom = logs(p.StoreId);
        return UILogReader.Extract(custom.LogStore, p.LogKey, p.LastMs, p.FromUtc, p.ToUtc, p.Skip, p.Take, p.Search, p.CaseSensitive, p.NewestFirst);
    }

    object series(SeriesPayload p) {
        var custom = logs(p.StoreId);
        var enabled = custom.GetDefinition(p.LogKey)?.EnableStatistics ?? false;
        return UILogReader.Series(custom.LogStore, p.LogKey, p.Property, p.Statistic, p.Interval, p.LastMs, p.FromUtc, p.ToUtc, enabled);
    }

    internal Task WriteExport(HttpContext http, ExportPayload p) {
        var custom = logs(p.StoreId);
        return UILogReader.WriteEntries(http, custom.LogStore, p.LogKey, p.FromUtc, p.ToUtc, p.Search, p.CaseSensitive, p.Format);
    }

    /// <summary>
    /// How one column's values are spread over a range, read from the entries themselves: what the
    /// statistics cannot say. They keep a sum and an average per interval, and nothing about the
    /// middle of the values or their tail - which is what a duration is usually asked about (the
    /// slowest one in a hundred) - and nothing at all about a column that declares no statistics.
    ///
    /// It costs what a search costs: the range is read, and every entry in it tested. So it stops
    /// after <see cref="AnalysePayload.MaxEntries"/> and says so, and the page asks for it with a
    /// button rather than on every change of the range.
    ///
    /// Numbers (and durations, as milliseconds, and moments, as milliseconds since 1970) get their
    /// percentiles and a histogram; text gets its most common values and how many distinct ones there
    /// were; bytes are measured by their length.
    /// </summary>
    object analyse(AnalysePayload p, CancellationToken cancel) {
        var custom = logs(p.StoreId);
        var store = custom.LogStore;
        var setting = store.GetSetting(p.LogKey);
        var type = setting.Properties.TryGetValue(p.Property, out var column) ? column.DataType : LogDataType.String;
        var (fromBound, toBound) = UILogReader.Window(p.LastMs, p.FromUtc, p.ToUtc);
        var first = UILogReader.AsUtc(store.GetTimestampOfFirstRecord(p.LogKey));
        var last = UILogReader.AsUtc(store.GetTimestampOfLastRecord(p.LogKey));
        var cap = Math.Clamp(p.MaxEntries <= 0 ? defaultAnalyseEntries : p.MaxEntries, 1, maxAnalyseEntries);
        var search = LogSearch.Parse(p.Search, p.CaseSensitive);
        var numeric = type is LogDataType.Integer or LogDataType.Double or LogDataType.TimeSpan or LogDataType.DateTime or LogDataType.Bytes;
        var numbers = new List<double>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var overflow = 0; // values past the distinct ones tracked
        int scanned = 0, matched = 0, withValue = 0;
        var truncated = false;
        DateTime? firstSeen = null, lastSeen = null;
        if (first is DateTime f && last is DateTime l) {
            var rangeFrom = fromBound is DateTime a && a > f ? a : f;
            var rangeTo = toBound is DateTime b && b <= l ? b : l.AddTicks(1);
            // newest first: a capped read of a range is about what happened lately, not about how it began
            var slice = rangeTo;
            while (slice > rangeFrom && !truncated) {
                cancel.ThrowIfCancellationRequested();
                var sliceFrom = slice.AddDays(-1) < rangeFrom ? rangeFrom : slice.AddDays(-1);
                foreach (var entry in store.ExtractLog(p.LogKey, sliceFrom, slice, 0, int.MaxValue, true, out _)) {
                    if (scanned >= cap) {
                        truncated = true;
                        break;
                    }
                    scanned++;
                    if (!search.IsEmpty && !search.Matches(entry, setting)) continue;
                    matched++;
                    firstSeen = firstSeen == null || entry.Timestamp < firstSeen ? entry.Timestamp : firstSeen;
                    lastSeen = lastSeen == null || entry.Timestamp > lastSeen ? entry.Timestamp : lastSeen;
                    if (!entry.Values.TryGetValue(p.Property, out var value) || value == null) continue;
                    withValue++;
                    if (numeric && asNumber(value, type) is double n) {
                        numbers.Add(n);
                    } else if (!numeric) {
                        var text = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                        if (counts.TryGetValue(text, out var count)) counts[text] = count + 1;
                        else if (counts.Count < maxDistinctTracked) counts[text] = 1;
                        else overflow++;
                    }
                }
                slice = sliceFrom;
            }
        }
        object? distribution = null;
        object? breakdown = null;
        if (numeric && numbers.Count > 0) {
            numbers.Sort();
            var sum = 0d;
            foreach (var n in numbers) sum += n;
            var mean = sum / numbers.Count;
            var variance = 0d;
            foreach (var n in numbers) variance += (n - mean) * (n - mean);
            var stdDev = Math.Sqrt(variance / numbers.Count);
            double[] ranks = [1, 5, 10, 25, 50, 75, 90, 95, 99, 99.9];
            distribution = new {
                Count = numbers.Count,
                Min = numbers[0],
                Max = numbers[^1],
                Mean = mean,
                Sum = sum,
                StdDev = stdDev,
                Percentiles = ranks.Select(r => new { P = r, Value = percentile(numbers, r) }).ToArray(),
                Histogram = histogram(numbers, type == LogDataType.Integer || type == LogDataType.Bytes),
            };
        } else if (!numeric) {
            var total = counts.Values.Sum() + overflow;
            var top = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(topValues).ToArray();
            breakdown = new {
                Distinct = counts.Count + (overflow > 0 ? 1 : 0),
                DistinctCapped = overflow > 0,
                Total = total,
                Top = top.Select(kv => new { Value = kv.Key, Count = kv.Value }).ToArray(),
                Other = total - top.Sum(kv => kv.Value),
            };
        }
        return new {
            p.LogKey,
            p.Property,
            DataType = type.ToString(),
            Scanned = scanned,
            Matched = matched,
            WithValue = withValue,
            Truncated = truncated,
            Cap = cap,
            FirstUtc = UILogReader.Utc(firstSeen),
            LastUtc = UILogReader.Utc(lastSeen),
            Distribution = distribution,
            Breakdown = breakdown,
        };
    }
    // a duration is its milliseconds, a moment its milliseconds since 1970, and bytes their length:
    // each of them is then a number like any other, and the page formats it back
    static double? asNumber(object value, LogDataType type) => value switch {
        int i => i,
        double d => double.IsFinite(d) ? d : null,
        TimeSpan ts => ts.TotalMilliseconds,
        DateTime dt => (DateTime.SpecifyKind(dt, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalMilliseconds,
        byte[] bytes => bytes.Length,
        _ => LogValues.TryConvert(value, type == LogDataType.Bytes ? LogDataType.Integer : type, out var converted) && converted is not string ? asNumber(converted, type) : null,
    };
    // the value below which the given share of the values fall, interpolated between the two either side
    static double percentile(List<double> sorted, double p) {
        if (sorted.Count == 1) return sorted[0];
        var rank = p / 100d * (sorted.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = Math.Min(sorted.Count - 1, lower + 1);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (rank - lower);
    }
    // Bins of equal width over the values. Whole numbers spanning fewer values than there are bins
    // get one bin per value, since a bin from 2.4 to 3.1 says nothing about a count of three.
    static object[] histogram(List<double> sorted, bool whole) {
        var min = sorted[0];
        var max = sorted[^1];
        if (max <= min) return [new { From = min, To = max, Count = sorted.Count }];
        var bins = histogramBins;
        var width = (max - min) / bins;
        if (whole && max - min + 1 <= bins) {
            bins = (int)(max - min + 1);
            width = 1;
        }
        var counts = new int[bins];
        foreach (var n in sorted) {
            var index = whole && width == 1 ? (int)(n - min) : (int)((n - min) / width);
            counts[Math.Clamp(index, 0, bins - 1)]++;
        }
        return [.. Enumerable.Range(0, bins).Select(i => (object)new {
            From = min + i * width,
            To = whole && width == 1 ? min + i : min + (i + 1) * width,
            Count = counts[i],
        })];
    }

    // ---- writing ----

    /// <summary>
    /// One entry, as the page's form wrote it. The values arrive as json - text from a field, a
    /// number, a switch - and the log converts each to its column's type, leaving out what does not
    /// convert, exactly as it does for an application recording into it.
    /// </summary>
    object record(RecordPayload p) {
        var custom = logs(p.StoreId);
        if (!custom.HasLog(p.LogKey)) throw new Exception("There is no log with the key '" + p.LogKey + "'. ");
        if (!custom.IsEnabled(p.LogKey)) throw new Exception("The log records nothing while both recording and statistics are off. ");
        var entry = new LogEntry { Timestamp = UILogReader.AsUtc(p.TimestampUtc) ?? DateTime.UtcNow };
        foreach (var (key, element) in p.Values ?? []) {
            object? value = element.ValueKind switch {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt32(out var i) ? i : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
            if (value is string text && text.Length == 0) continue; // an empty field is no value
            if (value != null) entry.Values[key] = value;
        }
        custom.Record(p.LogKey, entry, flushToDisk: true);
        return new { Recorded = true, TimestampUtc = UILogReader.Utc(entry.Timestamp) };
    }

    /// <summary>
    /// Entries made up for trying a log out: plausible values for every column, spread over a stretch
    /// of time that ends now, with the rhythm of a day in them so the graphs have something to show.
    /// Text columns draw from a handful of values, most of them rare, which is what a breakdown by
    /// value is for. The statistics are rebuilt afterwards, so entries placed in the past are counted
    /// where they belong even where the log already had newer ones.
    /// </summary>
    object sample(SamplePayload p) {
        var custom = logs(p.StoreId);
        var store = custom.LogStore;
        var setting = custom.GetDefinition(p.LogKey) ?? throw new Exception("There is no log with the key '" + p.LogKey + "'. ");
        if (!setting.EnableLog && !setting.EnableStatistics) throw new Exception("The log records nothing while both recording and statistics are off. ");
        var count = Math.Clamp(p.Count, 1, 1_000_000);
        var spanMs = Math.Clamp(p.SpanMs, 1_000L, 90L * 86_400_000);
        var random = new Random();
        var end = DateTime.UtcNow;
        var start = end.AddMilliseconds(-spanMs);
        var times = new List<DateTime>(count);
        while (times.Count < count) {
            var t = start.AddMilliseconds(random.NextDouble() * spanMs);
            // busier by day than by night: keep a moment with the chance the hour of it gives
            var dayShape = 0.35 + 0.65 * Math.Pow(Math.Sin(Math.PI * (t.Hour + t.Minute / 60d) / 24d), 2);
            if (random.NextDouble() <= dayShape) times.Add(t);
        }
        times.Sort();
        var makers = setting.Properties.ToDictionary(kv => kv.Key, kv => sampleValue(kv.Key, kv.Value.DataType, start, end));
        // Written entries are counted once, by the rebuild at the end, rather than as they are
        // written as well: a hundred thousand of them would otherwise be aggregated twice. A log that
        // only keeps statistics has nothing to rebuild from, so there they are counted as they come.
        var rebuild = setting.EnableStatistics && setting.EnableLog;
        bool? countNow = rebuild ? false : null;
        foreach (var t in times) {
            var entry = new LogEntry { Timestamp = t };
            foreach (var (key, make) in makers) {
                if (random.NextDouble() < 0.03) continue; // now and then a value is missing, as they are
                entry.Values[key] = make(random, t);
            }
            store.Record(p.LogKey, entry, false, null, countNow);
        }
        store.FlushToDiskNow(p.LogKey);
        if (rebuild) store.RebuildStatistics(p.LogKey);
        var rebuilt = rebuild;
        return new { Recorded = count, Rebuilt = rebuilt, Written = setting.EnableLog };
    }
    static readonly string[] sampleWords = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "kilo", "lima"];
    // what columns with these names usually hold, most common first: a trial log reads like a real one
    static readonly (string Hint, string[] Values)[] sampleVocabularies = [
        ("method", ["GET", "POST", "PUT", "DELETE", "PATCH"]),
        ("path", ["/", "/products", "/api/search", "/cart", "/login", "/products/42", "/api/orders", "/checkout", "/about", "/favicon.ico"]),
        ("url", ["/", "/products", "/api/search", "/cart", "/login", "/checkout"]),
        ("level", ["Info", "Warning", "Error", "Critical"]),
        ("outcome", ["Success", "Failed", "Cancelled"]),
        ("success", ["Success", "Error"]),
        ("result", ["Success", "Failed", "Skipped"]),
        ("event", ["page-view", "sign-up", "order", "refund", "newsletter"]),
        ("source", ["web", "api", "scheduler", "import", "mail"]),
        ("job", ["nightly-import", "reindex", "send-mail", "cleanup", "thumbnails"]),
        ("channel", ["web", "mobile", "store", "phone"]),
    ];
    static readonly int[] sampleStatuses = [200, 200, 200, 200, 200, 200, 304, 201, 204, 301, 302, 404, 400, 401, 403, 500, 503];
    static Func<Random, DateTime, object> sampleValue(string key, LogDataType type, DateTime start, DateTime end) {
        // the column's name decides its scale, so two columns of one log do not look alike
        var seed = (int)(key.XXH64Hash() % 1000);
        var scale = new[] { 1d, 10d, 50d, 100d, 250d, 1000d }[seed % 6];
        bool named(params string[] hints) => hints.Any(h => key.Contains(h, StringComparison.OrdinalIgnoreCase));
        // a few common values and a tail of rare ones: the shape most columns of words have
        static T skewed<T>(Random r, T[] values) => values[Math.Min(values.Length - 1, (int)Math.Floor(Math.Pow(r.NextDouble(), 2.2) * values.Length))];
        if (type == LogDataType.Integer && named("status", "code")) return (r, t) => sampleStatuses[r.Next(sampleStatuses.Length)];
        if (type is LogDataType.Integer or LogDataType.Double) {
            // "ms" alone would take "items" for a duration
            if (named("duration", "elapsed", "latency")) scale = 40;
            else if (named("bytes", "size", "length")) scale = 20_000;
            else if (named("amount", "price", "total", "sum", "value")) scale = 250;
            else if (named("count", "items", "quantity", "qty")) scale = 3;
        }
        if (type == LogDataType.String) {
            foreach (var (hint, values) in sampleVocabularies) {
                if (named(hint)) return (r, t) => skewed(r, values);
            }
        }
        switch (type) {
            case LogDataType.Integer:
                return (r, t) => (int)Math.Round(logNormal(r, scale, 0.6) * dayWave(t));
            case LogDataType.Double:
                return (r, t) => Math.Round(logNormal(r, scale, 0.5) * dayWave(t), 2);
            case LogDataType.TimeSpan:
                return (r, t) => TimeSpan.FromMilliseconds(Math.Round(logNormal(r, scale, 0.8), 1));
            case LogDataType.DateTime:
                return (r, t) => start.AddMilliseconds(r.NextDouble() * (end - start).TotalMilliseconds);
            case LogDataType.Bytes:
                return (r, t) => {
                    var bytes = new byte[8 + r.Next(56)];
                    r.NextBytes(bytes);
                    return bytes;
                };
            default: {
                    if (named("user", "customer", "client", "account")) return (r, t) => "user-" + (1 + (int)Math.Floor(Math.Pow(r.NextDouble(), 2) * 200));
                    if (named("message", "text", "details", "description")) {
                        return (r, t) => skewed(r, ["Could not reach the payment provider", "Timed out waiting for the index", "Saved", "User signed in", "Cache rebuilt", "Import finished"]);
                    }
                    var words = sampleWords.Skip(seed % 4).Take(3 + seed % 6).ToArray();
                    return (r, t) => skewed(r, words);
                }
        }
    }
    static double logNormal(Random r, double median, double sigma) {
        // Box-Muller: a normal draw, and the log-normal around the median from it
        var u1 = 1 - r.NextDouble();
        var u2 = r.NextDouble();
        var normal = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        return median * Math.Exp(sigma * normal);
    }
    static double dayWave(DateTime t) => 0.7 + 0.6 * Math.Pow(Math.Sin(Math.PI * (t.Hour + t.Minute / 60d) / 24d), 2);

    sealed record StorePayload(Guid StoreId);
    sealed record LogPayload(Guid StoreId, string LogKey);
    // Settings is the page's form (the columns as a list), Json the text of a settings file; one of
    // the two. IsNew says the key is to be a new log's rather than an existing one's, and
    // RebuildStatistics aggregates the statistics again from the entries once the change is saved.
    sealed record DefinitionPayload(Guid StoreId, JsonElement? Settings, string? Json, bool IsNew = false, bool RebuildStatistics = false);
    sealed record EnablePayload(Guid StoreId, string LogKey, bool? Log, bool? Statistics);
    sealed record DeletePayload(Guid StoreId, string LogKey, bool DeleteRecorded);
    // OlderThanUtc narrows deleting entries to the files entirely older than it; without it every entry goes
    sealed record ClearPayload(Guid StoreId, string LogKey, bool Entries, bool Statistics, DateTime? OlderThanUtc = null);
    sealed record ExtractPayload(Guid StoreId, string LogKey, DateTime? FromUtc, DateTime? ToUtc, int Skip = 0, int Take = 100, string? Search = null, bool CaseSensitive = false, long? LastMs = null, bool NewestFirst = true);
    // Format: "tsv", "csv" or "jsonl"
    internal sealed record ExportPayload(Guid StoreId, string LogKey, DateTime? FromUtc, DateTime? ToUtc, string? Search = null, bool CaseSensitive = false, string Format = "tsv");
    sealed record SeriesPayload(Guid StoreId, string LogKey, string? Property, string Statistic, string Interval, DateTime? FromUtc, DateTime? ToUtc, long? LastMs = null);
    sealed record AnalysePayload(Guid StoreId, string LogKey, string Property, DateTime? FromUtc, DateTime? ToUtc, long? LastMs = null, string? Search = null, bool CaseSensitive = false, int MaxEntries = 0);
    sealed record RecordPayload(Guid StoreId, string LogKey, Dictionary<string, JsonElement>? Values, DateTime? TimestampUtc = null);
    sealed record SamplePayload(Guid StoreId, string LogKey, int Count = 500, long SpanMs = 86_400_000);
    sealed record BrokenPayload(Guid StoreId, string FileKey, string? Json = null);
}
