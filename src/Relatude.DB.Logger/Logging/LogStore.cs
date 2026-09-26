using Relatude.DB.IO;
using Relatude.DB.Logging.Statistics;

namespace Relatude.DB.Logging;
public class LogStore : IDisposable, ILogStore {
    // volatile + copy on write in AddLog, so lock free lookups are safe while logs are added
    volatile Dictionary<string, Log> _logs;
    readonly object _addLock = new();
    readonly IIOProvider _io;
    public LogStore(IIOProvider io, IEnumerable<LogSettings> logSettings) {
        _io = io;
        _logs = logSettings.ToDictionary(s => s.Key, s => new Log(s, _io), StringComparer.OrdinalIgnoreCase);
    }
    Log? get(string logKey) => _logs.TryGetValue(logKey, out var log) ? log : null;
    public bool Record(string logKey, LogEntry entry, bool flushToDisk = false, bool? forceLogging = null, bool? forceStatistics = null) {
        if (get(logKey) is not { } log) return false;
        log.Record(entry, flushToDisk, forceLogging, forceStatistics);
        return true;
    }
    public IEnumerable<LogEntry> ExtractLog(string logKey, DateTime fromAndIncluding, DateTime upUntil, int skip, int take, bool orderByDescendingDates, out int total) {
        if (get(logKey) is { } log) return log.Extract(fromAndIncluding, upUntil, skip, take, orderByDescendingDates, out total);
        total = 0;
        return [];
    }
    public IEnumerable<LogEntry> SearchLog(string logKey, LogSearch search, DateTime fromAndIncluding, DateTime upUntil, int skip, int take, bool orderByDescendingDates, out int total) {
        if (get(logKey) is { } log) return log.Search(search, fromAndIncluding, upUntil, skip, take, orderByDescendingDates, out total);
        total = 0;
        return [];
    }
    public IEnumerable<LogEntry> SearchLog(string logKey, string search, DateTime fromAndIncluding, DateTime upUntil, int skip, int take, bool orderByDescendingDates, out int total)
        => SearchLog(logKey, LogSearch.Parse(search), fromAndIncluding, upUntil, skip, take, orderByDescendingDates, out total);
    public long GetFileSize(string logKey) => get(logKey)?.GetTotalFileSize() ?? 0;
    public long GetLogFileSize(string logKey) => get(logKey)?.GetLogFileSize() ?? 0;
    public long GetStatisticsFileSize(string logKey) => get(logKey)?.GetStatisticsFileSize() ?? 0;
    public void DeleteLogOlderThan(string logKey, DateTime to) => get(logKey)?.EnforceDateLimit(to);
    public void DeleteLog(string logKey) => get(logKey)?.EnforceDateLimit(DateTime.MaxValue);
    public void DeleteStatistics(string logKey) => get(logKey)?.DeleteStatistics();
    public void RebuildStatistics(string logKey) => get(logKey)?.RebuildStatistics();
    public void DeleteLogAndStatistics(string logKey) => get(logKey)?.DeleteAll();
    public void DeleteAll() {
        foreach (var log in _logs.Values) log.DeleteAll();
    }
    public void SaveStatsAndDeleteExpiredData() {
        SaveStatistics();
        EnforceLimits();
    }
    public void SaveStatistics() {
        foreach (var log in _logs.Values) log.SaveStatisticsState();
    }
    public void FlushToDiskNow(string logKey) => get(logKey)?.FlushToDiskNow();
    public void FlushToDiskNow() {
        foreach (var log in _logs.Values) log.FlushToDiskNow();
    }
    public void EnforceLimits() {
        foreach (var log in _logs.Values) {
            if (log.Setting.MaxAgeOfLogFilesInDays > 0)
                log.EnforceDateLimit(DateTime.UtcNow.AddDays(-log.Setting.MaxAgeOfLogFilesInDays));
            if (log.Setting.MaxTotalSizeOfLogFilesInMb > 0)
                log.EnforceSizeLimit(log.Setting.MaxTotalSizeOfLogFilesInMb);
        }
    }
    public bool IsEnabled(string logKey) => get(logKey)?.Setting.IsEnabled() ?? false;
    public bool HasLog(string logKey) => _logs.ContainsKey(logKey);
    public void AddLog(LogSettings settings) {
        lock (_addLock) {
            if (_logs.ContainsKey(settings.Key)) throw new ArgumentException($"There is already a log with the key '{settings.Key}'.");
            var logs = new Dictionary<string, Log>(_logs, StringComparer.OrdinalIgnoreCase);
            logs.Add(settings.Key, new Log(settings, _io));
            _logs = logs;
        }
    }
    /// <summary>
    /// Takes a log out of the store, saving its statistics and closing its files first. What it
    /// wrote stays on disk: removing a log is not deleting it (see <see cref="DeleteLogAndStatistics"/>).
    /// Returns false when there was no such log.
    /// </summary>
    public bool RemoveLog(string logKey) {
        Log? removed;
        lock (_addLock) {
            if (!_logs.TryGetValue(logKey, out removed)) return false;
            var logs = new Dictionary<string, Log>(_logs, StringComparer.OrdinalIgnoreCase);
            logs.Remove(logKey);
            _logs = logs;
        }
        // out of the dictionary before it is closed, so no new record is handed to it; one already
        // on its way is dropped by the log itself once it is disposed
        removed.Dispose();
        return true;
    }
    /// <summary>
    /// Puts a log back with new settings, or adds it when there was none: the old one saves its
    /// statistics and closes its files before the new one opens them, so the two never have the
    /// same files open at once.
    /// </summary>
    public void ReplaceLog(LogSettings settings) {
        lock (_addLock) {
            if (_logs.TryGetValue(settings.Key, out var old)) old.Dispose();
            var logs = new Dictionary<string, Log>(_logs, StringComparer.OrdinalIgnoreCase);
            logs[settings.Key] = new Log(settings, _io);
            _logs = logs;
        }
    }
    /// <summary>A store with every log whose settings are saved in the log folder (see <see cref="SaveSettings"/>).</summary>
    public static LogStore FromSavedSettings(IIOProvider io) => new(io, LogSettings.LoadAll(io));
    public LogSettings AddLogFromJson(string json) {
        var settings = LogSettings.FromJson(json);
        AddLog(settings);
        return settings;
    }
    public LogSettings AddLogFromFile(string filePath) {
        var settings = LogSettings.LoadFromFile(filePath);
        AddLog(settings);
        return settings;
    }
    public LogSettings AddLogFromSavedSettings(string logKey) {
        var settings = LogSettings.LoadIfSaved(_io, logKey) ?? throw new Exception($"No saved settings for log '{logKey}'.");
        AddLog(settings);
        return settings;
    }
    public string GetSettingJson(string logKey) => GetSetting(logKey).ToJson();
    public void SaveSettings(string logKey) => GetSetting(logKey).Save(_io);
    public void SaveSettingsToFile(string logKey, string filePath) => GetSetting(logKey).SaveToFile(filePath);
    public void SaveAllSettings() {
        foreach (var log in _logs.Values) log.Setting.Save(_io);
    }
    public bool HasSavedSettings(string logKey) => _io.ExistsAndIsNotEmpty(FileKeyUtility.Logger_GetSettings(logKey));
    public void DeleteSavedSettings(string logKey) => LogSettings.DeleteSaved(_io, logKey);
    public LogSettings GetSetting(string logKey) {
        return get(logKey)?.Setting ?? throw new Exception($"Log with key '{logKey}' not found");
    }
    public IEnumerable<LogSettings> GetSettings() {
        return _logs.Values.Select(l => l.Setting).ToList();
    }
    public DateTime? GetTimestampOfFirstRecord(string logKey) => get(logKey)?.GetTimestampOfFirstRecord();
    public DateTime? GetTimestampOfLastRecord(string logKey) => get(logKey)?.GetTimestampOfLastRecord();
    public IDictionary<string, List<StatisticsInfo>> GetAvailableStatisticsByProperty(string logKey) {
        return get(logKey)?.GetAvailableStatisticsByProperty() ?? new Dictionary<string, List<StatisticsInfo>>();
    }
    public IEnumerable<Interval<int>> AnalyseRows(string logKey, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        return get(logKey)?.AnalyseRows(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated) ?? [];
    }
    public IEnumerable<Interval<int>> AnalyseCounts(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        return get(logKey)?.AnalyseCounts(property, intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated) ?? [];
    }
    public IEnumerable<Interval<int>> AnalyseIntegerSums(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        return get(logKey)?.AnalyseIntegerSums(property, intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated) ?? [];
    }
    public IEnumerable<Interval<double>> AnalyseFloatSums(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        return get(logKey)?.AnalyseDoubleSums(property, intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated) ?? [];
    }
    public IEnumerable<Interval<AvgMinMax<double>>> AnalyseAvgMinMax(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        return get(logKey)?.AnalyseAvgMinMax(property, intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated) ?? [];
    }
    public IEnumerable<Interval<CountSumAvgMinMax<double>>> AnalyseCountSumAvgMinMax(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        return get(logKey)?.AnalyseCountSumAvgMinMax(property, intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated) ?? [];
    }
    public IEnumerable<Interval<Dictionary<string, int>>> AnalyseGroupCounts(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        return get(logKey)?.AnalyseGroupCounts(property, intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated) ?? [];
    }
    public IEnumerable<Interval<int>> AnalyseUniqueCounts(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        return get(logKey)?.AnalyseUniqueCounts(property, intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated) ?? [];
    }
    public IEnumerable<Interval<int>> AnalyseEstimatedUniqueCounts(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        return get(logKey)?.AnalyseEstimatedUniqueCounts(property, intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated) ?? [];
    }
    public Interval<int> AnalyseCombinedRows(string logKey, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        return get(logKey)?.AnalyseCombinedRows(intervalType, fromUtc, toUtc) ?? new(fromUtc, toUtc);
    }
    public Interval<int> AnalyseCombinedCounts(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        return get(logKey)?.AnalyseCombinedCounts(property, intervalType, fromUtc, toUtc) ?? new(fromUtc, toUtc);
    }
    public Interval<int> AnalyseCombinedIntegerSums(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        return get(logKey)?.AnalyseCombinedIntegerSums(property, intervalType, fromUtc, toUtc) ?? new(fromUtc, toUtc);
    }
    public Interval<double> AnalyseCombinedFloatSums(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        return get(logKey)?.AnalyseCombinedDoubleSums(property, intervalType, fromUtc, toUtc) ?? new(fromUtc, toUtc);
    }
    public Interval<AvgMinMax<double>> AnalyseCombinedAvgMinMax(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        return get(logKey)?.AnalyseCombinedAvgMinMax(property, intervalType, fromUtc, toUtc) ?? new(fromUtc, toUtc);
    }
    public Interval<CountSumAvgMinMax<double>> AnalyseCombinedCountSumAvgMinMax(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        return get(logKey)?.AnalyseCombinedCountSumAvgMinMax(property, intervalType, fromUtc, toUtc) ?? new(fromUtc, toUtc);
    }
    public Interval<Dictionary<string, int>> AnalyseCombinedGroupCounts(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        return get(logKey)?.AnalyseCombinedGroupCounts(property, intervalType, fromUtc, toUtc) ?? new(fromUtc, toUtc);
    }
    public void Dispose() {
        foreach (var log in _logs.Values) log.Dispose();
    }
}
