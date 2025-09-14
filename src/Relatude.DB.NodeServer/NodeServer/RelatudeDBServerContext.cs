namespace Relatude.DB.NodeServer;
public static class RelatudeDBServerContext {
    static RelatudeDBServer? _current;
    static public RelatudeDBServer Current => _current ?? throw new InvalidOperationException("RelatudeDBServerContext not initialized. ");
    static public bool IsInitialized => _current != null;
    static public void Initialize(RelatudeDBServer server) {
        if (_current != null) throw new InvalidOperationException("RelatudeDBServerContext already initialized. ");
        _current = server ?? throw new ArgumentNullException(nameof(server));
    }
}
