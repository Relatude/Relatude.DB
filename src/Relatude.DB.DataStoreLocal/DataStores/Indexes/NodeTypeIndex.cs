using System.Dynamic;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Indexes;
class MutableIdSet() {
    readonly StateIdTracker _state = new();
    public long StateId { get => _state.Current; }
    HashSet<int> _ids = [];
    IdSet? _lastSet;
    public void Index(int id) {
        _ids.Add(id);
        _state.RegisterAddition(id);
        _lastSet = null;
    }
    public void DeIndex(int id) {
        _ids.Remove(id);
        _state.RegisterRemoval(id);
        _lastSet = null;
    }
    public int Count => _ids.Count;
    public IdSet AsUnmutableIdSet() {
        if (_lastSet != null) return _lastSet;
        return _lastSet ??= new(_ids, _state.Current);
    }
}
internal class NodeTypeIndex : IIndex {
    Definition _definition;

    Dictionary<Guid, short> _shortTypeIdByGuid = [];
    Guid[] _guidByShortTypeId = new Guid[short.MaxValue]; // wastes 32k for faster lookup, limits number of node types to 32 000, should be plenty
    Dictionary<int, short> _typeByIds = [];

    Dictionary<Guid, MutableIdSet> _idsByType = [];
    internal NodeTypeIndex(Definition definition, string uniqueKey) {
        _definition = definition;
        UniqueKey = uniqueKey;
    }
    public string UniqueKey { get; private set; }

    public IdSet GetAllNodeIdsForType(Guid typeId) {
        if (_idsByType.TryGetValue(typeId, out var ids)) return ids.AsUnmutableIdSet();
        return IdSet.Empty;
    }
    public int GetCountForTypeForStatusInfo(Guid typeId) {
        if (_idsByType.TryGetValue(typeId, out var ids)) return ids.Count;
        return 0;
    }

    public Guid GetType(int id) {
        if (_typeByIds.TryGetValue(id, out var typeId)) return _guidByShortTypeId[typeId];
        throw new Exception("Internal error. Unable to determine type of unknown node with id: " + id);
    }
    public bool TryGetType(int id, out Guid typeId) {
        if (_typeByIds.TryGetValue(id, out var shortId)) {
            typeId = _guidByShortTypeId[shortId];
            return true;
        }
        typeId = Guid.Empty;
        return false;
    }

    short findNextAvailableShortId() {
        for (var i = 0; i < short.MaxValue; i++) {
            if (_guidByShortTypeId[i] == Guid.Empty) return (short)i;
        }
        throw new Exception("Internal error. Unable to find next available TypeId. Maximum number of " + short.MaxValue + " reached.");
    }

    public void Index(INodeData node) {
        var id = node.__Id;
        if (!_shortTypeIdByGuid.TryGetValue(node.NodeType, out var shortId)) {
            shortId = findNextAvailableShortId();
            _shortTypeIdByGuid.Add(node.NodeType, shortId);
            _guidByShortTypeId[shortId] = node.NodeType;
        }
        if (_guidByShortTypeId[shortId] == Guid.Empty) throw new Exception("Internal error. Unable to index node with id: " + id + " as the short id: " + shortId + " is already in use.");
        _typeByIds.Add(id, shortId);
        foreach (var typeId in _definition.Datamodel.NodeTypes[node.NodeType].ThisAndAllInheritedTypes.Keys) {
            if (!_idsByType.TryGetValue(typeId, out var ids)) _idsByType.Add(typeId, ids = new());
            ids.Index(id);
        }
    }
    public void DeIndex(INodeData node) {
        var id = node.__Id;
        _typeByIds.Remove(id);
        foreach (var typeId in _definition.Datamodel.NodeTypes[node.NodeType].ThisAndAllInheritedTypes.Keys) {
            if (_idsByType.TryGetValue(typeId, out var ids)) {
                ids.DeIndex(id);
                if (ids.Count == 0) _idsByType.Remove(typeId);
            } else {
                throw new Exception("Internal error. Unable to deindex node with id: " + id + " from type: " + typeId + " as it is not indexed.");
            }
        }
    }



    public void SaveState(IAppendStream stream) {
        stream.WriteVerifiedInt(_typeByIds.Count); // Count of types
        foreach (var kv in _typeByIds) {
            stream.WriteUInt((uint)kv.Key); // NodeId
            stream.WriteGuid(_guidByShortTypeId[kv.Value]); // NodeType
        }
        stream.WriteVerifiedInt(_idsByType.Count);
        foreach (var kv in _idsByType) {
            stream.WriteGuid(kv.Key); // NodeType
            var set = kv.Value.AsUnmutableIdSet();
            stream.WriteVerifiedInt(set.Count);
            foreach (var id in set.Enumerate())
                stream.WriteUInt((uint)id);
        }
    }
    public void ReadState(IReadStream stream) {
        var noIds = stream.ReadVerifiedInt();
        for (var i = 0; i < noIds; i++) {
            var nodeId = (int)stream.ReadUInt();
            var nodeTypeId = stream.ReadGuid();
            if (!_shortTypeIdByGuid.TryGetValue(nodeTypeId, out var byteValue)) {
                byteValue = (byte)_shortTypeIdByGuid.Count;
                _shortTypeIdByGuid.Add(nodeTypeId, byteValue);
                _guidByShortTypeId[byteValue] = nodeTypeId;
            }
            _typeByIds.Add(nodeId, byteValue);
        }
        var noTypes = stream.ReadVerifiedInt();
        for (var i = 0; i < noTypes; i++) {
            var typeId = stream.ReadGuid();
            var set = new MutableIdSet();
            var noIdsInSet = stream.ReadVerifiedInt();
            for (var j = 0; j < noIdsInSet; j++) set.Index((int)stream.ReadUInt());
            _idsByType.Add(typeId, set);
        }
    }

    public void Add(int nodeId, object value) => throw new NotSupportedException("Not relevant");
    public void Remove(int nodeId, object value) => throw new NotSupportedException("Not relevant");
    public void RegisterAddDuringStateLoad(int nodeId, object value, long timestampId) => throw new NotSupportedException("Not relevant");
    public void RegisterRemoveDuringStateLoad(int nodeId, object value, long timestampId) => throw new NotSupportedException("Not relevant");
    public void CompressMemory() { }
    public void Dispose() { }
    public void ClearCache() { }

}
