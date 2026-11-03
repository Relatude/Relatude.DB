using System.Text;
namespace Relatude.DB.IO;

public static class CriticalErrorLogFile {
    public const string EntryStart = "#######START CRITICAL ERROR LOG########";
    public const string EntryEnd = "#######END CRITICAL ERROR LOG########";
    static readonly object _writeLock = new();

    public static void Append(IIOProvider io, DateTime utc, string description, string? details = null) {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine(EntryStart);
        sb.AppendLine();
        sb.AppendLine(utc.ToString("o") + " ");
        sb.AppendLine(description);
        if (details != null) sb.AppendLine(details);
        sb.AppendLine();
        sb.AppendLine(EntryEnd);
        sb.AppendLine();
        sb.AppendLine();
        lock (_writeLock) {
            using var stream = io.OpenAppend(FileKeyUtility.CriticalErrorLogFileKey);
            stream.WriteUTF8StringNoLengthPrefix(sb.ToString());
        }
    }
}
