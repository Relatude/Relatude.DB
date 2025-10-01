using Microsoft.AspNetCore.Mvc.Diagnostics;

namespace Relatude.DB.NodeServer.EventHub;
public class EventData<TEvenData>(string name, TEvenData data, TimeSpan? maxAge = null) : IEventData {
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime Timestamp { get; } = DateTime.UtcNow;
    public TimeSpan MaxAge { get; set; } = maxAge ?? TimeSpan.FromSeconds(60);
    public string Name { get; } = name;
    public TEvenData Data { get; } = data;
}
public class EmptyEventData(string name, TimeSpan? maxAge = null) : IEventData {
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime Timestamp { get; } = DateTime.UtcNow;
    public TimeSpan MaxAge { get; set; } = maxAge ?? TimeSpan.FromSeconds(60);
    public string Name { get; } = name;
}

public class EventDataBuilder<TSubscriptionContext, TEventData>(string name, Func<EventSubscription<TSubscriptionContext>, TEventData> data, TimeSpan? maxAge = null) {
    public TimeSpan? MaxAge { get; } = maxAge;
    public Func<EventSubscription<TSubscriptionContext>, TEventData> BuildEventData { get; } = data;
    public string Name { get; } = name;
}

public interface IEventData {
    public Guid Id { get; }
    public DateTime Timestamp { get; }
    public TimeSpan MaxAge { get; set; }
    public string Name { get; }
}

