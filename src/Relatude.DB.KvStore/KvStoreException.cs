namespace KvStore;

/// <summary>Thrown for invalid usage or a detected corruption of a KvStore database.</summary>
public sealed class KvStoreException : Exception
{
    public KvStoreException(string message) : base(message) { }
    public KvStoreException(string message, Exception inner) : base(message, inner) { }
}
