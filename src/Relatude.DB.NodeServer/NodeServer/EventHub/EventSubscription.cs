namespace Relatude.DB.NodeServer.EventHub;
public class EventSubscription {
    public EventSubscription() {
        EventName = "";
        Filter = null;
    }
    public EventSubscription(string name, string? filter) {
        EventName = name;
        Filter = filter;
    }
    public string EventName { get; init; }
    public string? Filter { get; init; }
}
