using Relatude.DB.Common;

namespace Relatude.DB.NodeServer.EventTriggers;

public class ServerStatusEventTrigger : IDisposable {
    RelatudeDBServer _server;
    Timer _pulseTimer;
    Dictionary<Guid, DataStoreStatus> _lastStatuses = [];
    public ServerStatusEventTrigger() {
        _pulseTimer = new Timer(_ => pulse(), null, 1000, 1000);
    }
    void pulse() {
        
    }
    bool updateIfChanged() {
        
    }
    public void Dispose() {
        _pulseTimer?.Dispose();
    }
}
