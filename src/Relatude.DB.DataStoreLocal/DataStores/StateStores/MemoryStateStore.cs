using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Relations;
using Relatude.DB.Transactions;

namespace Relatude.DB.DataStores.StateStores;

/// <summary>Everything resident, saved as one snapshot in the state file and otherwise rebuilt from the log at open.</summary>
public sealed class MemoryStateStore : IStateStore {
    public IIndexEngine? Engine => null;
    public ISegmentMap CreateSegmentMap() => new MemorySegmentMap();
    public IGuidMap CreateGuidMap() => new MemoryGuidMap();
    public IAddressMap CreateAddressMap() => new MemoryAddressMap();
    public IRelationIndex CreateRelationIndex(RelationModel relation) => relation.RelationType switch {
        RelationType.OneOne => new OneOneIndex(),
        RelationType.OneToOne => new OneToOneIndex(),
        RelationType.OneToMany => new OneToManyIndex(),
        RelationType.ManyMany => new ManyManyIndex(),
        RelationType.ManyToMany => new ManyToManyIndex(),
        _ => throw new NotSupportedException(relation.RelationType.ToString()),
    };
    public void Dispose() { }
}
sealed class MemorySegmentMap : ISegmentMap {
    ValueByIdMap<NodeSegment> _segments = new();
    public bool PersistedByEngine => false;
    public int Count => _segments.Count;
    public bool TryGet(int id, out NodeSegment segment) => _segments.TryGetValue(id, out segment);
    public bool Contains(int id) => _segments.Contains(id);
    public void Set(int id, NodeSegment segment) => _segments.Set(id, segment);
    public bool Remove(int id) {
        if (!_segments.Contains(id)) return false;
        _segments.Remove(id);
        return true;
    }
    public void EnsureCapacity(int count) {
        if (_segments.Count == 0) _segments = new(count);
    }
    public IEnumerable<KeyValuePair<int, NodeSegment>> Entries => _segments;
}
sealed class MemoryGuidMap : IGuidMap {
    readonly Dictionary<Guid, int> _ids = [];
    ValueByIdMap<Guid> _guids = new();
    public bool PersistedByEngine => false;
    public int Count => _ids.Count;
    public int LastId { get; set; }
    public bool TryGetId(Guid guid, out int id) => _ids.TryGetValue(guid, out id);
    public bool TryGetGuid(int id, out Guid guid) => _guids.TryGetValue(id, out guid);
    public void Add(int id, Guid guid) {
        _ids.Add(guid, id);
        _guids.Add(id, guid);
    }
    public void Remove(int id, Guid guid) {
        _ids.Remove(guid);
        _guids.Remove(id);
    }
    public void EnsureCapacity(int count) {
        _ids.EnsureCapacity(count);
        if (_guids.Count == 0) _guids = new(count);
    }
    public IEnumerable<KeyValuePair<int, Guid>> Entries => _guids;
}
sealed class MemoryAddressMap : IAddressMap {
    readonly Dictionary<long, string> _addressByKey = [];
    readonly Dictionary<string, long[]> _ownersByAddress = new(StringComparer.Ordinal); // owner arrays are replaced, never mutated
    public bool PersistedByEngine => false;
    public int Count => _addressByKey.Count;
    public string? Meta { get; set; }
    public bool TryGet(long key, out string address) => _addressByKey.TryGetValue(key, out address!);
    public void Set(long key, string address) {
        if (_addressByKey.TryGetValue(key, out var old)) {
            if (old == address) return;
            removeOwner(old, key);
        }
        _addressByKey[key] = address;
        addOwner(address, key);
    }
    public bool Remove(long key) {
        if (!_addressByKey.Remove(key, out var address)) return false;
        removeOwner(address, key);
        return true;
    }
    public long[] GetOwners(string address) => _ownersByAddress.TryGetValue(address, out var owners) ? owners : [];
    public IEnumerable<KeyValuePair<long, string>> Entries => _addressByKey;
    void addOwner(string address, long key) {
        if (_ownersByAddress.TryGetValue(address, out var owners)) {
            if (Array.IndexOf(owners, key) >= 0) return;
            _ownersByAddress[address] = [.. owners, key];
        } else {
            _ownersByAddress[address] = [key];
        }
    }
    void removeOwner(string address, long key) {
        if (!_ownersByAddress.TryGetValue(address, out var owners)) return;
        var index = Array.IndexOf(owners, key);
        if (index < 0) return;
        if (owners.Length == 1) _ownersByAddress.Remove(address);
        else _ownersByAddress[address] = [.. owners[..index], .. owners[(index + 1)..]];
    }
}
