namespace Relatude.DB.Connection {
    public interface IMultiThreadedConnection : IDisposable {
        Task<Stream> SendAndReceiveBinary(Stream input);
    }
}