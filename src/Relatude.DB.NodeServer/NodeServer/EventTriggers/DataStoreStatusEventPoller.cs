using Relatude.DB.Common;
using Relatude.DB.NodeServer.EventHub;
using System.Net.NetworkInformation;

namespace Relatude.DB.NodeServer.EventTriggers;

public class DataStoreStatusEventPoller : IEventPoller {
    public string EventName => "DataStoreStatus";
    Dictionary<Guid, DataStoreStatus> _last = [];
    public EventDataFactory[]? Poll(RelatudeDBServer server, string?[] filters, bool onlyOnChange, out int msNextCollect) {
        List<EventDataFactory> events = [];
        msNextCollect = 100;
        foreach (var filter in filters) {
            if (Guid.TryParse(filter, out Guid containerId)) {
                if (server.Containers.TryGetValue(containerId, out var container)) {
                    if (container.IsOpenOrOpening() && container.Store?.Datastore != null) {
                        var status = container.Store.Datastore.GetStatus();
                        if (_last.TryGetValue(containerId, out var lastStatus)) {
                            if (onlyOnChange && status.Equals(lastStatus)) continue; // no change
                            _last[containerId] = status;
                        } else {
                            _last[containerId] = status;
                        }
                        events.Add(new(status, filter));
                    } else {
                        if (container.Store == null) {
                            _last[containerId] = new DataStoreStatus(DataStoreState.Closed, []);
                        } else _last.Remove(containerId);
                    }
                }
            }
        }
        return events.Count > 0 ? events.ToArray() : null;
    }
}
