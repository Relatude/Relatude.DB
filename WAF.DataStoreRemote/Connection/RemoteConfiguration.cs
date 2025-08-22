namespace WAF.Connection;
public enum RemoteProtocol {
    Sockets, // fastest, but can cause problems with firewalls ( tcp ), does not support Json format
    Http, // slower but easier with firewalls as it is http
    Loopback, // same process, for testing serialization and de-serialization
    // NamedPipes, // not implemenet yet, faster than sockets, but more sensitive to firewalls and reliable network
    // SharedMem, // not implemenet yet, fastest, but only local
}
public enum DataFormat {
    Binary, // faster and with validation, but requires native client library
    Json, // slower, but simpler client code (browser clients)
}
public class RemoteConfiguration {
    public RemoteProtocol Protocol { get; set; } = RemoteProtocol.Sockets;
    public DataFormat Format { get; set; } = DataFormat.Binary;
    //public string? EncryptionKey { get; set; }  // not implemented yet
    public string? DNS { get; set; }
    public string? Path { get; set; }
    public string? IP { get; set; }
    public int Port { get; set; } = 0;
    public int MaxNoConnections = 1000; // max concurrent connections, before execution is qued
    public int ExecutionTimeoutInSec = 30;  // execution is aborted after timeout and a timeout exception is thrown
    public int ConnectionTimeoutInSec = 30; // timeout in attempting to create connection or aquire one from the pool
    public int IdleTimeoutInPoolInSec = 30; // connection is closed and removed from pool after timeout
}
