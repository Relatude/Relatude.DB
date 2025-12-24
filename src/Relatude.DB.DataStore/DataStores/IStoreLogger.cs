using Relatude.DB.Logging;
using Relatude.DB.Logging.Statistics;
using Relatude.DB.Query;
using Relatude.DB.Tasks;
using System.Text.Json.Serialization;
namespace Relatude.DB.DataStores;
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SystemLogEntryType {
    Info,
    Warning,
    Error,
    Backup,
}
public class TraceEntry(DateTime timestamp, SystemLogEntryType type, string text, string? details = null) {
    public DateTime Timestamp { get; } = timestamp;
    public SystemLogEntryType Type { get; } = type;
    public string Text { get; } = text;
    public string? Details { get; } = details;
}
public class StoreMetrics {
    public long MemUsage { get; set; }
    public double CpuUsage { get; set; }
    public long QueryCount { get; set; }
    public long ActionCount { get; set; }
    public long TransactionCount { get; set; }
    public int NodeCount { get; set; }
    public int RelationCount { get; set; }
    public int NodeCacheCount { get; set; }
    public long NodeCacheSize { get; set; }
    public int SetCacheCount { get; set; }
    public long SetCacheSize { get; set; }
    public int TasksExecuted { get; set; }
    public int TasksPersistedExecuted { get; set; }
    public int TasksQueued { get; set; }
    public int TasksPersistedQueued { get; set; }
}
public interface IStoreLogger {
    bool LoggingActions { get; }
    bool LoggingAny { get; }
    bool LoggingMetrics { get; }
    bool LoggingQueries { get; }
    bool LoggingSystem { get; }
    bool LoggingTask { get; }
    bool LoggingTaskBatch { get; }
    bool LoggingTransactions { get; }
    bool LoggingTransactionsOrActions { get; }
    int MinDurationMsBeforeLogging { get; set; }
    bool RecordingPropertyHits { get; set; }
    public ILogStore LogStore { get; }
    Interval<int>[] AnalyseActionCount(IntervalType intervalType, DateTime from, DateTime to);
    Interval<Dictionary<string, int>> AnalyseActionOperations(IntervalType intervalType, DateTime from, DateTime to);
    Interval<int>[] AnalyseQueryCount(IntervalType intervalType, DateTime from, DateTime to);
    Interval<CountSumAvgMinMax<double>>[] AnalyseQueryDuration(IntervalType intervalType, DateTime from, DateTime to);
    Interval<int>[] AnalyseSystemLogCount(IntervalType intervalType, DateTime from, DateTime to);
    Interval<Dictionary<string, int>> AnalyseSystemLogCountByType(IntervalType intervalType, DateTime from, DateTime to);
    Interval<CountSumAvgMinMax<double>>[] AnalyseTransactionAction(IntervalType intervalType, DateTime from, DateTime to);
    Interval<int>[] AnalyseTransactionCount(IntervalType intervalType, DateTime from, DateTime to);
    Interval<CountSumAvgMinMax<double>>[] AnalyseTransactionDuration(IntervalType intervalType, DateTime from, DateTime to);
    KeyValuePair<string, int>[] AnalyzePropertyHits();
    void ClearLog(string logKey);
    void ClearStatistics(string logKey);
    void Dispose();
    void EnableLog(string logKey, bool enable);
    void EnableStatistics(string logKey, bool enable);
    LogEntry[] ExtractLog(string logKey, DateTime from, DateTime to, int skip, int take, bool orderByDescendingDates, out int total);
    void FlushToDiskNow();
    KeyValuePair<string, string>[] GetLogKeysAndNames();
    bool IsLogEnabled(string logKey);
    bool IsStatisticsEnabled(string logKey);
    void RecordAction(long transactionId, string operation, string details);
    void RecordPropertyHit(Guid propertyId);
    void RecordQuery(string query, TimeSpan duration, int resultCount, Metrics metrics);
    void RecordMetrics(StoreMetrics storeMetrics);
    void RecordSystem(SystemLogEntryType type, string text, string? details = null);
    void RecordTask(string taskTypeName, bool success, Guid batchId, string taskId, string details);
    void RecordTaskBatch(Guid id, BatchTaskResult batchResult);
    void RecordTransaction(long transactionId, TimeSpan duration, int actionCount, int primitiveActionCount, bool diskFlush);
    void SaveStatsAndDeleteExpiredData();
    long GetTotalFileSize();
}