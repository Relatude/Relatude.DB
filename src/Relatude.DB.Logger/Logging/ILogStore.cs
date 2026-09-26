using Relatude.DB.Logging.Statistics;

namespace Relatude.DB.Logging {
    public interface ILogStore {
        void AddLog(LogSettings settings);
        /// <summary>Takes a log out of the store, closing its files; what it wrote stays on disk.</summary>
        bool RemoveLog(string logKey);
        /// <summary>Puts a log back with new settings (or adds it), closing the old one first.</summary>
        void ReplaceLog(LogSettings settings);
        /// <summary>Deletes the recorded entries of one log, keeping its statistics.</summary>
        void DeleteLog(string logKey);
        void FlushToDiskNow(string logKey);
        /// <summary>Adds a log from settings json (see <see cref="LogSettings.FromJson"/>) and returns its settings.</summary>
        LogSettings AddLogFromJson(string json);
        /// <summary>Adds a log from a settings json file on the local disk and returns its settings.</summary>
        LogSettings AddLogFromFile(string filePath);
        /// <summary>Adds a log from the settings saved for it by <see cref="SaveSettings"/>; throws if there are none.</summary>
        LogSettings AddLogFromSavedSettings(string logKey);
        string GetSettingJson(string logKey);
        /// <summary>Saves the log's settings as json in the log folder, where <see cref="AddLogFromSavedSettings"/>
        /// and <see cref="LogStore.FromSavedSettings"/> find them. Deleting the log's data leaves them in place.</summary>
        void SaveSettings(string logKey);
        void SaveSettingsToFile(string logKey, string filePath);
        void SaveAllSettings();
        bool HasSavedSettings(string logKey);
        void DeleteSavedSettings(string logKey);
        IEnumerable<Interval<AvgMinMax<double>>> AnalyseAvgMinMax(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null);
        Interval<AvgMinMax<double>> AnalyseCombinedAvgMinMax(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc);
        Interval<int> AnalyseCombinedCounts(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc);
        Interval<CountSumAvgMinMax<double>> AnalyseCombinedCountSumAvgMinMax(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc);
        Interval<double> AnalyseCombinedFloatSums(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc);
        Interval<Dictionary<string, int>> AnalyseCombinedGroupCounts(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc);
        Interval<int> AnalyseCombinedIntegerSums(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc);
        Interval<int> AnalyseCombinedRows(string logKey, IntervalType intervalType, DateTime fromUtc, DateTime toUtc);
        IEnumerable<Interval<int>> AnalyseCounts(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null);
        IEnumerable<Interval<CountSumAvgMinMax<double>>> AnalyseCountSumAvgMinMax(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null);
        IEnumerable<Interval<int>> AnalyseEstimatedUniqueCounts(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null);
        IEnumerable<Interval<double>> AnalyseFloatSums(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null);
        IEnumerable<Interval<Dictionary<string, int>>> AnalyseGroupCounts(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null);
        IEnumerable<Interval<int>> AnalyseIntegerSums(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null);
        IEnumerable<Interval<int>> AnalyseRows(string logKey, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null);
        IEnumerable<Interval<int>> AnalyseUniqueCounts(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null);
        void DeleteAll();
        void DeleteLogAndStatistics(string logKey);
        void DeleteLogOlderThan(string logKey, DateTime to);
        void DeleteStatistics(string logKey);
        void Dispose();
        void EnforceLimits();
        IEnumerable<LogEntry> ExtractLog(string logKey, DateTime fromAndIncluding, DateTime upUntil, int skip, int take, bool orderByDescendingDates, out int total);
        /// <summary>The entries of a range that a text search matches, newest or oldest first, with
        /// the number of matches in the whole range. Nothing is indexed: the range is read and every
        /// record in it tested, so it is the range that bounds the cost.</summary>
        IEnumerable<LogEntry> SearchLog(string logKey, LogSearch search, DateTime fromAndIncluding, DateTime upUntil, int skip, int take, bool orderByDescendingDates, out int total);
        /// <summary>The same, with the search written out: see <see cref="LogSearch.Parse"/> for what it may say.</summary>
        IEnumerable<LogEntry> SearchLog(string logKey, string search, DateTime fromAndIncluding, DateTime upUntil, int skip, int take, bool orderByDescendingDates, out int total);
        void FlushToDiskNow();
        IDictionary<string, List<StatisticsInfo>> GetAvailableStatisticsByProperty(string logKey);
        long GetFileSize(string logKey);
        long GetLogFileSize(string logKey);
        long GetStatisticsFileSize(string logKey);
        LogSettings GetSetting(string logKey);
        IEnumerable<LogSettings> GetSettings();
        DateTime? GetTimestampOfFirstRecord(string logKey);
        DateTime? GetTimestampOfLastRecord(string logKey);
        bool HasLog(string logKey);
        bool IsEnabled(string logKey);
        void RebuildStatistics(string logKey);
        bool Record(string logKey, LogEntry entry, bool flushToDisk = false, bool? forceLogging = null, bool? forceStatistics = null);
        void SaveStatistics();
        void SaveStatsAndDeleteExpiredData();
    }
}