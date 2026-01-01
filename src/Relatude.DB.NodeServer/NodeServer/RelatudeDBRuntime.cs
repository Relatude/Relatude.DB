using Relatude.DB.Nodes;

namespace Relatude.DB.NodeServer;
/// <summary>
/// A static class to hold the current RelatudeDBServer instance for easy access in contexts where dependency injection is not available.
/// </summary>
public static class RelatudeDBRuntime {
    static RelatudeDBServer? _current;
    static public RelatudeDBServer Server => _current ?? throw new InvalidOperationException("RelatudeDBServerContext not initialized. ");
    static public bool IsInitialized => _current != null;
    static public void Initialize(RelatudeDBServer server) {
        if (_current != null) throw new InvalidOperationException("RelatudeDBServerContext already initialized. ");
        _current = server ?? throw new ArgumentNullException(nameof(server));
    }
}
public class RelatudeDBContext() {
    public RelatudeDBServer Server => RelatudeDBRuntime.Server;
    public NodeStore Database {
        get {
            var store = Server.DefaultContainer?.Store;
            if (store == null) throw new InvalidOperationException("Database store is not configured or not ready. ");
            return store;
        }
    }
}
