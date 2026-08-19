using Relatude.DB.DataStores;

namespace Relatude.DB.Nodes;

/// <summary>
/// One older version of a node, found by <see cref="NodeStore.FindOlderVersions{T}"/>: the mapped
/// node object as it was when the version was written, with the log timestamp it was written at.
/// Versions are read directly from the transaction log files on every call — nothing is cached.
/// </summary>
public sealed class NodeVersion<T> {
    internal NodeVersion(T node, NodeVersionData data) {
        Node = node;
        Source = data.Source;
        Timestamp = data.Timestamp;
    }
    /// <summary>The log file the version was read from (the primary or the secondary log file key).</summary>
    public string Source { get; }
    /// <summary>The log timestamp of the transaction that wrote the version.</summary>
    public long Timestamp { get; }
    /// <summary><see cref="Timestamp"/> as a UTC point in time. Timestamps are UTC ticks made
    /// strictly monotonic, so under heavy write load it can lie a few ticks after the wall clock.</summary>
    public DateTime EstimatedCreationUtc => new(Timestamp, DateTimeKind.Utc);
    /// <summary>The node as it was when the version was written. Relations are not part of node data and are not included.</summary>
    public T Node { get; }
}
