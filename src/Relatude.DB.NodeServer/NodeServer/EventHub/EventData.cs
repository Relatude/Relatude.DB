namespace Relatude.DB.NodeServer.EventHub;
public class EventData<K>(string name, K data, TimeSpan? maxAge = null) : IEventData {
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime Timestamp { get; } = DateTime.UtcNow;
    public TimeSpan MaxAge { get; set; } = maxAge ?? TimeSpan.FromSeconds(60);
    public string Name { get; } = name;
    public K Data { get; } = data;
}

public class EventDataBuilder<T, K>(string name, Func<EventSubscription<T>, K> data, TimeSpan? maxAge = null) {
    public TimeSpan? MaxAge { get; } = maxAge;
    public Func<EventSubscription<T>, K> Data { get; } = data;
    public string Name { get; } = name;
}

public interface IEventData
{
    public Guid Id { get; }
    public DateTime Timestamp { get; }
    public TimeSpan MaxAge { get; set; }
    public string Name { get; }
}