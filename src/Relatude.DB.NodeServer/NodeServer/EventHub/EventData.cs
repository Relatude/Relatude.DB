namespace Relatude.DB.NodeServer.EventHub;
public class EventData(string name, object data, TimeSpan? maxAge = null) {
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime Timestamp { get; } = DateTime.UtcNow;
    public TimeSpan MaxAge { get; set; } = maxAge ?? TimeSpan.FromSeconds(60);
    public string Name { get; } = name;
    public object Data { get; } = data;
}

public class EventDataBuilder(string name, Func<Guid, object> data, TimeSpan? maxAge = null) {
    public TimeSpan? MaxAge { get; } = maxAge;
    public Func<Guid, object> Data { get; } = data;
    public string Name { get; } = name;
}
