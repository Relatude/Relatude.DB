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
        /// <summary>Saves the log's settings as json in the log folder (log/{key}.json, beside the folder of
        /// the log's data), where <see cref="AddLogFromSavedSettings"/> and <see cref="LogStore.FromSavedSettings"/>
        /// find them. Deleting the log's data leaves them in place.</summary>
        void SaveSettings(string logKey);
        void SaveSettingsToFile(string logKey, string filePath);
        void SaveAllSettings();
        bool HasSavedSettings(string logKey);
        void DeleteSavedSettings(string logKey);
        IEnumerable<Interval<AvgMinMax<double>>> AnalyseAvgMinMax(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null);
        Interval<AvgMinMax<double>> AnalyseCombinedAvgMinMax(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc);
        Interval<int> AnalyseCombinedCounts(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc);
        Interval<CountSumAvgMinMax<double>> AnalyseCombinedCountSumAvgMinMax(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, StatisticsType? statistic = null);
        Interval<double> AnalyseCombinedFloatSums(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc);
        Interval<Dictionary<string, int>> AnalyseCombinedGroupCounts(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, StatisticsType? statistic = null);
        Interval<int> AnalyseCombinedIntegerSums(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc);
        Interval<int> AnalyseCombinedRows(string logKey, IntervalType intervalType, DateTime fromUtc, DateTime toUtc);
        IEnumerable<Interval<int>> AnalyseCounts(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null);
        IEnumerable<Interval<CountSumAvgMinMax<double>>> AnalyseCountSumAvgMinMax(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null, StatisticsType? statistic = null);
        IEnumerable<Interval<int>> AnalyseEstimatedUniqueCounts(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null, StatisticsType? statistic = null);
        IEnumerable<Interval<double>> AnalyseFloatSums(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null);
        IEnumerable<Interval<Dictionary<string, int>>> AnalyseGroupCounts(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null, StatisticsType? statistic = null);
        IEnumerable<Interval<int>> AnalyseIntegerSums(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null);
        IEnumerable<Interval<int>> AnalyseRows(string logKey, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null);
        /// <summary>The centre and spread of a column of positions per interval (<see cref="StatisticsType.GeoSpread"/>).</summary>
        IEnumerable<Interval<Common.GeoSpread>> AnalyseGeoSpread(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null);
        /// <summary>...and of a whole range, merged exactly.</summary>
        Interval<Common.GeoSpread> AnalyseCombinedGeoSpread(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc);
        /// <summary>The heatmap of a column of positions per interval (<see cref="StatisticsType.GeoHeatmap"/>).</summary>
        IEnumerable<Interval<GeoHeatmap>> AnalyseGeoHeatmap(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, bool estimateNowInterval, bool fillInBlanks, DateTime? nowSimulated = null);
        /// <summary>...and of a whole range, in at most <paramref name="maxCells"/> cells.</summary>
        Interval<GeoHeatmap> AnalyseCombinedGeoHeatmap(string logKey, string property, IntervalType intervalType, DateTime fromUtc, DateTime toUtc, int maxCells = StatisticsGeoHeatmap.DefaultCombinedCells);
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
        /// <summary>Sizes and the first and last record of one log, from a single listing of its files.</summary>
        LogFilesSummary GetFilesSummary(string logKey);
        LogSettings GetSetting(string logKey);
        IEnumerable<LogSettings> GetSettings();
        DateTime? GetTimestampOfFirstRecord(string logKey);
        DateTime? GetTimestampOfLastRecord(string logKey);
        bool HasLog(string logKey);
        bool IsEnabled(string logKey);
        void RebuildStatistics(string logKey);
        bool Record(string logKey, LogEntry entry, bool flushToDisk = false, bool? forceLogging = null, bool? forceStatistics = null);
        /// <summary>Records an entry and has it written to disk as soon as the log is free, without waiting for it.</summary>
        bool RecordAndFlushWhenFree(string logKey, LogEntry entry);
        /// <summary>Writes every log to disk now if it is free, or has whoever holds it do so as it lets go. Never waits.</summary>
        void FlushToDiskWhenFree();
        /// <summary>What a log is busy with, and whether its entries reach its files. Never waits.</summary>
        LogCondition GetCondition(string logKey);
        /// <summary>
        /// Writes out and closes the files of every log that can be had within <paramref name="wait"/>, and
        /// replaces each one that cannot by a fresh log on the same files, letting go of the busy one without
        /// waiting for it (its statistics since they were last saved are lost). The way past a log that is stuck.
        /// </summary>
        LogReleaseResult ReleaseFiles(TimeSpan wait);
        void SaveStatistics();
        void SaveStatsAndDeleteExpiredData();
    }
    public sealed record LogFilesSummary(DateTime? FirstRecordUtc, DateTime? LastRecordUtc, long LogBytes, long StatisticsBytes) {
        public static readonly LogFilesSummary Empty = new(null, null, 0, 0);
        public long TotalBytes => LogBytes + StatisticsBytes;
        /// <summary>The figures given last time: the log was busy, and they were not waited for.</summary>
        public bool Stale { get; init; }
    }
    /// <summary>
    /// What a log is busy with, and since when (null when it is free), and why what it records is not
    /// reaching its files, if it is not. <see cref="DroppedEntries"/> counts what it has let go of since it
    /// was opened: entries that found it busy with a full backlog, or could not be written in time.
    /// </summary>
    public sealed record LogCondition(string? BusyWith, DateTime? BusySinceUtc, string? WriteProblem, DateTime? WriteProblemSinceUtc, long DroppedEntries) {
        public static readonly LogCondition Unknown = new(null, null, null, null, 0);
    }
    /// <summary>The logs a release wrote out and closed, and those that stayed busy and were replaced by fresh ones.</summary>
    public sealed record LogReleaseResult(string[] Released, string[] Replaced) {
        public static LogReleaseResult Combine(LogReleaseResult a, LogReleaseResult b) => new([.. a.Released, .. b.Released], [.. a.Replaced, .. b.Replaced]);
    }
    /// <summary>A log stayed busy for longer than a reader would wait for it.</summary>
    public class LogBusyException(string logKey, string? logName, string? busyWith, DateTime? busySinceUtc, TimeSpan waited)
        : Exception(message(logKey, logName, busyWith, busySinceUtc, waited)) {
        public string LogKey { get; } = logKey;
        public string? BusyWith { get; } = busyWith;
        public DateTime? BusySinceUtc { get; } = busySinceUtc;
        static string message(string logKey, string? logName, string? busyWith, DateTime? busySinceUtc, TimeSpan waited) {
            var name = string.IsNullOrWhiteSpace(logName) ? logKey : logName;
            var what = busyWith == null ? "busy" : "busy " + busyWith;
            var since = busySinceUtc is DateTime s ? " for " + Math.Max(0, (DateTime.UtcNow - s).TotalSeconds).ToString("0") + " s" : "";
            return "The log \"" + name + "\" is " + what + since + ", and was not free after " + waited.TotalSeconds.ToString("0") + " s. "
                + "Try again in a moment, or release the logs on the Activity page if it stays busy.";
        }
    }
}