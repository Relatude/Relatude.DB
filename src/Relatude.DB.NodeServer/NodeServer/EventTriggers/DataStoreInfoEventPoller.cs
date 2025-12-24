using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.NodeServer.EventHub;
using System.Diagnostics;

namespace Relatude.DB.NodeServer.EventTriggers;

public class DataStoreInfoEventPoller : IEventPoller {
    public string EventName => "DataStoreInfo";
    public EventDataFactory[]? Poll(RelatudeDBServer server, string?[] filters, bool onlyOnChange, out int msNextCollect) {
        List<EventDataFactory> events = [];
        msNextCollect = 1000;
        foreach (var filter in filters) {
            if (Guid.TryParse(filter, out Guid containerId)) {
                if (server.Containers.TryGetValue(containerId, out var container)) {
                    if (container.IsOpenOrOpening() && container.Store?.Datastore != null) {
                        var info = container.Store!.Datastore.GetInfo();
                        events.Add(new(info, filter));
                    } else {
                        var info = new DataStoreInfo() {
                            ProcessWorkingMemory = Process.GetCurrentProcess().WorkingSet64
                        };
                        events.Add(new(info, filter));
                    }
                }
            }
        }
        return events.Count > 0 ? events.ToArray() : null;
    }
}
