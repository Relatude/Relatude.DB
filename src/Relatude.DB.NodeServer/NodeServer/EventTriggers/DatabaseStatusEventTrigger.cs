using Relatude.DB.Common;
using Relatude.DB.NodeServer.EventHub;

namespace Relatude.DB.NodeServer.EventTriggers;

public class DataStoreStatusEventTrigger : IEventPoller {
    public string EventName => "DataStoreStatus";
    Dictionary<Guid, DataStoreStatus> _last = [];
    public EventDataFactory[]? Poll(RelatudeDBServer server, string?[] filters, bool onlyOnChange, out int msNextCollect) {
        List<EventDataFactory> events = [];
        msNextCollect = 100;
        foreach (var filter in filters) {
            if (Guid.TryParse(filter, out Guid containerId)) {
                if (server.Containers.TryGetValue(containerId, out var container)) {
                    if (container.IsOpenOrOpening() && container.Datastore != null) {
                        var status = container.Datastore.GetStatus();
                        if (_last.TryGetValue(containerId, out var lastStatus)) {
                            if (onlyOnChange && status.Equals(lastStatus)) continue; // no change
                            _last[containerId] = status;
                        } else {
                            _last[containerId] = status;
                        }
                        events.Add(new(status, filter));
                    }
                }
            }
        }
        return events.Count > 0 ? events.ToArray() : null;
    }
}
