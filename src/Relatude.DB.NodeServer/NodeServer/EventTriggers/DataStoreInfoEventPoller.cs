using Relatude.DB.Common;
using Relatude.DB.NodeServer.EventHub;

namespace Relatude.DB.NodeServer.EventTriggers;

public class DataStoreInfoEventPoller : IEventPoller {
    public string EventName => "DataStoreInfo";
    public EventDataFactory[]? Poll(RelatudeDBServer server, string?[] filters, bool onlyOnChange, out int msNextCollect) {
        List<EventDataFactory> events = [];
        msNextCollect = 1000;
        foreach (var filter in filters) {
            if (Guid.TryParse(filter, out Guid containerId)) {
                if (server.Containers.TryGetValue(containerId, out var container)) {
                    if (container.IsOpenOrOpening() && container.Datastore != null) {
                        var info = container.Datastore.GetInfo();
                        events.Add(new(info, filter));
                    }
                }
            }
        }
        return events.Count > 0 ? events.ToArray() : null;
    }
}
