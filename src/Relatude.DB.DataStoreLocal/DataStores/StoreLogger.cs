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
    static string _ioLogKey = "io";

    readonly IIOProvider _io;
    LogStore _logStore;
    readonly Datamodel? _datamodel;
    public ILogStore LogStore => _logStore;

    bool _enableSystemLog = false;
    bool _enableSystemLogStatistics = false;
    bool _enableSystemQueryLog = false;
    bool _enableSystemQueryLogStatistics = false;
    bool _enableTransactionLog = false;
    bool _enableTransactionLogStatistics = false;
    bool _enableActionLog = false;
    bool _enableActionLogStatistics = false;
    bool _enableTaskLog = false;
    bool _enableTaskLogStatistics = false;
    bool _enableTaskBatchLog = false;
    bool _enableTaskBatchLogStatistics = false;
    bool _enableMetricsLog = false;
    bool _enableMetricsLogStatistics = false;
    bool _enableIoLog = false;
    bool _enableIoLogStatistics = false;

    LogSettings[]? _settings;
    LogSettings[] getSettings() {
        _settings = [
            new() {
                Name = "System",
                Key = _systemLogKey,
                FileInterval = FileInterval.Day,
                EnableLog = _enableSystemLog,
                EnableStatistics = _enableSystemLogStatistics,
                EnableLogTextFormat = false,
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
                EnableLog = _enableSystemQueryLog,
                EnableStatistics = _enableSystemQueryLogStatistics,
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
                EnableLog = _enableTransactionLog,
                EnableStatistics = _enableTransactionLogStatistics,
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
                EnableLog = _enableActionLog,
                EnableStatistics = _enableActionLogStatistics,
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
                EnableLog = _enableTaskBatchLog,
                EnableStatistics = _enableTaskBatchLogStatistics,
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
                EnableLog = _enableMetricsLog,
                EnableStatistics = _enableMetricsLogStatistics,
                EnableLogTextFormat = false,
                ResolutionRowStats = 4,
                FirstDayOfWeek = DayOfWeek.Monday,
                MaxAgeOfLogFilesInDays = 10,
                MaxTotalSizeOfLogFilesInMb = 100,
                Compressed = false,
                Properties = {
                        { "cpuUsagePercentage", new() { Name = "CPU %", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.AvgMinMax)] } },
                        { "memUsageMb", new() { Name = "Memory Mb", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.AvgMinMax)] } },
                        { "queryCount", new() { Name = "Query count", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.Sum)] } },
                        { "actionCount", new() { Name = "Action count", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.Sum)] } },
                        { "transactionCount", new() { Name = "Transaction count", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.Sum)] } },
                        { "nodeCount", new() { Name = "Node count", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.AvgMinMax)] } },
                        { "relationCount", new() { Name = "Relation count", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.AvgMinMax)] } },
                        { "nodeCacheCount", new() { Name = "Node cache count", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.AvgMinMax)] } },
                        { "nodeCacheSizeMb", new() { Name = "Node cache size Mb", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.AvgMinMax)] } },
                        { "setCacheCount", new() { Name = "Set cache count", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.AvgMinMax)] } },
                        { "setCacheSizeMb", new() { Name = "Set cache size Mb", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.AvgMinMax)] } },
                        { "taskQueueCount", new() { Name = "Task queue count", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.AvgMinMax)] } },
                        { "taskExecutedCount", new() { Name = "Executed task count", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.AvgMinMax)] } },
                        { "taskPersistedQueueCount", new() { Name = "Persisted task queue count", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.AvgMinMax)] } },
                        { "taskPersistedExecutedCount", new() { Name = "Persisted executed task count", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.AvgMinMax)] } },
                    },
            },
            new() {
                Name = "IO",
                Key = _ioLogKey,
                FileInterval = FileInterval.Day,
                EnableLog = _enableIoLog,
                EnableStatistics = _enableIoLogStatistics,
                EnableLogTextFormat = false,
                ResolutionRowStats = 4,
                FirstDayOfWeek = DayOfWeek.Monday,
                MaxAgeOfLogFilesInDays = 10,
                MaxTotalSizeOfLogFilesInMb = 100,
                Compressed = false,
                Properties = {
                        { "writtenKb", new() { Name = "Writtes Kb", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.CountSumAvgMinMax)] } },
                        { "readKb", new() { Name = "Reads Kb", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.CountSumAvgMinMax)] } },
                        { "writtenKbDisk", new() { Name = "Read count", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.Sum)] } },
                        { "readKbDisk", new() { Name = "Disk Writes", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.CountSumAvgMinMax)] } },
                    },
            },
        ];
        return _settings;
    }

    public bool LoggingSystem => _enableSystemLog || _enableSystemLogStatistics;
    public bool LoggingTransactionsOrActions => _enableTransactionLog || _enableActionLog || _enableTransactionLogStatistics || _enableActionLogStatistics;
    public bool LoggingTransactions => _enableTransactionLog || _enableTransactionLogStatistics;
    public bool LoggingActions => _enableActionLog || _enableActionLogStatistics;
    public bool LoggingQueries => _enableSystemQueryLog || _enableSystemQueryLogStatistics;
    public bool LoggingTask => _enableTaskLog || _enableTaskLogStatistics;
    public bool LoggingTaskBatch => _enableTaskBatchLog || _enableTaskBatchLogStatistics;
    public bool LoggingMetrics => _enableMetricsLog || _enableMetricsLogStatistics;
    public bool LoggingAny => _enableSystemLog || _enableSystemLogStatistics || _enableSystemQueryLog || _enableSystemQueryLogStatistics || _enableTransactionLog || _enableTransactionLogStatistics || _enableActionLog || _enableActionLogStatistics || _enableTaskLog || _enableTaskLogStatistics || _enableTaskBatchLog || _enableTaskBatchLogStatistics || _enableMetricsLog || _enableMetricsLogStatistics;

    public int MinDurationMsBeforeLogging { get; set; } = 0; // in milliseconds
    FileKeyUtility _fileKeys;
    public StoreLogger(IIOProvider io, FileKeyUtility fileKeys, Datamodel? datamodel) {
        _io = io;
        _datamodel = datamodel;
        _fileKeys = fileKeys;
        _logStore = new LogStore(_io, getSettings(), fileKeys);
    }
    void reloadLogsWithNewSettings() {
        _logStore?.Dispose();
        _logStore = new LogStore(_io, getSettings(), _fileKeys);
    }

    public void RecordSystem(SystemLogEntryType type, string text, string? details = null) {
        if (!LoggingSystem) return;
        LogEntry entry = new();
        entry.Values.Add("type", type.ToString());
        entry.Values.Add("text", text);
        entry.Values.Add("details", details == null ? string.Empty : details);
        _logStore?.Record(_systemLogKey, entry);
        _logStore?.FlushToDiskNow(_systemLogKey);
    }
    public void RecordQuery(string query, TimeSpan duration, int resultCount, Metrics metrics) {
        if (!LoggingQueries) return;
        if (duration.TotalMilliseconds < MinDurationMsBeforeLogging) return;
        LogEntry entry = new();
        entry.Values.Add("query", query);
        entry.Values.Add("duration", duration.TotalMilliseconds);
        entry.Values.Add("resultCount", resultCount);
        entry.Values.Add("nodeCount", metrics.NodeCount);
        entry.Values.Add("uniqueNodeCount", metrics.UniqueNodeCount);
        entry.Values.Add("diskReads", metrics.DiskReads);
        entry.Values.Add("nodesReadFromDisk", metrics.NodesReadFromDisk);
        _logStore?.Record(_queryLogKey, entry);
    }
    public void RecordTransaction(long transactionId, TimeSpan duration, int actionCount, int primitiveActionCount, bool diskFlush) {
        if (!LoggingTransactions) return;
        LogEntry entry = new();
        entry.Values.Add("transactionId", transactionId);
        entry.Values.Add("duration", duration.TotalMilliseconds);
        entry.Values.Add("actionCount", actionCount);
        entry.Values.Add("primitiveActionCount", primitiveActionCount);
        entry.Values.Add("diskFlush", diskFlush ? "Yes" : "-");
        _logStore?.Record(_transactionLogKey, entry);
    }
    public void RecordAction(long transactionId, string operation, string details) {
        if (!LoggingActions) return;
        LogEntry entry = new();
        entry.Values.Add("transactionId", transactionId);
        entry.Values.Add("operation", operation);
        entry.Values.Add("details", details);
        _logStore?.Record(_actionLogKey, entry);
    }
    public void RecordTask(string taskTypeName, bool success, Guid batchId, string taskId, string details) {
        if (!LoggingTask) return;
        LogEntry entry = new();
        entry.Values.Add("taskTypeName", taskTypeName);
        entry.Values.Add("success", success ? "Success" : "Error");
        entry.Values.Add("batchId", batchId.ToString());
        entry.Values.Add("taskId", taskId);
        entry.Values.Add("details", details);
        _logStore?.Record(_taskLogKey, entry);
    }
    public void RecordTaskBatch(Guid id, BatchTaskResult batchResult) {
        if (!LoggingTaskBatch) return;
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
    public void RecordMetrics(StoreMetrics metrics) {
        if (!LoggingMetrics) return;
        LogEntry entry = new();
        entry.Values.Add("cpuUsagePercentage", (int)(metrics.CpuUsage * 100));
        entry.Values.Add("memUsageMb", (int)((double)metrics.MemUsage / (1024d * 1024d)));
        entry.Values.Add("queryCount", unchecked((int)metrics.QueryCount));
        entry.Values.Add("actionCount", unchecked((int)metrics.ActionCount));
        entry.Values.Add("transactionCount", unchecked((int)metrics.TransactionCount));
        entry.Values.Add("nodeCount", metrics.NodeCount);
        entry.Values.Add("relationCount", metrics.RelationCount);
        entry.Values.Add("nodeCacheCount", metrics.NodeCacheCount);
        entry.Values.Add("nodeCacheSizeMb", (int)(metrics.NodeCacheSize / 1024 / 1024));
        entry.Values.Add("setCacheCount", metrics.SetCacheCount);
        entry.Values.Add("setCacheSizeMb", (int)(metrics.SetCacheSize / 1024 * 1024));
        entry.Values.Add("taskQueueCount", metrics.TasksQueued);
        entry.Values.Add("taskPersistedQueueCount", metrics.TasksPersistedQueued);
        entry.Values.Add("taskExecutedCount", metrics.TasksExecuted);
        entry.Values.Add("taskPersistedExecutedCount", metrics.TasksPersistedExecuted);
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
        if (logKey == _systemLogKey) _enableSystemLog = enable;
        else if (logKey == _queryLogKey) _enableSystemQueryLog = enable;
        else if (logKey == _transactionLogKey) _enableTransactionLog = enable;
        else if (logKey == _actionLogKey) _enableActionLog = enable;
        else if (logKey == _taskLogKey) _enableTaskLog = enable;
        else if (logKey == _taskbatchLogKey) _enableTaskBatchLog = enable;
        else if (logKey == _metricsLogKey) _enableMetricsLog = enable;
        else throw new Exception($"Unknown log key: {logKey}");
        reloadLogsWithNewSettings();
    }
    public void EnableStatistics(string logKey, bool enable) {
        if (logKey == _systemLogKey) _enableSystemLogStatistics = enable;
        else if (logKey == _queryLogKey) _enableSystemQueryLogStatistics = enable;
        else if (logKey == _transactionLogKey) _enableTransactionLogStatistics = enable;
        else if (logKey == _actionLogKey) _enableActionLogStatistics = enable;
        else if (logKey == _taskLogKey) _enableTaskLogStatistics = enable;
        else if (logKey == _taskbatchLogKey) _enableTaskBatchLogStatistics = enable;
        else if (logKey == _metricsLogKey) _enableMetricsLogStatistics = enable;
        else throw new Exception($"Unknown log key: {logKey}");
        reloadLogsWithNewSettings();
    }
    public bool IsLogEnabled(string logKey) {
        if (logKey == _systemLogKey) return _enableSystemLog;
        else if (logKey == _queryLogKey) return _enableSystemQueryLog;
        else if (logKey == _transactionLogKey) return _enableTransactionLog;
        else if (logKey == _actionLogKey) return _enableActionLog;
        else if (logKey == _taskLogKey) return _enableTaskLog;
        else if (logKey == _taskbatchLogKey) return _enableTaskBatchLog;
        else if (logKey == _metricsLogKey) return _enableMetricsLog;
        else throw new Exception($"Unknown log key: {logKey}");        
    }
    public bool IsStatisticsEnabled(string logKey) {
        if (logKey == _systemLogKey) return _enableSystemLogStatistics;
        else if (logKey == _queryLogKey) return _enableSystemQueryLogStatistics;
        else if (logKey == _transactionLogKey) return _enableTransactionLogStatistics;
        else if (logKey == _actionLogKey) return _enableActionLogStatistics;
        else if (logKey == _taskLogKey) return _enableTaskLogStatistics;
        else if (logKey == _taskbatchLogKey) return _enableTaskBatchLogStatistics;
        else if (logKey == _metricsLogKey) return _enableMetricsLogStatistics;
        else throw new Exception($"Unknown log key: {logKey}");
    }

    public long GetTotalFileSize() {
        long totalSize = 0;
        totalSize += _logStore.GetFileSize(_taskLogKey);
        totalSize += _logStore.GetFileSize(_taskbatchLogKey);
        totalSize += _logStore.GetFileSize(_systemLogKey);
        totalSize += _logStore.GetFileSize(_queryLogKey);
        totalSize += _logStore.GetFileSize(_transactionLogKey);
        totalSize += _logStore.GetFileSize(_actionLogKey);
        totalSize += _logStore.GetFileSize(_metricsLogKey);
        return totalSize;
    }

}
