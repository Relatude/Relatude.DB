using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Relations;
using Relatude.DB.Transactions;

namespace Relatude.DB.DataStores.StateStores;

/// <summary>
/// Where the per node state lives: the guid/id map, each node's position in the log, the addresses
/// and the relations. The memory store keeps them in resident collections saved with the state file.
/// An engine backed store keeps them in its engine, which then takes part in the engine protocol
/// (<see cref="IIndexEngine"/>) exactly like the index engines: committed per transaction, made
/// durable after each WAL flush, replayed from its own timestamp at open.
/// </summary>
public interface IStateStore : IDisposable {
    IIndexEngine? Engine { get; }
    ISegmentMap CreateSegmentMap();
    IGuidMap CreateGuidMap();
    IAddressMap CreateAddressMap();
    IRelationIndex CreateRelationIndex(RelationModel relation);
}
public interface IStateMap {
    /// <summary>True when the engine persists the map, so the state file carries nothing for it.</summary>
    bool PersistedByEngine { get; }
    int Count { get; }
}
/// <summary>Node id → position and length of the node data in the log.</summary>
public interface ISegmentMap : IStateMap {
    bool TryGet(int id, out NodeSegment segment);
    bool Contains(int id);
    void Set(int id, NodeSegment segment);
    bool Remove(int id);
    void EnsureCapacity(int count);
    IEnumerable<KeyValuePair<int, NodeSegment>> Entries { get; }
}
/// <summary>Guid ↔ node id, both ways, and the id allocation cursor.</summary>
public interface IGuidMap : IStateMap {
    int LastId { get; set; }
    bool TryGetId(Guid guid, out int id);
    bool TryGetGuid(int id, out Guid guid);
    void Add(int id, Guid guid);
    void Remove(int id, Guid guid);
    void EnsureCapacity(int count);
    IEnumerable<KeyValuePair<int, Guid>> Entries { get; }
}
/// <summary>Packed (node id, culture id) → address, with the multi-owner reverse lookup.</summary>
public interface IAddressMap : IStateMap {
    bool TryGet(long key, out string address);
    void Set(long key, string address);
    bool Remove(long key);
    long[] GetOwners(string address);
    /// <summary>Small state the registry keeps beside the map (its culture table): persisted by the engine, unused by the memory map.</summary>
    string? Meta { get; set; }
    IEnumerable<KeyValuePair<long, string>> Entries { get; }
}
