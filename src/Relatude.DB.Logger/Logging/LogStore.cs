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
            var logs = new Dictionary<string, Log>(_logs, StringComparer.OrdinalIgnoreCase);
            logs.Add(settings.Key, new Log(settings, _io));
            _logs = logs;
        }
    }
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
