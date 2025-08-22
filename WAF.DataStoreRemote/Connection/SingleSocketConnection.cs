using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace WAF.Connection {
    /// <summary>
    /// Socket connection to remote server
    /// </summary>
    internal class SingleSocketConnection : ISingleThreadedConnection {
        readonly Socket _socket;
        readonly IPAddress _ipAddress;
        readonly int _port;
        readonly byte[] _sendBuffer;
        readonly byte[] _recieveBuffer;
        readonly Stopwatch _sw = new();
        bool _connected;
        public DateTime LastUseUtc { get; private set; } = DateTime.UtcNow;
        public bool FlaggedAsStalled { get; set; }
        bool _sendAndRecieveInProgress;
        object _lock = new();
        public SingleSocketConnection(RemoteConfiguration config) {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _ipAddress = IPAddress.Loopback;
            _sendAndRecieveInProgress = false;
            _sendBuffer = new byte[_socket.SendBufferSize];
            _recieveBuffer = new byte[_socket.ReceiveBufferSize];
            _port = config.Port == 0 ? 1971 : config.Port;
            _connected = false;
        }
        public async Task<Stream> SendAndReceiveBinary(Stream input) {
            lock (_lock) {
                if (_sendAndRecieveInProgress) throw new Exception("Only one simultaneous operation per allowed per socket connection. ");
                _sendAndRecieveInProgress = true;
            }
            _sw.Restart();
            LastUseUtc = DateTime.UtcNow;
            try {
                return await callSocket(input);
            } catch (Exception ex) {
                if (FlaggedAsStalled) throw new Exception("Operation timed out and connection was closed. ", ex);
                throw;
            } finally {
                lock (_lock) _sendAndRecieveInProgress = false;
                LastUseUtc = DateTime.UtcNow;
                _sw.Reset();
            }
        }
        async Task<Stream> callSocket(Stream input) {
            if (!_connected) {
                await _socket.ConnectAsync(_ipAddress, _port);
                _connected = true;
            }
            await _socket.SendStoreMessage(input, _sendBuffer);
            return await _socket.RecieveStoreMessage(_recieveBuffer);
        }
        public double CurrentCallDurationInMs() => _sw.Elapsed.TotalMilliseconds;
        public void Dispose() {
            _socket.Dispose();
        }

        public Task<string?> SendAndReceiveJson(string method, string? input) {
            throw new NotImplementedException();
        }
    }
}
