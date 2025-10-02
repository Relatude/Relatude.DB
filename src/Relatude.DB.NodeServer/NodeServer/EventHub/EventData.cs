using Microsoft.AspNetCore.Mvc.Diagnostics;

namespace Relatude.DB.NodeServer.EventHub;
internal class EventData<T>(string name, T data, TimeSpan? maxAge = null) : IEventData {
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime Timestamp { get; } = DateTime.UtcNow;
    public TimeSpan MaxAge { get; set; } = maxAge ?? TimeSpan.FromSeconds(60);
    public string Name { get; } = name;
    public T Data { get; } = data;
}

public interface IEventData {
    public Guid Id { get; }
    public DateTime Timestamp { get; }
    public TimeSpan MaxAge { get; set; }
    public string Name { get; }
}

