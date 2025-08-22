using System.Net.Sockets;

namespace WAF.Connection;
public static class SocketExtensions {
    public static async Task<Stream> RecieveStoreMessage(this Socket socket, byte[] buffer) {
        var mem = new Memory<byte>(buffer, 0, buffer.Length);
        var recieved = await socket.ReceiveAsync(mem, SocketFlags.None);
        if (recieved > 0) {
            long messageLength = BitConverter.ToInt64(buffer, 0); // first 8 bytes contains message length
            var message = new MemoryStream((int)messageLength);
            message.Write(buffer, 8, recieved - 8); // extract first portion of message ( minus the 8 first for message length )
            while (message.Length < messageLength) { // get the rest of the message
                recieved = await socket.ReceiveAsync(mem, SocketFlags.None);
                message.Write(buffer, 0, recieved);
            }
            message.Position = 0; // making it ready for read
            return message;
        }
        return new MemoryStream(); // empty stream
    }
    public static async Task SendStoreMessage(this Socket socket, Stream message, byte[] buffer) {
        long sent = 0;
        var messageLength = new Memory<byte>(BitConverter.GetBytes(message.Length));
        await socket.SendAsync(messageLength, SocketFlags.None);
        message.Position = 0;
        while (sent < message.Length) {
            var bytesLeft = message.Length - message.Position;
            var bytesToSend = buffer.Length > bytesLeft ? (int)bytesLeft : buffer.Length;
            message.Read(buffer, 0, bytesToSend);
            var mem = new Memory<byte>(buffer, 0, bytesToSend);
            sent += await socket.SendAsync(mem, SocketFlags.None);
        }
    }
}
