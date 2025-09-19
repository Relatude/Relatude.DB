using System.Text;
using Relatude.DB.IO;
using Relatude.DB.Transactions;
namespace Relatude.DB.DataStores;
public sealed partial class DataStoreLocal : IDataStore {
    public void LogInfo(string text, string? details = null) => Log(SystemLogEntryType.Info, text, details);
    public void LogWarning(string text, string? details = null) => Log(SystemLogEntryType.Warning, text, details);
    public void Log(SystemLogEntryType type, string text, string? details = null) {
        try {
            if (_settings.WriteSystemLogConsole) Console.WriteLine(text + (details == null ? null : Environment.NewLine + details));
        } catch { }
        try {
            if (_settings.EnableSystemLog && (type == SystemLogEntryType.Error || !_settings.OnlyLogErrorsToSystemLog)) {
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
}

