using Relatude.DB.Datamodels;
using Relatude.DB.Nodes;

namespace Relatude.DB.NodeServer;
/// <summary>
/// A static class to hold the current RelatudeDBServer instance for easy access in contexts where dependency injection is not available.
/// </summary>
public static class RelatudeDBRuntime {
    static RelatudeDBServer? _server;
    static public void Initialize(RelatudeDBServer server) {
        if (_server != null) throw new InvalidOperationException("RelatudeDBServerContext already initialized. ");
        _server = server ?? throw new ArgumentNullException(nameof(server));
    }
    static public RelatudeDBServer Server => _server ?? throw new InvalidOperationException("RelatudeDBServerContext not initialized. ");
    static public NodeStore Database {
        get {
            var store = Server.DefaultContainer?.Store;
            if (store == null) throw new InvalidOperationException("Default database store is not configured or not ready. ");
            return store;
        }
    }
    static public bool IsInitialized => _server != null;
}

public class RelatudeDBContext() {
    public RelatudeDBServer Server => RelatudeDBRuntime.Server;
    public QueryContext Session { get; set; } = QueryContext.Anonymous;
    public NodeStore Database => RelatudeDBRuntime.Database;
}

