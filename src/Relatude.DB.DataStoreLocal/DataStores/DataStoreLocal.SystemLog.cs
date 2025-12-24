using Relatude.DB.DataStores.SimpleTracer;
using Relatude.DB.IO;
using Relatude.DB.Tasks;
using Relatude.DB.Transactions;
using System.Diagnostics;
using System.Text;
namespace Relatude.DB.DataStores;

public sealed partial class DataStoreLocal : IDataStore {
    SimpleSystemLogTracer _tracer = new();
    public void LogInfo(string text, string? details = null) => Log(SystemLogEntryType.Info, text, details);
    public void LogWarning(string text, string? details = null) => Log(SystemLogEntryType.Warning, text, details);
    static object _consoleColorLock = new();
    public void Log(SystemLogEntryType type, string text, string? details = null) {
        try {
            if (_settings.WriteSystemLogConsole) {
                lock (_consoleColorLock) {
                    var originalColor = Console.ForegroundColor;
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
            _tracer.Trace(type, text, details);
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
    void logCriticalTransactionError(string description, Exception error, TransactionData transaction) {
        var sb = new StringBuilder();
        buildErrorLog(sb, error);
        sb.AppendLine("--- Callstack:");
        var callstack = new System.Diagnostics.StackTrace();
        sb.AppendLine(callstack.ToString());
        sb.AppendLine("--- Transaction:");
        var n = 0;
        foreach (var action in transaction.Actions) {
            sb.AppendLine(action.ToString());
            if (++n > 100) {
                sb.AppendLine("... and " + (transaction.Actions.Count - n) + " other actions");
                break;
            }
        }
        Log(SystemLogEntryType.Error, description, sb.ToString());
    }
    void logCriticalError(string description, Exception error) {
        var sb = new StringBuilder();
        buildErrorLog(sb, error);
        sb.AppendLine("--- Callstack:");
        var callstack = new System.Diagnostics.StackTrace();
        sb.AppendLine(callstack.ToString());
        Log(SystemLogEntryType.Error, description, sb.ToString());
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
            TasksExecuted = TaskQueue.TaskExecutedSinceLastMetric(),
            TasksPersistedExecuted = TaskQueuePersisted.TaskExecutedSinceLastMetric(),
            TasksQueued = TaskQueue.TaskQueuedSinceLastMetric(),
            TasksPersistedQueued = TaskQueuePersisted.TaskQueuedSinceLastMetric(),
        };
        _noQueriesSinceLastMetric = 0;
        _noActionsSinceLastMetric = 0;
        _noTransactionsSinceLastMetric = 0;
        return metrics;
    }
}

