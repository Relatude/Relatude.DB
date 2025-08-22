//using System.Diagnostics;
//using System.IO.Pipes;
//using System.Net;
//using System.Net.Sockets;

//namespace WAF.Connection {
//    /// <summary>
//    /// Pipe connection to remote server on same computer
//    /// </summary>
//    internal class SingleSharedMemConnection : ISingleThreadedConnection {
//        readonly AnonymousPipeServerStream _pipe;
//        readonly int _port;
//        readonly byte[] _sendBuffer;
//        readonly byte[] _recieveBuffer;
//        readonly Stopwatch _sw = new();
//        bool _connected;
//        public DateTime LastUseUtc { get; private set; } = DateTime.UtcNow;
//        public bool FlaggedAsStalled { get; set; }
//        bool _sendAndRecieveInProgress;
//        public SingleSharedMemConnection(RemoteConfiguration config) {
//            _pipe = new AnonymousPipeServerStream(PipeDirection.InOut, HandleInheritability.Inheritable);
//            _sendAndRecieveInProgress = false;
//            _sendBuffer = new byte[1 * 1024 ^ 2]; // 1 mb
//            _recieveBuffer = new byte[1 * 1024 ^ 2]; // 1 mb
//            _port = config.Port == 0 ? 1971 : config.Port;
//            _connected = false;
//        }
//        public async Task<Stream> SendAndReceiveBinary(Stream input) {
//            lock (_lock) {
//                if (_sendAndRecieveInProgress) throw new Exception("Only one simultaneous operation per allowed per socket connection. ");
//                _sendAndRecieveInProgress = true;
//            }
//            _sw.Restart();
//            LastUseUtc = DateTime.UtcNow;
//            try {
//                return await callSocket(input);
//            } catch (Exception ex) {
//                if (FlaggedAsStalled) throw new Exception("Operation timed out and connection was closed. ", ex);
//                throw;
//            } finally {
//                lock (_lock) _sendAndRecieveInProgress = false;
//                LastUseUtc = DateTime.UtcNow;
//                _sw.Reset();
//            }
//        }
//        async Task<Stream> callSocket(Stream input) {
//            if (!_connected) {
//                //await _pipe.ConnectAsync(_ipAddress, _port);
//                _connected = true;
//            }
//            await _pipe.SendStoreMessage(input, _sendBuffer);
//            return await _pipe.RecieveStoreMessage(_recieveBuffer);
//        }
//        public double CurrentCallDurationInMs() => _sw.Elapsed.TotalMilliseconds;
//        public void Dispose() {
//            _pipe.Dispose();
//        }

//        public Task<string?> SendAndReceiveJson(string method, string? input) {
//            throw new NotImplementedException();
//        }
//    }
//}
