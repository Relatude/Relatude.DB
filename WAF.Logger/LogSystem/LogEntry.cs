
namespace WAF.LogSystem;
public class LogEntry {
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public Dictionary<string, object> Values { get; set; } = [];
}
