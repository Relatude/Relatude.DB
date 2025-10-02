namespace Relatude.DB.NodeServer.EventHub;
public delegate object? ProducePayload(EventContext context, string? filter);
public class EventDataFactory {
    public EventDataFactory(ProducePayload producePayload, string? filter = null) {
        _produce = producePayload;
        Filter = filter;
    }
    public EventDataFactory(object payload, string? filter = null) {
        _payload = payload;
        Filter = filter;
    }
    ProducePayload? _produce = null;
    object? _payload = null;
    public object? GetPayload(EventContext context) => _produce != null ? _produce(context, Filter) : _payload;
    public string? Filter { get; }
}
