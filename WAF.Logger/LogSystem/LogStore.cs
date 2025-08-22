using WAF.IO;
using WAF.LogSystem;
using WAF.LogSystem.Statistics;

namespace WAF.LogSystem;
public class LogStore : IDisposable {
    readonly Dictionary<string, Log> _logs;
    IIOProvider _io;
    static public LogStore EmptySink = new(new IOProviderMemory(), Array.Empty<LogSettings>());
    public LogStore(IIOProvider io, IEnumerable<LogSettings> logSettings) {
        _io = io;
        _logs = logSettings.ToDictionary(s => s.Key, s => new Log(s, _io), StringComparer.OrdinalIgnoreCase);
    }
    public bool Record(string logKey, LogEntry entry, bool flushToDisk = false) {
        if (_logs.TryGetValue(logKey, out var log)) {
            log.Record(entry, flushToDisk);
            return true;
        }
        return false;
    }
    public IEnumerable<LogEntry> ExtractLog(string logKey, DateTime fromAndIncluding, DateTime upUntil, int skip, int take, out int total) {
        if (_logs.TryGetValue(logKey, out var log)) {
            return log.Extract(fromAndIncluding, upUntil, skip, take, out total);
        } else {
            total = 0;
            return new List<LogEntry>();
        }
    }
    public long GetFileSize(string logKey) {
        if (_logs.TryGetValue(logKey, out var log)) {
            return log.GetTotalFileSize();
        } else {
            return 0;
        }
    }
    public void DeleteLogOlderThan(string logKey, DateTime to) {
        if (_logs.TryGetValue(logKey, out var log)) log.EnforceDateLimit(to);
    }
    public void DeleteStatistics(string logKey) {
        if (_logs.TryGetValue(logKey, out var log)) log.DeleteStatistics();
    }
    public void DeleteLogAndStatistics(string logKey) {
        if (_logs.TryGetValue(logKey, out var log)) {
            log.EnforceDateLimit(DateTime.MaxValue);
            log.DeleteStatistics();
        }
    }
    public void DeleteAll() {
        foreach (var log in _logs) DeleteLogAndStatistics(log.Key);
    }
    public void ExecuteMaintenance() {
        SaveStatistics();
        EnforceLimits();
    }
    public void SaveStatistics() {
        foreach (var log in _logs) log.Value.SaveStatisticsState();
    }
    public void FlushToDisk() {
        foreach (var log in _logs) log.Value.FlushToDisk();
    }
    public void EnforceLimits() {
        foreach (var log in _logs.Values) {
            if (log.Setting.MaxAgeOfLogFilesInDays > 0) {
                log.EnforceDateLimit(DateTime.UtcNow.AddDays(-log.Setting.MaxAgeOfLogFilesInDays));
                log.EnforceSizeLimit(log.Setting.MaxTotalSizeOfLogFilesInMb);
            }
        }
    }
    public bool IsEnabled(string logKey) {
        return _logs.TryGetValue(logKey, out var log) && log.Setting.IsEnabled();
    }
    public bool HasLog(string logKey) {
        return _logs.ContainsKey(logKey);
    }
    public void AddLog(LogSettings settings) {
        _logs.Add(settings.Key, new Log(settings, _io));
    }
    public LogSettings GetSetting(string logKey) {
        if (_logs.TryGetValue(logKey, out var log)) return log.Setting;
        throw new Exception($"Log with key '{logKey}' not found");
    }
    public IEnumerable<LogSettings> GetSettings() {
        return _logs.Values.Select(l => l.Setting).ToList();
    }
    public DateTime? GetTimestampOfFirstRecord(string logKey) {
        if (_logs.TryGetValue(logKey, out var log)) return log.GetTimestampOfFirstRecord();
        return null;
    }
    public DateTime? GetTimestampOfLastRecord(string logKey) {
        if (_logs.TryGetValue(logKey, out var log)) return log.GetTimestampOfLastRecord();
        return null;
    }
    public IDictionary<string, List<StatisticsInfo>> GetAvailableStatisticsByProperty(string logKey) {
        if (_logs.TryGetValue(logKey, out var log)) return log.GetAvailableStatisticsByProperty();
        return new Dictionary<string, List<StatisticsInfo>>();
    }
    public IEnumerable<Interval<int>> AnalyseRows(string logKey, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        if (_logs.TryGetValue(logKey, out var log)) return log.AnalyseRows(intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated);
        return Array.Empty<Interval<int>>();
    }
    public IEnumerable<Interval<int>> AnalyseCounts(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        if (_logs.TryGetValue(logKey, out var log)) return log.AnalyseCounts(property, intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated);
        return Array.Empty<Interval<int>>();
    }
    public IEnumerable<Interval<int>> AnalyseIntegerSums(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        if (_logs.TryGetValue(logKey, out var log)) return log.AnalyseIntegerSums(property, intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated);
        return Array.Empty<Interval<int>>();
    }
    public IEnumerable<Interval<double>> AnalyseFloatSums(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        if (_logs.TryGetValue(logKey, out var log)) return log.AnalyseDoubleSums(property, intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated);
        return Array.Empty<Interval<double>>();
    }
    public IEnumerable<Interval<AvgMinMax<double>>> AnalyseAvgMinMax(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        if (_logs.TryGetValue(logKey, out var log)) return log.AnalyseAvgMinMax(property, intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated);
        return Array.Empty<Interval<AvgMinMax<double>>>();
    }
    public IEnumerable<Interval<CountSumAvgMinMax<double>>> AnalyseCountSumAvgMinMax(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        if (_logs.TryGetValue(logKey, out var log)) return log.AnalyseCountSumAvgMinMax(property, intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated);
        return Array.Empty<Interval<CountSumAvgMinMax<double>>>();
    }
    public IEnumerable<Interval<Dictionary<string, int>>> AnalyseGroupCounts(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        if (_logs.TryGetValue(logKey, out var log)) return log.AnalyseGroupCounts(property, intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated);
        return Array.Empty<Interval<Dictionary<string, int>>>();
    }
    public IEnumerable<Interval<int>> AnalyseUniqueCounts(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        if (_logs.TryGetValue(logKey, out var log)) return log.AnalyseUniqueCounts(property, intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated);
        return Array.Empty<Interval<int>>();
    }
    public IEnumerable<Interval<int>> AnalyseEstimatedUniqueCounts(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null) {
        if (_logs.TryGetValue(logKey, out var log)) return log.AnalyseEstimatedUniqueCounts(property, intervalType, fromUtc, toUtc, estimateNowInterval, fillInBlanks, nowSimulated);
        return Array.Empty<Interval<int>>();
    }


