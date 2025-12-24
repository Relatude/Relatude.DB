using Relatude.DB.NodeServer.EventHub;
namespace Relatude.DB.NodeServer.EventTriggers;

public class DataStoreTraceEventPoller : IEventPoller {
    public string EventName => "DataStoreTrace";
    Dictionary<Guid, DateTime> _last = [];
    public EventDataFactory[]? Poll(RelatudeDBServer server, string?[] filters, bool onlyOnChange, out int msNextCollect) {
        List<EventDataFactory> events = [];
        foreach (var filter in filters) {
            if (Guid.TryParse(filter, out Guid containerId)) {
                if (server.Containers.TryGetValue(containerId, out var container)) {
                    if (container.Store == null) continue;
                    var latest = container.Store.Datastore.GetLatestSystemTraceTimestamp();
                    if (_last.TryGetValue(containerId, out DateTime last)) {
                        if (onlyOnChange && latest <= last) continue; // no new trace entries
                    }
                    _last[containerId] = latest;
                    var trace = container.Store.Datastore.GetSystemTrace(0, 50);
                    events.Add(new(trace, filter));
                }
            }
        }
        msNextCollect = 100;
        if (events.Count == 0) {
            return null;
        } else {
            return [.. events];
        }
    }
}
