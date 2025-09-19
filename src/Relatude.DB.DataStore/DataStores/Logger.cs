using Relatude.DB.Datamodels;
using Relatude.DB.IO;
using Relatude.DB.Logging;
using Relatude.DB.Logging.Statistics;
using Relatude.DB.Query;
namespace Relatude.DB.DataStores;
public enum SystemLogEntryType {
    Info,
    Warning,
    Error,
}
public class Logger : IDisposable {

    static string _systemLogKey = "system";
    static string _queryLogKey = "query";
    static string _transactionLogKey = "transaction";
    static string _actionLogKey = "action";
    static string _taskqueueLogKey = "taskqueue";
    static string _taskbatchqueueLogKey = "taskbatchqueue";
    static string _statusLogKey = "status";

    readonly IIOProvider _io;
    readonly FileKeyUtility _fileKeys;
    LogStore _logStore;
    readonly Datamodel _datamodel;
    public LogStore LogStore {
        get {
            return _logStore;
        }
    }
    List<LogSettings> getSettings() {
        return [
            new() {
                Name = "System",
                Key = _systemLogKey,
                FileNamePrefix = _fileKeys.QueryLog_GetFilePrefix(),
                FileNameDelimiter = _fileKeys.QueryLog_GetFileDelimiter(),
                FileNameExtension = _fileKeys.QueryLog_GetFileExtension(),
                FileInterval = FileResolution.Day,
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
                        { "text", new() { Name = "Text", DataType = LogDataType.Double } },
                        { "details", new() { Name = "Details",DataType = LogDataType.String, } },
                },
            },
            new() {
                Name = "Queries",
                Key = _queryLogKey,
                FileNamePrefix = _fileKeys.QueryLog_GetFilePrefix(),
                FileNameDelimiter = _fileKeys.QueryLog_GetFileDelimiter(),
                FileNameExtension = _fileKeys.QueryLog_GetFileExtension(),
                FileInterval = FileResolution.Day,
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
                FileNamePrefix = _fileKeys.QueryLog_GetFilePrefix(),
                FileNameDelimiter = _fileKeys.QueryLog_GetFileDelimiter(),
                FileNameExtension = _fileKeys.QueryLog_GetFileExtension(),
                FileInterval = FileResolution.Day,
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
                FileNamePrefix = _fileKeys.QueryLog_GetFilePrefix(),
                FileNameDelimiter = _fileKeys.QueryLog_GetFileDelimiter(),
                FileNameExtension = _fileKeys.QueryLog_GetFileExtension(),
                FileInterval = FileResolution.Day,
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
                Name = "TaskQue",
                Key = _taskqueueLogKey,
                FileNamePrefix = _fileKeys.QueryLog_GetFilePrefix(),
                FileNameDelimiter = _fileKeys.QueryLog_GetFileDelimiter(),
                FileNameExtension = _fileKeys.QueryLog_GetFileExtension(),
                FileInterval = FileResolution.Day,
                EnableLog = true,
                EnableStatistics = true,
                EnableLogTextFormat = false,
                ResolutionRowStats = 4,
                FirstDayOfWeek = DayOfWeek.Monday,
                MaxAgeOfLogFilesInDays = 10,
                MaxTotalSizeOfLogFilesInMb = 100,
                Compressed = false,
                Properties = {
                        { "id", new() { Name = "Id", DataType = LogDataType.String, }},
                        { "taskName", new() { Name = "Task name", DataType = LogDataType.String, Statistics = [new (StatisticsType.UniqueCountWithValues)] }},
                        { "created", new() { Name = "Created", DataType = LogDataType.String } },
                    },
            },
            new() {
                Name = "TaskBatchQue",
                Key = _taskbatchqueueLogKey,
                FileNamePrefix = _fileKeys.QueryLog_GetFilePrefix(),
                FileNameDelimiter = _fileKeys.QueryLog_GetFileDelimiter(),
                FileNameExtension = _fileKeys.QueryLog_GetFileExtension(),
                FileInterval = FileResolution.Day,
                EnableLog = true,
                EnableStatistics = true,
                EnableLogTextFormat = false,
                ResolutionRowStats = 4,
                FirstDayOfWeek = DayOfWeek.Monday,
                MaxAgeOfLogFilesInDays = 10,
                MaxTotalSizeOfLogFilesInMb = 100,
                Compressed = false,
                Properties = {
                        { "id", new() { Name = "Id", DataType = LogDataType.String, }},
                        { "taskName", new() { Name = "Task name", DataType = LogDataType.String, Statistics = [new (StatisticsType.UniqueCountWithValues)] }},
                        { "created", new() { Name = "Created", DataType = LogDataType.String } },
                        { "duration", new() { Name = "Duration", DataType = LogDataType.Double, Statistics = [new (StatisticsType.CountSumAvgMinMax)] } },
                        { "result", new() { Name = "Result", DataType = LogDataType.String }},
                        { "details", new() { Name = "Details", DataType = LogDataType.String }},
                    },
            },
            new() {
                Name = "Status",
                Key = _statusLogKey,
                FileNamePrefix = _fileKeys.QueryLog_GetFilePrefix(),
                FileNameDelimiter = _fileKeys.QueryLog_GetFileDelimiter(),
                FileNameExtension = _fileKeys.QueryLog_GetFileExtension(),
                FileInterval = FileResolution.Day,
                EnableLog = true,
                EnableStatistics = true,
                EnableLogTextFormat = false,
                ResolutionRowStats = 4,
                FirstDayOfWeek = DayOfWeek.Monday,
                MaxAgeOfLogFilesInDays = 10,
                MaxTotalSizeOfLogFilesInMb = 100,
                Compressed = false,
                Properties = {
                        { "nodeCacheCount", new() { Name = "Node cache count", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.AvgMinMax)] } },
                        { "nodeCacheSize", new() { Name = "Node cache size", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.AvgMinMax)] } },
                        { "setCacheSize", new() { Name = "Set cache size", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.AvgMinMax)] } },
                        { "setCacheCount", new() { Name = "Set cache count", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.AvgMinMax)] } },
                        { "taskQueueCount", new() { Name = "Task queue count", DataType = LogDataType.Integer, Statistics = [new (StatisticsType.AvgMinMax)] } },
                    },
            },
        ];
    }

    public bool EnableSystemLog = true; // only system log, is enabled by default
    public bool EnableSystemLogStatistics = true; // all statistics enabled by default for all logs...
    public bool EnableSystemQueryLog = false;
    public bool EnableSystemQueryLogStatistics = true;
    public bool EnableTransactionLog = false;
    public bool EnableTransactionLogStatistics = true;
    public bool EnableActionLog = false;
    public bool EnableActionLogStatistics = true;
    public bool EnableTaskQueueLog = false;
    public bool EnableTaskQueueLogStatistics = true;
    public bool EnableTaskBatchQueueLog = false;
    public bool EnableTaskBatchQueueLogStatistics = true;
    public bool EnableStatusLog = false;
    public bool EnableStatusLogStatistics = true;

    public bool LoggingSystem => EnableSystemLog || EnableSystemLogStatistics;
    public bool LoggingTransactionsOrActions => EnableTransactionLog || EnableActionLog || EnableTransactionLogStatistics || EnableActionLogStatistics;
    public bool LoggingTransactions => EnableTransactionLog || EnableTransactionLogStatistics;
    public bool LoggingActions => EnableActionLog || EnableActionLogStatistics;
    public bool LoggingQueries => EnableSystemQueryLog || EnableSystemQueryLogStatistics;
    public bool LoggingTaskQueue => EnableTaskQueueLog || EnableTaskQueueLogStatistics;
    public bool LoggingTaskBatchQueue => EnableTaskBatchQueueLog || EnableTaskBatchQueueLogStatistics;
    public bool LoggingStatus => EnableStatusLog || EnableStatusLogStatistics;
    public bool LoggingAny => EnableSystemLog || EnableSystemLogStatistics || EnableSystemQueryLog || EnableSystemQueryLogStatistics || EnableTransactionLog || EnableTransactionLogStatistics || EnableActionLog || EnableActionLogStatistics || EnableTaskQueueLog || EnableTaskQueueLogStatistics || EnableTaskBatchQueueLog || EnableTaskBatchQueueLogStatistics || EnableStatusLog || EnableStatusLogStatistics;


    public int MinDurationMsBeforeLogging { get; set; } = 0; // in milliseconds

    public Logger(IIOProvider io, FileKeyUtility fileKeys, Datamodel datamodel) {
        _fileKeys = fileKeys;
        _io = io;
        _datamodel = datamodel;
        _logStore = new LogStore(_io, getSettings());
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
    public void RecordTaskQueue(string id, string taskName, DateTime created, double durationMs, string result, string details) {
        if (!EnableTaskQueueLog) return;
        LogEntry entry = new();
        entry.Values.Add("id", id);
        entry.Values.Add("taskName", taskName);
        entry.Values.Add("created", created.ToString("o"));
        entry.Values.Add("duration", durationMs);
        entry.Values.Add("result", result);
        entry.Values.Add("details", details);
        _logStore?.Record(_taskqueueLogKey, entry);
    }
    public void RecordTaskBatchQueue(string id, string taskName, DateTime created, double durationMs, string result, string details) {
        if (!EnableTaskBatchQueueLog) return;
        LogEntry entry = new();
        entry.Values.Add("id", id);
        entry.Values.Add("taskName", taskName);
        entry.Values.Add("created", created.ToString("o"));
        entry.Values.Add("duration", durationMs);
        entry.Values.Add("result", result);
        entry.Values.Add("details", details);
        _logStore?.Record(_taskbatchqueueLogKey, entry);
    }
    public void RecordStatus(int nodeCacheCount, int nodeCacheSize, int setCacheCount, int setCacheSize, int taskQueueCount) {
        LogEntry entry = new();
        entry.Values.Add("nodeCacheCount", nodeCacheCount);
        entry.Values.Add("nodeCacheSize", nodeCacheSize);
        entry.Values.Add("setCacheCount", setCacheCount);
        entry.Values.Add("setCacheSize", setCacheSize);
        entry.Values.Add("taskQueueCount", taskQueueCount);
        _logStore?.Record(_statusLogKey, entry);
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
    public void Clear(string logKey) {
        if (_logStore == null) return;
        _logStore.DeleteLogAndStatistics(logKey);
    }
    public LogEntry[] ExtractLog(string logKey, DateTime from, DateTime to, int skip, int take, out int total) {
        if (_logStore == null) { total = 0; return []; }
        return _logStore.ExtractLog(logKey, from, to, skip, take, out total).ToArray();
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

    public void EnableLog(string logKey, bool enable) {
        switch (logKey) {
            case "system": EnableSystemLog = enable; break;
            case "query": EnableSystemQueryLog = enable; break;
            case "transaction": EnableTransactionLog = enable; break;
            case "action": EnableActionLog = enable; break;
            case "taskqueue": EnableTaskQueueLog = enable; break;
            case "taskbatchqueue": EnableTaskBatchQueueLog = enable; break;
            case "status": EnableStatusLog = enable; break;
            default: break;
        }
    }
    public void EnableStatistics(string logKey, bool enable) {
        switch (logKey) {
            case "system": EnableSystemLogStatistics = enable; break;
            case "query": EnableSystemQueryLogStatistics = enable; break;
            case "transaction": EnableTransactionLogStatistics = enable; break;
            case "action": EnableActionLogStatistics = enable; break;
            case "taskqueue": EnableTaskQueueLogStatistics = enable; break;
            case "taskbatchqueue": EnableTaskBatchQueueLogStatistics = enable; break;
            case "status": EnableStatusLogStatistics = enable; break;
            default: break;
        }
    }
    public bool IsLogEnabled(string logKey) {
        switch (logKey) {
            case "system": return EnableSystemLog;
            case "query": return EnableSystemQueryLog;
            case "transaction": return EnableTransactionLog;
            case "action": return EnableActionLog;
            case "taskqueue": return EnableTaskQueueLog;
            case "taskbatchqueue": return EnableTaskBatchQueueLog;
            case "status": return EnableStatusLog;
            default: return false;
        }
    }
    public bool IsStatisticsEnabled(string logKey) {
        switch (logKey) {
            case "system": return EnableSystemLogStatistics;
            case "query": return EnableSystemQueryLogStatistics;
            case "transaction": return EnableTransactionLogStatistics;
            case "action": return EnableActionLogStatistics;
            case "taskqueue": return EnableTaskQueueLogStatistics;
            case "taskbatchqueue": return EnableTaskBatchQueueLogStatistics;
            case "status": return EnableStatusLogStatistics;
            default: return false;
        }
    }
}
