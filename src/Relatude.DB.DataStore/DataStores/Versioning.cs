using Relatude.DB.Datamodels;

namespace Relatude.DB.DataStores;

/// <summary>
/// One historical version of a node, found by <see cref="IDataStore.FindOlderVersions"/>. Versions
/// are read directly from the transaction log files on every call — nothing is cached.
/// </summary>
public sealed class NodeVersionData {
    /// <summary>The log file the version was read from (the primary or the secondary log file key).</summary>
    public required string Source { get; init; }
    /// <summary>The log timestamp of the transaction that wrote the version.</summary>
    public required long Timestamp { get; init; }
    /// <summary><see cref="Timestamp"/> as a UTC point in time. Timestamps are UTC ticks made
    /// strictly monotonic, so under heavy write load it can lie a few ticks after the wall clock.</summary>
    public DateTime EstimatedCreationUtc => new(Timestamp, DateTimeKind.Utc);
    /// <summary>The node as it was when the version was written. Relations are not part of node data and are not included.</summary>
    public required INodeDataExternal Node { get; init; }
}