    public Interval<int> AnalyseCombinedRows(string logKey, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        if (_logs.TryGetValue(logKey, out var log)) return log.AnalyseCombinedRows(intervalType, fromUtc, toUtc);
        return new(fromUtc, toUtc);
    }
    public Interval<int> AnalyseCombinedCounts(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        if (_logs.TryGetValue(logKey, out var log)) return log.AnalyseCombinedCounts(property, intervalType, fromUtc, toUtc);
        return new(fromUtc, toUtc);
    }
    public Interval<int> AnalyseCombinedIntegerSums(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        if (_logs.TryGetValue(logKey, out var log)) return log.AnalyseCombinedIntegerSums(property, intervalType, fromUtc, toUtc);
        return new(fromUtc, toUtc);
    }
    public Interval<double> AnalyseCombinedFloatSums(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        if (_logs.TryGetValue(logKey, out var log)) return log.AnalyseCombinedDoubleSums(property, intervalType, fromUtc, toUtc);
        return new(fromUtc, toUtc);
    }
    public Interval<AvgMinMax<double>> AnalyseCombinedAvgMinMax(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        if (_logs.TryGetValue(logKey, out var log)) return log.AnalyseCombinedAvgMinMax(property, intervalType, fromUtc, toUtc);
        return new(fromUtc, toUtc);
    }
    public Interval<CountSumAvgMinMax<double>> AnalyseCombinedCountSumAvgMinMax(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        if (_logs.TryGetValue(logKey, out var log)) return log.AnalyseCombinedCountSumAvgMinMax(property, intervalType, fromUtc, toUtc);
        return new(fromUtc, toUtc);
    }
    public Interval<Dictionary<string, int>> AnalyseCombinedGroupCounts(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc) {
        if (_logs.TryGetValue(logKey, out var log)) return log.AnalyseCombinedGroupCounts(property, intervalType, fromUtc, toUtc);
        return new(fromUtc, toUtc);
    }


    public void Dispose() {
        foreach (var log in _logs) {
            log.Value.Dispose();
        }
    }
}
