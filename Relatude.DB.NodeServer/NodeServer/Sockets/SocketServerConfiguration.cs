using Relatude.DB.Connection;

namespace Relatude.DB.NodeServer.Sockets {
    public class SocketServerConfiguration {
        public RemoteProtocol Protocol { get; set; }
        public string? IP { get; set; }
        public int Port { get; set; } = 1971;
        public int MaxConnections = 10000;
        public int ExecutionDelayInMs = 0;
    }
}
