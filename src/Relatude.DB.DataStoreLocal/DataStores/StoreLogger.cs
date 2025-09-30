using Relatude.DB.Datamodels;
using Relatude.DB.IO;
using Relatude.DB.Logging;
using Relatude.DB.Logging.Statistics;
using Relatude.DB.Query;
using Relatude.DB.Tasks;
namespace Relatude.DB.DataStores;
public class StoreLogger : IDisposable, IStoreLogger {

    static string _systemLogKey = "system";
    static string _queryLogKey = "query";
    static string _transactionLogKey = "transaction";
    static string _actionLogKey = "action";
    static string _taskLogKey = "task";
    static string _taskbatchLogKey = "taskbatch";
    static string _metricsLogKey = "metrics";

    readonly IIOProvider _io;
    readonly FileKeyUtility _fileKeys;
    LogStore _logStore;
    readonly Datamodel? _datamodel;
    public ILogStore LogStore => _logStore;
    LogSettings[]? _settings;
    LogSettings[] getSettings() {
        _settings = [
            new() {
                Name = "System",
                Key = _systemLogKey,
                FileInterval = FileInterval.Day,
                EnableLog = true,
                EnableStatistics = true,
                EnableLogTextFormat = true,
                ResolutionRowStats = 4,
                FirstDayOfWeek = DayOfWeek.Monday,
                MaxAgeOfLogFilesInDays = 10,
                MaxTotalSizeOfLogFilesInMb = 100,
                Compressed = false,
                Properties = {
                        { "type", new() { Name = "Type", DataType = LogDataType.String, Statistics = [new (StatisticsType.UniqueCountWithValues)]} },
                        { "text", new() { Name = "Text", DataType = LogDataType.String } },
                        { "details", new() { Name = "Details",DataType = LogDataType.String, } },
                },
            },
            new() {
                Name = "Queries",
                Key = _queryLogKey,
                FileInterval = FileInterval.Day,
                EnableLog = true,
                EnableStatistics = true,
                EnableLogTextFormat = false,
                ResolutionRowStats = 4,
                FirstDayOfWeek = DayOfWeek.Monday,
                MaxAgeOfLogFilesInDays = 10,
                MaxTotalSizeOfLogFilesInMb = 100,
                Compressed = false,
                Properties = {
                        { "query", new() { Name = "Query", DataType = LogDataType.String, } },
                        { "duration", new() { Name = "Duration", DataType = LogDataType.Double, Statistics = [new (StatisticsType.CountSumAvgMinMax)] } },
                        { "resultCount", new() { Name = "Row count",DataType = LogDataType.Integer, } },
                        { "nodeCount", new() { Name = "Node count", DataType = LogDataType.Integer, } },
                        { "uniqueNodeCount", new() { Name = "Unique node count", DataType = LogDataType.Integer, } },
                        { "diskReads", new() { Name = "Disk reads", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.CountSumAvgMinMax)] } },
                        { "nodesReadFromDisk", new() { Name = "Nodes read from disk", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.CountSumAvgMinMax)] }}
                },
            },
            new() {
                Name = "Transactions",
                Key = _transactionLogKey,
                FileInterval = FileInterval.Day,
                EnableLog = true,
                EnableStatistics = true,
                EnableLogTextFormat = false,
                ResolutionRowStats = 4,
                FirstDayOfWeek = DayOfWeek.Monday,
                MaxAgeOfLogFilesInDays = 10,
                MaxTotalSizeOfLogFilesInMb = 100,
                Compressed = false,
                Properties = {
                        { "transactionId", new() { Name = "Transaction ID", DataType = LogDataType.String } },
                        { "duration", new() { Name = "Duration", DataType = LogDataType.Double, Statistics = [new (StatisticsType.CountSumAvgMinMax)] } },
                        { "actionCount", new() { Name = "Actions", Statistics = [new (StatisticsType.CountSumAvgMinMax)] }},
                        { "primitiveActionCount", new() { Name = "Actions", Statistics = [new (StatisticsType.CountSumAvgMinMax)] }},
                        { "diskFlush", new() { Name = "Disk flush", DataType = LogDataType.String } },
                    },
            },
            new() {
                Name = "Actions",
                Key = _actionLogKey,
                FileInterval = FileInterval.Day,
                EnableLog = true,
                EnableStatistics = true,
                EnableLogTextFormat = false,
                ResolutionRowStats = 4,
                FirstDayOfWeek = DayOfWeek.Monday,
                MaxAgeOfLogFilesInDays = 10,
                MaxTotalSizeOfLogFilesInMb = 100,
                Compressed = false,
                Properties = {
                        { "transactionId", new() { Name = "Transaction ID", DataType = LogDataType.String } },
                        { "operation", new() { Name = "Operation", DataType = LogDataType.String, Statistics = [new (StatisticsType.UniqueCountWithValues)] }},
                        { "details", new() { Name = "Details", DataType = LogDataType.String }},
                    },
            },
            new() {
                Name = "Tasks",
                Key = _taskLogKey,
                FileInterval = FileInterval.Day,
                EnableLog = true,
                EnableStatistics = true,
                EnableLogTextFormat = false,
                ResolutionRowStats = 4,
                FirstDayOfWeek = DayOfWeek.Monday,
                MaxAgeOfLogFilesInDays = 10,
                MaxTotalSizeOfLogFilesInMb = 100,
                Compressed = false,
                Properties = {
                    { "taskTypeName", new() { Name = "Task type", DataType = LogDataType.String, Statistics = [new (StatisticsType.UniqueCountWithValues)] }},
                    { "success", new() { Name = "Success", DataType = LogDataType.String, Statistics = [new (StatisticsType.UniqueCountWithValues)] }},
                    { "batchId", new() { Name = "Batch ID", DataType = LogDataType.String } },
                    { "taskId", new() { Name = "Task ID", DataType = LogDataType.String } },
                    { "details", new() { Name = "Details", DataType = LogDataType.String }},
                },
            },
            new() {
                Name = "TaskBatches",
                Key = _taskbatchLogKey,
                FileInterval = FileInterval.Day,
                EnableLog = true,
                EnableStatistics = true,
                EnableLogTextFormat = false,
                ResolutionRowStats = 4,
                FirstDayOfWeek = DayOfWeek.Monday,
                MaxAgeOfLogFilesInDays = 10,
                MaxTotalSizeOfLogFilesInMb = 100,
                Compressed = false,
                    Properties = {
                        { "batchId", new() { Name = "Batch Id", DataType = LogDataType.String, }},
                        { "taskTypeName", new() { Name = "Task type", DataType = LogDataType.String, Statistics = [new (StatisticsType.UniqueCountWithValues)] }},
                        { "started", new() { Name = "Started", DataType = LogDataType.DateTime } },
                        { "duration", new() { Name = "Duration", DataType = LogDataType.Double, Statistics = [new (StatisticsType.CountSumAvgMinMax)] } },
                        { "taskCount", new() { Name = "Task count", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.CountSumAvgMinMax)] } },
                        { "success", new() { Name = "Success", DataType = LogDataType.String, Statistics = [new (StatisticsType.UniqueCountWithValues)] }},
                        { "error", new() { Name = "Error", DataType = LogDataType.String }},
                    },
            },
            new() {
                Name = "Metrics",
                Key = _metricsLogKey,
                FileInterval = FileInterval.Day,
                EnableLog = true,
                EnableStatistics = true,
                EnableLogTextFormat = false,
                ResolutionRowStats = 4,
                FirstDayOfWeek = DayOfWeek.Monday,
                MaxAgeOfLogFilesInDays = 10,
                MaxTotalSizeOfLogFilesInMb = 100,
                Compressed = false,
                Properties = {
                        { "queryCount", new() { Name = "Query count", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.Sum)] } },
                        { "transactionCount", new() { Name = "Transaction Count", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.Sum)] } },
                        { "nodeCount", new() { Name = "Node count", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.AvgMinMax)] } },
                        { "relationCount", new() { Name = "Relation count", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.AvgMinMax)] } },
                        { "nodeCacheCount", new() { Name = "Node cache count", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.AvgMinMax)] } },
                        { "nodeCacheSize", new() { Name = "Node cache size", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.AvgMinMax)] } },
                        { "setCacheCount", new() { Name = "Set cache count", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.AvgMinMax)] } },
                        { "setCacheSize", new() { Name = "Set cache size", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.AvgMinMax)] } },
                        { "taskQueueCount", new() { Name = "Task queue count", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.AvgMinMax)] } },
                    },
            },
        ];
        return _settings;
    }

    public bool EnableSystemLog = true; // only system log, is enabled by default
    public bool EnableSystemLogStatistics = false; // all statistics enabled by default for all logs...
    public bool EnableSystemQueryLog = false;
    public bool EnableSystemQueryLogStatistics = false;
    public bool EnableTransactionLog = false;
    public bool EnableTransactionLogStatistics = false;
    public bool EnableActionLog = false;
    public bool EnableActionLogStatistics = false;
    public bool EnableTaskLog = false;
    public bool EnableTaskLogStatistics = false;
    public bool EnableTaskBatchLog = false;
    public bool EnableTaskBatchLogStatistics = false;
    public bool EnableMetricsLog = false;
    public bool EnableMetricsLogStatistics = false;

    public bool LoggingSystem => EnableSystemLog || EnableSystemLogStatistics;
    public bool LoggingTransactionsOrActions => EnableTransactionLog || EnableActionLog || EnableTransactionLogStatistics || EnableActionLogStatistics;
    public bool LoggingTransactions => EnableTransactionLog || EnableTransactionLogStatistics;
    public bool LoggingActions => EnableActionLog || EnableActionLogStatistics;
    public bool LoggingQueries => EnableSystemQueryLog || EnableSystemQueryLogStatistics;
    public bool LoggingTask => EnableTaskLog || EnableTaskLogStatistics;
    public bool LoggingTaskBatch => EnableTaskBatchLog || EnableTaskBatchLogStatistics;
    public bool LoggingMetrics => EnableMetricsLog || EnableMetricsLogStatistics;
    public bool LoggingAny => EnableSystemLog || EnableSystemLogStatistics || EnableSystemQueryLog || EnableSystemQueryLogStatistics || EnableTransactionLog || EnableTransactionLogStatistics || EnableActionLog || EnableActionLogStatistics || EnableTaskLog || EnableTaskLogStatistics || EnableTaskBatchLog || EnableTaskBatchLogStatistics || EnableMetricsLog || EnableMetricsLogStatistics;

    public int MinDurationMsBeforeLogging { get; set; } = 0; // in milliseconds

    public StoreLogger(IIOProvider io, FileKeyUtility fileKeys, Datamodel? datamodel) {
        _fileKeys = fileKeys;
        _io = io;
        _datamodel = datamodel;
        _logStore = new LogStore(_io, getSettings(), fileKeys);
    }
    public void RecordSystem(SystemLogEntryType type, string text, string? details = null) {
        if (!EnableSystemLog) return;
        LogEntry entry = new();
        entry.Values.Add("type", type.ToString());
        entry.Values.Add("text", text);
        entry.Values.Add("details", details == null ? string.Empty : details);
        _logStore?.Record(_systemLogKey, entry);
        _logStore?.FlushToDiskNow(_systemLogKey);
    }
    public void RecordQuery(string query, double durationMs, int resultCount, Metrics metrics) {
        if (!EnableSystemQueryLog) return;
        if (durationMs < MinDurationMsBeforeLogging) return;
        LogEntry entry = new();
        entry.Values.Add("query", query);
        entry.Values.Add("duration", durationMs);
        entry.Values.Add("resultCount", resultCount);
        entry.Values.Add("nodeCount", metrics.NodeCount);
        entry.Values.Add("uniqueNodeCount", metrics.UniqueNodeCount);
        entry.Values.Add("diskReads", metrics.DiskReads);
        entry.Values.Add("nodesReadFromDisk", metrics.NodesReadFromDisk);
        _logStore?.Record(_queryLogKey, entry);
    }
    public void RecordTransaction(long transactionId, double duration, int actionCount, int primitiveActionCount, bool diskFlush) {
        if (!EnableTransactionLog) return;
        LogEntry entry = new();
        entry.Values.Add("transactionId", transactionId);
        entry.Values.Add("duration", duration);
        entry.Values.Add("actionCount", actionCount);
        entry.Values.Add("primitiveActionCount", primitiveActionCount);
        entry.Values.Add("diskFlush", diskFlush ? "Yes" : "-");
        _logStore?.Record(_transactionLogKey, entry);
    }
    public void RecordAction(long transactionId, string operation, string details) {
        if (!EnableActionLog) return;
        LogEntry entry = new();
        entry.Values.Add("transactionId", transactionId);
        entry.Values.Add("operation", operation);
        entry.Values.Add("details", details);
        _logStore?.Record(_actionLogKey, entry);
    }
    public void RecordTask(string taskTypeName, bool success, Guid batchId, string taskId, string details) {
        if (!EnableTaskLog) return;
        LogEntry entry = new();
        entry.Values.Add("taskTypeName", taskTypeName);
        entry.Values.Add("success", success ? "Success" : "Error");
        entry.Values.Add("batchId", batchId.ToString());
        entry.Values.Add("taskId", taskId);
        entry.Values.Add("details", details);
        _logStore?.Record(_taskLogKey, entry);
    }
    public void RecordTaskBatch(Guid id, BatchTaskResult batchResult) {
        if (!EnableTaskBatchLog) return;
        LogEntry entry = new();
        entry.Values.Add("batchId", id.ToString());
        entry.Values.Add("taskTypeName", batchResult.TaskTypeName);
        entry.Values.Add("started", batchResult.StartedUTC);
        entry.Values.Add("duration", batchResult.DurationMs);
        entry.Values.Add("taskCount", batchResult.TaskCount);
        entry.Values.Add("success", batchResult.Error == null ? "Success" : "Error");
        entry.Values.Add("error", batchResult.Error?.Message ?? string.Empty);

        _logStore?.Record(_taskbatchLogKey, entry);
    }
    public void RecordStoreMetrics(StoreMetrics storeMetrics) {
        LogEntry entry = new();
        entry.Values.Add("queryCount", storeMetrics.QueryCount);
        entry.Values.Add("transactionCount", storeMetrics.TransactionCount);
        entry.Values.Add("nodeCount", storeMetrics.NodeCount);
        entry.Values.Add("relationCount", storeMetrics.RelationCount);
        entry.Values.Add("nodeCacheCount", storeMetrics.NodeCacheCount);
        entry.Values.Add("nodeCacheSize", storeMetrics.NodeCacheSize);
        entry.Values.Add("setCacheCount", storeMetrics.SetCacheCount);
        entry.Values.Add("setCacheSize", storeMetrics.SetCacheSize);
        entry.Values.Add("taskQueueCount", storeMetrics.TaskQueueCount);
        entry.Values.Add("taskPersistedQueueCount", storeMetrics.TaskPersistedQueueCount);
        _logStore?.Record(_metricsLogKey, entry);
    }

    bool _isRecordingPropertyHits = false;
    Dictionary<Guid, int> _propertyHits = [];
    public bool RecordingPropertyHits {
        set {
            lock (_propertyHits) {
                if (value) {
                    if (!_isRecordingPropertyHits) { // Only clear if not already recording
                        _propertyHits.Clear();
                        _isRecordingPropertyHits = true;
                    }
                } else {
                    lock (_propertyHits) {
                        _isRecordingPropertyHits = false;
                    }
                }
            }
        }
        get {
            lock (_propertyHits) return _isRecordingPropertyHits;
        }
    }
    public KeyValuePair<string, int>[] AnalyzePropertyHits() {
        if (_datamodel == null) return [];
        lock (_propertyHits) {
            return _propertyHits.Select(kv => {
                var property = _datamodel.Properties[kv.Key];
                var nodeType = _datamodel.NodeTypes[property.NodeType];
                var name = $"{nodeType.FullName}.{property.CodeName}";
                return new KeyValuePair<string, int>(name, kv.Value);
            }
            ).ToArray();
        }
    }
    public void RecordPropertyHit(Guid propertyId) {
        lock (_propertyHits) {
            if (_propertyHits.TryGetValue(propertyId, out var count)) {
                _propertyHits[propertyId] = count + 1;
            } else {
                _propertyHits.Add(propertyId, 1);
            }
        }
    }
    public void ClearLog(string logKey) {
        if (_logStore == null) return;
        _logStore.DeleteLog(logKey);
    }
    public void ClearStatistics(string logKey) {
        if (_logStore == null) return;
        _logStore.DeleteStatistics(logKey);
    }
    public LogEntry[] ExtractLog(string logKey, DateTime from, DateTime to, int skip, int take, bool orderByDescendingDates, out int total) {
        if (_logStore == null) { total = 0; return []; }
        return _logStore.ExtractLog(logKey, from, to, skip, take, orderByDescendingDates, out total).ToArray();
    }
    public Interval<int>[] AnalyseSystemLogCount(IntervalType intervalType, DateTime from, DateTime to) {
        if (_logStore == null) return [];
        return _logStore.AnalyseRows(_systemLogKey, intervalType, from, to, false, true).ToArray();
    }
    public Interval<Dictionary<string, int>> AnalyseSystemLogCountByType(IntervalType intervalType, DateTime from, DateTime to) {
        if (_logStore == null) return new Interval<Dictionary<string, int>>(from, to);
        return _logStore.AnalyseCombinedGroupCounts(_systemLogKey, "type", intervalType, from, to);
    }
    public Interval<int>[] AnalyseQueryCount(IntervalType intervalType, DateTime from, DateTime to) {
        if (_logStore == null) return [];
        return _logStore.AnalyseRows(_queryLogKey, intervalType, from, to, false, true).ToArray();
    }
    public Interval<CountSumAvgMinMax<double>>[] AnalyseQueryDuration(IntervalType intervalType, DateTime from, DateTime to) {
        if (_logStore == null) return [];
        return _logStore.AnalyseCountSumAvgMinMax(_queryLogKey, "duration", intervalType, from, to, false, true).ToArray();
    }
    public Interval<int>[] AnalyseTransactionCount(IntervalType intervalType, DateTime from, DateTime to) {
        if (_logStore == null) return [];
        return _logStore.AnalyseRows(_transactionLogKey, intervalType, from, to, false, true).ToArray();
    }
    public Interval<CountSumAvgMinMax<double>>[] AnalyseTransactionDuration(IntervalType intervalType, DateTime from, DateTime to) {
        if (_logStore == null) return [];
        return _logStore.AnalyseCountSumAvgMinMax(_transactionLogKey, "duration", intervalType, from, to, false, true).ToArray();
    }
    public Interval<CountSumAvgMinMax<double>>[] AnalyseTransactionAction(IntervalType intervalType, DateTime from, DateTime to) {
        if (_logStore == null) return [];
        return _logStore.AnalyseCountSumAvgMinMax(_transactionLogKey, "actionCount", intervalType, from, to, false, true).ToArray();
    }
    public Interval<int>[] AnalyseActionCount(IntervalType intervalType, DateTime from, DateTime to) {
        if (_logStore == null) return [];
        return _logStore.AnalyseRows(_actionLogKey, intervalType, from, to, false, true).ToArray();
    }
    public Interval<Dictionary<string, int>> AnalyseActionOperations(IntervalType intervalType, DateTime from, DateTime to) {
        if (_logStore == null) return new Interval<Dictionary<string, int>>(from, to);
        return _logStore.AnalyseCombinedGroupCounts(_actionLogKey, "operation", intervalType, from, to);
    }

    public void FlushToDiskNow() {
        if (_logStore != null) _logStore.FlushToDiskNow();
    }
    public void SaveStatsAndDeleteExpiredData() {
        if (_logStore != null) _logStore.SaveStatsAndDeleteExpiredData();
    }
    public void Dispose() {
        _logStore?.Dispose();
    }

    public KeyValuePair<string, string>[] GetLogKeysAndNames() {
        if (_settings == null) return [];
        return _settings.Select(s => new KeyValuePair<string, string>(s.Key, s.Name)).ToArray();
    }
    public void EnableLog(string logKey, bool enable) {
        if (logKey == _systemLogKey) EnableSystemLog = enable;
        else if (logKey == _queryLogKey) EnableSystemQueryLog = enable;
        else if (logKey == _transactionLogKey) EnableTransactionLog = enable;
        else if (logKey == _actionLogKey) EnableActionLog = enable;
        else if (logKey == _taskLogKey) EnableTaskLog = enable;
        else if (logKey == _taskbatchLogKey) EnableTaskBatchLog = enable;
        else if (logKey == _metricsLogKey) EnableMetricsLog = enable;
    }
    public void EnableStatistics(string logKey, bool enable) {
        if (logKey == _systemLogKey) EnableSystemLogStatistics = enable;
        else if (logKey == _queryLogKey) EnableSystemQueryLogStatistics = enable;
        else if (logKey == _transactionLogKey) EnableTransactionLogStatistics = enable;
        else if (logKey == _actionLogKey) EnableActionLogStatistics = enable;
        else if (logKey == _taskLogKey) EnableTaskLogStatistics = enable;
        else if (logKey == _taskbatchLogKey) EnableTaskBatchLogStatistics = enable;
        else if (logKey == _metricsLogKey) EnableMetricsLogStatistics = enable;
    }
    public bool IsLogEnabled(string logKey) {
        if (logKey == _systemLogKey) return EnableSystemLog;
        else if (logKey == _queryLogKey) return EnableSystemQueryLog;
        else if (logKey == _transactionLogKey) return EnableTransactionLog;
        else if (logKey == _actionLogKey) return EnableActionLog;
        else if (logKey == _taskLogKey) return EnableTaskLog;
        else if (logKey == _taskbatchLogKey) return EnableTaskBatchLog;
        else if (logKey == _metricsLogKey) return EnableMetricsLog;
        return false;
    }
    public bool IsStatisticsEnabled(string logKey) {
        if (logKey == _systemLogKey) return EnableSystemLogStatistics;
        else if (logKey == _queryLogKey) return EnableSystemQueryLogStatistics;
        else if (logKey == _transactionLogKey) return EnableTransactionLogStatistics;
        else if (logKey == _actionLogKey) return EnableActionLogStatistics;
        else if (logKey == _taskLogKey) return EnableTaskLogStatistics;
        else if (logKey == _taskbatchLogKey) return EnableTaskBatchLogStatistics;
        else if (logKey == _metricsLogKey) return EnableMetricsLogStatistics;
        return false;
    }


}
