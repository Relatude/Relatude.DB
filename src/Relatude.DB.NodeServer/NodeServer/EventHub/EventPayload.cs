namespace Relatude.DB.NodeServer.EventHub;
public delegate object? EventPayloadProvider(string? filter, object? context);
public class EventPayload {
    public EventPayload(EventPayloadProvider getData, string? filter = null) {
        GetData = getData;
        Filter = filter;
    }
    public EventPayload(object data, string? filter = null) {
        GetData = (filter, context) => data;
        Filter = filter;
    }
    public EventPayloadProvider GetData { get; }
    public string? Filter { get; }
}
