namespace Relatude.DB.NodeServer.EventHub;
public class EventSubscription(string name, string? filter) {
    public string EventName { get; } = name;
    public string? Filter { get; } = filter;
}
