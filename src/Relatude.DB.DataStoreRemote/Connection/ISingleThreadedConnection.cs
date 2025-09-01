namespace Relatude.DB.Connection {
    public interface ISingleThreadedConnection : IDisposable {
        Task<Stream> SendAndReceiveBinary(Stream input);
        //Task<string?> SendAndReceiveJson(string method, string? input);
        DateTime LastUseUtc { get; }
        bool FlaggedAsStalled { get; set; }
        double MsSinceLastUsed() => DateTime.UtcNow.Subtract(LastUseUtc).TotalMilliseconds;
        double CurrentCallDurationInMs();
    }
}