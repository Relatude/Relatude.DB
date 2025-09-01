using System.Text;
using Relatude.DB.IO;
using Relatude.DB.Transactions;
namespace Relatude.DB.DataStores;
public sealed partial class DataStoreLocal : IDataStore {
    object _logFileLock = new();
    public void Log(string description, bool isError = false) {
        try {
            if (_logCallback != null) _logCallback(description);
        } catch (Exception ex) {
            Console.WriteLine("Error in log callback: " + ex.Message);
        }
        try {
            if (_settings.WriteSystemLogConsole) Console.WriteLine(description);
        } catch (Exception ex) {
            Console.WriteLine("Error in console log: " + ex.Message);
        }
        try {
            if (_settings.EnableSystemLog && (isError || !_settings.OnlyLogErrorsToSystemLog)) {
                var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
                var logtext = timestamp + " - " + description + Environment.NewLine;
                DateOnly dt = DateOnly.FromDateTime(DateTime.UtcNow);
                lock (_logFileLock) {
                    var fileKey = _fileKeys.SystemLog_GetFileKey(dt);
                    using var file = _io.OpenAppend(fileKey);
                    file.WriteUTF8StringNoLengthPrefix(logtext);
                }
            }
        } catch (Exception ex) {
            Console.WriteLine("Error in system file log: " + ex.Message);
        }
    }
    void deleteOldSystemLogFiles() {
        var logFiles = _fileKeys.SystemLog_GetAllFileKeys(_io);
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (var file in logFiles) {
            var dt = _fileKeys.SystemLog_GetFileDateTimeFromFileKey(file);
            if (dt.AddDays(_settings.DaysToKeepSystemLog) < today) {
                _io.DeleteIfItExists(file);
            }
        }
    }
    void logLine___________________________() => Log("--------------------------------------");
    public void LogError(string description, Exception error) {
        var sb = new StringBuilder();
        sb.Append("ERROR: ");
        sb.AppendLine(description);
        buildErrorLog(sb, error);
        Log(sb.ToString(), true);
    }
    void logCriticalTransactionError(string description, Exception error, TransactionData transaction) {
        var sb = new StringBuilder();
        sb.Append("ERROR: ");
        sb.AppendLine(description);
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
        Log(sb.ToString());
    }
    void logCriticalError(string description, Exception error) {
        var sb = new StringBuilder();
        sb.Append("ERROR: ");
        sb.AppendLine(description);
        buildErrorLog(sb, error);
        sb.AppendLine("--- Callstack:");
        var callstack = new System.Diagnostics.StackTrace();
        sb.AppendLine(callstack.ToString());
        Log(sb.ToString());
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

