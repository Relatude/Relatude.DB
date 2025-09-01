using System.Net.Sockets;
using Relatude.DB.Connection;

namespace Relatude.DB.NodeServer.Sockets;
public class Client : IDisposable {
    public readonly int Id;
    public Guid Guid { get; private set; } = Guid.NewGuid();
    byte[] _sendBuffer;
    byte[] _recieveBuffer;
    static int _id;
    public DateTime LastCallUtc { get; private set; } = DateTime.UtcNow;
    public TimeSpan IdleTime { get => DateTime.Now.Subtract(LastCallUtc); }
    public long CallCount { get; private set; }
    public long ErrorCount { get; private set; }
    public Client(Socket socket) {
        Id = Interlocked.Increment(ref _id);
        _socket = socket;
        _sendBuffer = new byte[_socket.SendBufferSize];
        _recieveBuffer = new byte[_socket.ReceiveBufferSize];
    }
    Socket _socket;
    public async Task<Stream> Recieve() {
        CallCount++;
        LastCallUtc = DateTime.UtcNow;
        return await _socket.RecieveStoreMessage(_recieveBuffer);
    }
    public async Task Send(Stream stream) {
        await _socket.SendStoreMessage(stream, _sendBuffer);
    }
    public void Dispose() {
        _socket.Dispose();
    }
    public override string ToString() {
        return "#" + Id + " " + _socket.LocalEndPoint;
    }
}
