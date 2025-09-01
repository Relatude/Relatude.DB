using System.Net;
using System.Net.Sockets;
using Relatude.DB.DataStores;

namespace Relatude.DB.NodeServer.Sockets;
public class SocketServer : IDisposable {
    Socket _listener;
    readonly Dictionary<Guid, Client> _clients = new();
    readonly Func<IDataStore>? _onFirstConnect;
    readonly Action<IDataStore?>? _onDispose;
    IDataStore? _dataStore;
    SocketServerConfiguration _config;
    object _lock = new();
    public SocketServer(IDataStore dataStore, SocketServerConfiguration? config = null) {
        if (config == null) config = new SocketServerConfiguration();
        _config = config;
        _listener = createSocket();
        _dataStore = dataStore;
    }
    public SocketServer(Func<IDataStore> onFirstConnect, Action<IDataStore?>? onDispose = null, SocketServerConfiguration? config = null) {
        if (config == null) config = new SocketServerConfiguration();
        _config = config;
        _listener = createSocket();
        _onFirstConnect = onFirstConnect;
        _onDispose = onDispose;
    }
    Socket createSocket() {
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var ip = string.IsNullOrEmpty(_config.IP) ? IPAddress.Any : IPAddress.Parse(_config.IP);
        var endPoint = new IPEndPoint(ip, _config.Port);
        listener.Bind(endPoint);
        listener.Listen(_config.MaxConnections);
        return listener;
    }
    void log(string? message) => Console.WriteLine(message);
    IDataStore getStore() {
        lock (_lock) {
            if (_dataStore == null && _onFirstConnect != null) {
                try {
                    _dataStore = _onFirstConnect();
                } catch (Exception ex) {
                    log(ex.Message);
                    throw;
                }
            }
            if (_dataStore == null) throw new NullReferenceException(nameof(IDataStore) + " is null. ");
            return _dataStore;
        }
    }
    public async Task Run() {
        log("Waiting for connections on " + _listener.LocalEndPoint);
        while (true) {
            var socket = await _listener.AcceptAsync();
            getStore();
            var client = new Client(socket);
            lock (_clients) _clients.Add(client.Guid, client);
            log("Connected " + client + " - Total connections: " + _clients.Count);
            _ = runClient(client);
        }
    }
    public async Task runClient(Client client) {
        while (true) {
            try {
                var input = await client.Recieve(); // will wait indefinetly for incomming call
                if (_config.ExecutionDelayInMs > 0) await Task.Delay(_config.ExecutionDelayInMs);
                if (input.Length == 0) break; // means client side dissconnect                
                var output = await getStore().BinaryCallAsync(input); // executing call
                await client.Send(output); // returning result
            } catch (Exception ex) {
                log(ex.ToString());
                throw;
            }
        }
        disconnect(client);
    }
    void disconnect(Client client) {
        try {
            lock (_clients) _clients.Remove(client.Guid);
            log("Disconnected " + client + " - Total connections: " + _clients.Count);
            client.Dispose();
        } catch { }
    }
    public void Dispose() {
        log("Disposed");
        if (_onDispose != null) _onDispose(_dataStore);
        lock (_clients) {
            foreach (var kv in _clients) kv.Value.Dispose();
        }
    }
}