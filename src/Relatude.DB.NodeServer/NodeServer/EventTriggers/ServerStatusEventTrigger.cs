using Microsoft.AspNetCore.Hosting.Server;
using Relatude.DB.Common;

namespace Relatude.DB.NodeServer.EventTriggers;

public class ServerStatusEventTrigger : IDisposable {
    RelatudeDBServer _server;
    Timer _pulseTimer;
    Dictionary<Guid, DataStoreStatus> _lastStatuses = [];
    public ServerStatusEventTrigger( RelatudeDBServer server) {
        _server = server;
        _pulseTimer = new Timer(_ => pulse(), null, 1000, 1000);
    }
    void pulse() {
        
    }
    bool updateIfChanged() {
        _server.GetC
        return false;
    }
    public void Dispose() {
        _pulseTimer?.Dispose();
    }
}
