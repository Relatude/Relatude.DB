using Relatude.DB.Common;
using Relatude.DB.NodeServer.EventHub;
namespace Relatude.DB.NodeServer.EventTriggers;
public class ServerStatusEventTrigger : IEventPoller {
    Dictionary<Guid, DataStoreState> _last = [];
    public string EventName => "ServerStatus";
    public EventDataFactory[]? Poll(RelatudeDBServer server, string?[] filters, out int msNextCollect) {
        msNextCollect = 1000;
        var current = server.Containers.ToDictionary(c => c.Key, c => c.Value.Store != null ? c.Value.Store.State : DataStoreState.Closed);
        var noChange = current.OrderBy(kv => kv.Key).SequenceEqual(_last.OrderBy(kv => kv.Key));
        if (noChange) return null; // no change, no event
        msNextCollect = 1000;
        _last = current;
        return [new(current)];
    }
}
