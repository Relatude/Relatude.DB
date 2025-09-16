using Relatude.DB.Datamodels;
using Relatude.DB.IO;
using Relatude.DB.Logging;
using Relatude.DB.Logging.Statistics;
using Relatude.DB.Query;

namespace Relatude.DB.DataStores;
public class QueryLogger : IDisposable {
    static string _queryLogKey = "query";
    static string _transactionLogKey = "transaction";
    static string _actionLogKey = "action";
    static string _taskqueueLogKey = "taskqueue";
    static string _taskbatchqueueLogKey = "taskbatchqueue";
    static string _statusLogKey = "status";
    readonly IIOProvider _io;
    readonly FileKeyUtility _fileKeys;
    LogStore? _logStore;
    public LogStore LogStore {
        get {
            if (_logStore == null) throw new InvalidOperationException("Logging is not initialized.");
            return _logStore;
        }
    }
    List<LogSettings> getSettings() {
        return [
            new() {
                Name = "Queries",
                Key = _queryLogKey,
                FileNamePrefix = _fileKeys.QueryLog_GetFilePrefix(),
                FileNameDelimiter = _fileKeys.QueryLog_GetFileDelimiter(),
                FileNameExtension = _fileKeys.QueryLog_GetFileExtension(),
                FileInterval = FileResolution.Day,
                EnableLog = _enableDetails,
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
                EnableLog = _enableDetails,
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
                EnableLog = _enableDetails,
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
                EnableLog = _enableDetails,
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
                EnableLog = _enableDetails,
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
                EnableLog = _enableDetails,
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
    readonly Datamodel _datamodel;
    bool _enabled;
    public bool Enabled {
        get => _enabled;
        set {
            if (value && _logStore == null) _logStore = new LogStore(_io, getSettings());
            _enabled = value;
        }
    }
    public int MinDurationMsBeforeLogging { get; set; } = 0; // in milliseconds
    bool _enableDetails;
    public bool EnableDetails {
        get => _enableDetails;
        set {
            _enableDetails = value;
            bool orgValue = Enabled;
            Enabled = false;
            _logStore?.Dispose();
            _logStore = new LogStore(_io, getSettings());
            Enabled = orgValue;
        }
    }
    public QueryLogger(IIOProvider io, bool enabled, bool enableDetails, FileKeyUtility fileKeys, Datamodel datamodel) {
        _fileKeys = fileKeys;
        _enableDetails = enableDetails;
        _io = io;
        _datamodel = datamodel;
        Enabled = enabled;
    }
    public void RecordQuery(string query, double durationMs, int resultCount, Metrics metrics) {
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
        LogEntry entry = new();
        entry.Values.Add("transactionId", transactionId);
        entry.Values.Add("duration", duration);
        entry.Values.Add("actionCount", actionCount);
        entry.Values.Add("primitiveActionCount", primitiveActionCount);
        entry.Values.Add("diskFlush", diskFlush ? "Yes" : "-");
        _logStore?.Record(_transactionLogKey, entry);
    }
    public void RecordAction(long transactionId, string operation, string details) {
        LogEntry entry = new();
        entry.Values.Add("transactionId", transactionId);
        entry.Values.Add("operation", operation);
        entry.Values.Add("details", details);
        _logStore?.Record(_actionLogKey, entry);
    }
    public void RecordTaskQueue(string id, string taskName, DateTime created, double durationMs, string result, string details) {
        LogEntry entry = new();
        entry.Values.Add("id", id);
        entry.Values.Add("taskName", taskName);
        entry.Values.Add("created", created.ToString("o"));
        entry.Values.Add("duration", durationMs);
        entry.Values.Add("result", result);
        entry.Values.Add("details", details);
        _logStore?.Record(_taskqueueLogKey, entry);
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

    public void Clear() {
        if (_logStore == null) return;
        _logStore.DeleteLogAndStatistics(_queryLogKey);
        _logStore.DeleteLogAndStatistics(_transactionLogKey);
        _logStore.DeleteLogAndStatistics(_actionLogKey);
    }
    public LogEntry[] ExtractQueryLog(DateTime from, DateTime to, int skip, int take, out int total) {
        if (_logStore == null) {
            total = 0;
            return [];
        }
        return _logStore.ExtractLog(_queryLogKey, from, to, skip, take, out total).ToArray();
    }
    public LogEntry[] ExtractTransactionLog(DateTime from, DateTime to, int skip, int take, out int total) {
        if (_logStore == null) {
            total = 0;
            return [];
        }
        return _logStore.ExtractLog(_transactionLogKey, from, to, skip, take, out total).ToArray();
    }
    public LogEntry[] ExtractActionLog(DateTime from, DateTime to, int skip, int take, out int total) {
        if (_logStore == null) {
            total = 0;
            return [];
        }
        return _logStore.ExtractLog(_actionLogKey, from, to, skip, take, out total).ToArray();
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
}

