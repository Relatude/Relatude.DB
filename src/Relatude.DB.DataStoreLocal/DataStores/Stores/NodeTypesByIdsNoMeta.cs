using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Stores;

class mutableIdSet {
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
internal class NodeTypesByIdsNoMeta {
    readonly Definition _definition;
    readonly Dictionary<Guid, short> _shortTypeIdByGuid = [];
    readonly Guid[] _typeGuidByShortTypeId = new Guid[short.MaxValue]; // wastes 32k for faster lookup, limits number of node types to 32 000, should be plenty
    readonly Dictionary<int, short> _typeByIds = [];
    readonly Dictionary<Guid, mutableIdSet> _idsByTypeNoMetaIncludingDescendants = [];
    readonly Dictionary<Guid, mutableIdSet> _idsByTypeNoMetaAndWithoutDescendants = [];
    internal NodeTypesByIdsNoMeta(Definition definition) {
        _definition = definition;
    }
    public IdSet GetAllNodeIdsForType(Guid typeId, bool excludeDecendants) {
        if (excludeDecendants) {
            if (_idsByTypeNoMetaAndWithoutDescendants.TryGetValue(typeId, out var ids)) return ids.AsUnmutableIdSet();
        } else {
            if (_idsByTypeNoMetaIncludingDescendants.TryGetValue(typeId, out var ids)) return ids.AsUnmutableIdSet();
        }
        return IdSet.Empty;
    }
    public int GetCountForType(Guid typeId, bool excludeDecendants) {
        if (excludeDecendants) {
            if (_idsByTypeNoMetaAndWithoutDescendants.TryGetValue(typeId, out var ids)) return ids.Count;
        } else {
            if (_idsByTypeNoMetaIncludingDescendants.TryGetValue(typeId, out var ids)) return ids.Count;
        }
        return 0;
    }
    public Guid GetType(int id) {
        if (_typeByIds.TryGetValue(id, out var shortId)) return _typeGuidByShortTypeId[shortId];
        throw new Exception("Internal error. Unable to determine type of unknown node with id: " + id);
    }
    public bool TryGetType(int id, out Guid typeId) {
        if (_typeByIds.TryGetValue(id, out var shortId)) {
            typeId = _typeGuidByShortTypeId[shortId];
            return true;
        }
        typeId = Guid.Empty;
        return false;
    }
    short findNextAvailableShortId() {
        for (var i = 0; i < short.MaxValue; i++) {
            if (_typeGuidByShortTypeId[i] == Guid.Empty) return (short)i;
        }
        throw new Exception("Internal error. Unable to find next available TypeId. Maximum number of " + short.MaxValue + " reached.");
    }
    public void Insert(INodeData node, NodeTypeModel nodeType) {
        var id = node.__Id;
        Guid nodeTypeId = node.NodeType;
        if (!_shortTypeIdByGuid.TryGetValue(nodeTypeId, out var shortId)) {
            shortId = findNextAvailableShortId();
            _shortTypeIdByGuid.Add(nodeTypeId, shortId);
            _typeGuidByShortTypeId[shortId] = nodeTypeId;
        }
        if (_typeGuidByShortTypeId[shortId] == Guid.Empty) throw new Exception("Internal error. ");
        _typeByIds.Add(id, shortId);
        foreach (var typeId in nodeType.ThisAndAllInheritedTypes.Keys) {
            if (!_idsByTypeNoMetaIncludingDescendants.TryGetValue(typeId, out var ids)) _idsByTypeNoMetaIncludingDescendants.Add(typeId, ids = new());
            ids.Index(id);
        }
        {
            if (!_idsByTypeNoMetaAndWithoutDescendants.TryGetValue(nodeTypeId, out var ids)) _idsByTypeNoMetaAndWithoutDescendants.Add(nodeTypeId, ids = new());
            ids.Index(id);
        }
    }
    public void Delete(INodeData node, NodeTypeModel nodeType) {
        var id = node.__Id;
        Guid nodeTypeId = node.NodeType;
        _typeByIds.Remove(id);
        foreach (var typeId in nodeType.ThisAndAllInheritedTypes.Keys) {
            if (_idsByTypeNoMetaIncludingDescendants.TryGetValue(typeId, out var ids)) {
                ids.DeIndex(id);
                if (ids.Count == 0) _idsByTypeNoMetaIncludingDescendants.Remove(typeId);
            } else {
                throw new Exception("Internal error. Unable to deindex node with id: " + id + " from type: " + typeId + " as it is not indexed.");
            }
        }
        {
            if (_idsByTypeNoMetaAndWithoutDescendants.TryGetValue(nodeTypeId, out var ids)) {
                ids.DeIndex(id);
                if (ids.Count == 0) _idsByTypeNoMetaAndWithoutDescendants.Remove(nodeTypeId);
            } else {
                throw new Exception("Internal error. Unable to deindex node with id: " + id + " from type: " + nodeTypeId + " as it is not indexed.");
            }
        }
    }
    public void SaveState(IAppendStream stream) {
        stream.WriteVerifiedInt(_typeByIds.Count); // Count of types
        foreach (var kv in _typeByIds) {
            var nodeId = kv.Key;
            stream.WriteUInt((uint)nodeId); // NodeId
            var nodeTypeGuid = _typeGuidByShortTypeId[kv.Value];
            stream.WriteGuid(nodeTypeGuid); // NodeType
        }
    }
    public void ReadState(IReadStream stream) {
        var noIds = stream.ReadVerifiedInt();
        for (var i = 0; i < noIds; i++) {
            var nodeId = (int)stream.ReadUInt();
            var nodeTypeId = stream.ReadGuid();
            var nodeType = _definition.NodeTypes[nodeTypeId].Model;
            Insert(new NodeDataOnlyTypeAndId(nodeId, nodeTypeId), nodeType);
        }
    }
}

