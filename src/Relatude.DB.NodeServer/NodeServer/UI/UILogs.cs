using Relatude.DB.DataStores;
using Relatude.DB.Logging;
using Relatude.DB.NodeServer.Settings;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// The system logs section of the admin UI: the activity logs of one database, what they contain,
/// and the statistics kept alongside them.
///
/// Nothing here knows what a query log or a metrics log looks like. A log describes itself through
/// its <see cref="LogSettings"/>, and that description is what the client renders, both for the
/// tables and for the graphs (see <see cref="UILogReader"/>, which reads them for the custom logs
/// too). A log added to StoreLogger therefore shows up in the UI, with its columns and its graphs,
/// without a line changing here or in the browser.
/// </summary>
sealed class UILogs {
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
                Series = UILogReader.SeriesOf(setting),
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

    // ---- reading entries ----

    object extract(ExtractPayload p) {
        var log = logger(p.StoreId);
        return UILogReader.Extract(log.LogStore, p.LogKey, p.LastMs, p.FromUtc, p.ToUtc, p.Skip, p.Take, p.Search, p.CaseSensitive);
    }

    // ---- a log as a file ----

    /// <summary>
    /// A log written out as tab separated text: the whole of it, or one range, narrowed to what a
    /// search matches when there is one. See <see cref="UILogReader.WriteEntries"/>.
    /// </summary>
    internal Task WriteTsv(HttpContext http, ExportPayload p) {
        var log = logger(p.StoreId);
        return UILogReader.WriteEntries(http, log.LogStore, p.LogKey, p.FromUtc, p.ToUtc, p.Search, p.CaseSensitive, "tsv");
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
        return UILogReader.Series(log.LogStore, p.LogKey, p.Property, p.Statistic, p.Interval, p.LastMs, p.FromUtc, p.ToUtc, log.IsStatisticsEnabled(p.LogKey));
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

    static string? utc(DateTime? value) => UILogReader.Utc(value);

    sealed record StorePayload(Guid StoreId);
    sealed record LogPayload(Guid StoreId, string LogKey);
    sealed record TracePayload(Guid StoreId, int Take = 200);
    // Search is what the search box holds, and is missing or empty when there is nothing in it:
    // every entry in the range is then listed, which is what the page shows until something is
    // typed. CaseSensitive tells upper and lower case apart, which a search does not by itself.
    sealed record ExtractPayload(Guid StoreId, string LogKey, DateTime? FromUtc, DateTime? ToUtc, int Skip = 0, int Take = 200, string? Search = null, bool CaseSensitive = false, long? LastMs = null);
    // both bounds omitted is the whole log; either one on its own bounds that end of it. A search
    // narrows the file to the entries matching it, the same ones the table is showing.
    internal sealed record ExportPayload(Guid StoreId, string LogKey, DateTime? FromUtc, DateTime? ToUtc, string? Search = null, bool CaseSensitive = false);
    sealed record SeriesPayload(Guid StoreId, string LogKey, string? Property, string Statistic, string Interval, DateTime? FromUtc, DateTime? ToUtc, long? LastMs = null);
    sealed record EnablePayload(Guid StoreId, string LogKey, bool? Log, bool? Statistics);
    sealed record ClearPayload(Guid StoreId, string? LogKey, bool Log, bool Statistics);
    sealed record ScanRecordPayload(Guid StoreId, bool Enable);
    sealed record MinDurationPayload(Guid StoreId, int Ms);
}
