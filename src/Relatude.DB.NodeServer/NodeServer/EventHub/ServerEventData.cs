namespace Relatude.DB.NodeServer.EventHub;
public class ServerEventData {
    public ServerEventData(string eventName, object? payload, TimeSpan? timeSpan = null) {
        EventName = eventName;
        Timestamp = DateTime.UtcNow;
        MaxAge = timeSpan ?? TimeSpan.FromMinutes(5);
        Payload = payload;
    }
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime Timestamp { get; }
    public TimeSpan MaxAge { get; }
    public string EventName { get; }
    public object? Payload;
}

