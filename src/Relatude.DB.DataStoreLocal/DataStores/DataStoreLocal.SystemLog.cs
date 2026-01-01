using Relatude.DB.DataStores.SimpleTracer;
using Relatude.DB.IO;
using Relatude.DB.Tasks;
using Relatude.DB.Transactions;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
namespace Relatude.DB.DataStores;

public sealed partial class DataStoreLocal : IDataStore {
    SimpleSystemLogTracer _tracer = new();
    public void LogInfo(string text, string? details = null, bool replace = false) => Log(SystemLogEntryType.Info, text, details, replace);
    public void LogWarning(string text, string? details = null) => Log(SystemLogEntryType.Warning, text, details);
    static object _consoleColorLock = new();
    static object _criticalLogLock = new();
    public void Log(SystemLogEntryType type, string text, string? details = null, bool replace = false) {
        try {
            if (_settings.WriteSystemLogConsole) {
                lock (_consoleColorLock) {
                    var originalColor = Console.ForegroundColor;
                    if (replace && Console.CursorTop > 0) Console.SetCursorPosition(0, Console.CursorTop - 1);
                    Console.ForegroundColor = ConsoleColor.DarkCyan;
                    Console.Write("relatude.db ");
                    var color = type switch {
                        SystemLogEntryType.Info => ConsoleColor.DarkGreen,
                        SystemLogEntryType.Warning => ConsoleColor.DarkYellow,
                        SystemLogEntryType.Error => ConsoleColor.DarkRed,
                        SystemLogEntryType.Backup => ConsoleColor.DarkMagenta,
                        _ => ConsoleColor.Gray
                    };
                    Console.ForegroundColor = color;
                    Console.Write(type.ToString().ToLower());
                    Console.ForegroundColor = originalColor;
                    Console.Write(": ");
                    Console.WriteLine(text + (details == null ? null : Environment.NewLine + details));
                }
            }
        } catch { }
        try {
            _tracer.Trace(type, text, details, replace);
        } catch { }
        try {
            if (Logger.LoggingSystem) {
                Logger.RecordSystem(type, text, details);
            }
        } catch (Exception ex) {
            try {
                Console.WriteLine("Error in system file log: " + ex.Message);
            } catch { }
        }
    }
    void logLine___________________________() => Log(SystemLogEntryType.Info, "--------------------------------------");
    public void LogError(string description, Exception error) {
        var sb = new StringBuilder();
        buildErrorLog(sb, error);
        Log(SystemLogEntryType.Error, description, sb.ToString());
    }
    Exception createCriticalErrorAndSetDbToErrorState(string description, Exception error, TransactionData? transaction = null) {
        try {
            logError(description, error, transaction, true);
        } catch { }
        _state = Common.DataStoreState.Error;
        return new Exception("Critical error occurred. " + description, error);
    }
    void logError(string description, Exception error) {
        logError(description, error, null, false);
    }
    void logError(string description, Exception error, TransactionData? transaction = null, bool isCritical = false) {
        var sb = new StringBuilder();
        try {
            buildErrorLog(sb, error);
            sb.AppendLine("--- Callstack:");
            var callstack = new System.Diagnostics.StackTrace();
            sb.AppendLine(callstack.ToString());
            if (transaction != null) {
                sb.AppendLine("--- Transaction:");
                var n = 0;
                foreach (var action in transaction.Actions) {
                    sb.AppendLine(action.ToString());
                    if (++n > 100) {
                        sb.AppendLine("... and " + (transaction.Actions.Count - n) + " other actions");
                        break;
                    }
                }
            }
        } catch { }
        if (isCritical) {
            try {
                writeCriticalSystemErrorTextFile(description, sb.ToString());
            } catch { }
        }
        Log(SystemLogEntryType.Error, description, sb.ToString());
    }
    void writeCriticalSystemErrorTextFile(string description, string? details = null) {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("#######START CRITICAL ERROR LOG########");
        sb.AppendLine();
        sb.AppendLine(DateTime.UtcNow.ToString("o") + " ");
        sb.AppendLine(description);
        if (details != null) sb.AppendLine(details);
        try {
            var metrics = DequeMetrics();
            sb.AppendLine();
            sb.AppendLine("Metrics: ");
            sb.AppendLine(JsonSerializer.Serialize(metrics));
        } catch { }
        sb.AppendLine();
        sb.AppendLine("#######END CRITICAL ERROR LOG########");
        sb.AppendLine();
        sb.AppendLine();
        lock (_criticalLogLock) {
            var fileKey = _fileKeys.CriticalErrorLogFileKey;
            var io = _ioLog;
            using var stream = io.OpenAppend(fileKey);
            stream.WriteUTF8StringNoLengthPrefix(sb.ToString());
        }
    }
    void buildErrorLog(StringBuilder sb, Exception error) {
        sb.AppendLine(error.GetType().FullName + ": " + error.Message);
        sb.AppendLine(error.StackTrace);
        if (error.InnerException != null) {
            sb.AppendLine("--- Inner exception:");
            buildErrorLog(sb, error.InnerException);
        }
    }

    public TraceEntry[] GetSystemTrace(int skip, int take) => _tracer.GetEntries(skip, take);
    public DateTime GetLatestSystemTraceTimestamp() => _tracer.GetLatest();
    CpuMonitor _cpuMonitorMetrics = new();
    public StoreMetrics DequeMetrics() {
        var metrics = new StoreMetrics() {
            MemUsage = Process.GetCurrentProcess().WorkingSet64,
            CpuUsage = _cpuMonitorMetrics.DequeCpuUsage(),
            QueryCount = _noQueriesSinceLastMetric,
            ActionCount = _noActionsSinceLastMetric,
            TransactionCount = _noTransactionsSinceLastMetric,
            NodeCount = _nodes.Count,
            RelationCount = _relations.TotalCount(),
            NodeCacheCount = _nodes.CacheCount,
            NodeCacheSize = _nodes.CacheSize,
            SetCacheCount = _sets.CacheCount,
            SetCacheSize = _sets.CacheSize,
            TasksQueued = TaskQueue.Count([BatchState.Pending, BatchState.Running]),
            TasksPersistedQueued = TaskQueuePersisted.Count([BatchState.Pending, BatchState.Running]),
        };
        _noQueriesSinceLastMetric = 0;
        _noActionsSinceLastMetric = 0;
        _noTransactionsSinceLastMetric = 0;
        return metrics;
    }

}

