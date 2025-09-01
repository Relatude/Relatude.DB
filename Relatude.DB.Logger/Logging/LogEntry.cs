namespace Relatude.DB.Logging;
public class LogEntry {
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public Dictionary<string, object> Values { get; set; } = [];
}
