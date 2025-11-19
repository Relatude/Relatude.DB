using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Stores;

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
internal class NodeTypeStore {
    Definition _definition;
    Dictionary<Guid, short> _shortTypeIdByGuid = [];
    Guid[] _guidByShortTypeId = new Guid[short.MaxValue]; // wastes 32k for faster lookup, limits number of node types to 32 000, should be plenty
    Dictionary<int, short> _typeByIds = [];

    Dictionary<Guid, MutableIdSet> _idsByTypeIncludingDecendants = [];
    Dictionary<Guid, MutableIdSet> _idsByTypeWithoutDecendants = [];
    internal NodeTypeStore(Definition definition, string uniqueKey) {
        _definition = definition;
        UniqueKey = uniqueKey;
    }
    public string UniqueKey { get; private set; }

    public IdSet GetAllNodeIdsForType(Guid typeId, bool includeDescendants) {
        if (includeDescendants) {
            if (_idsByTypeIncludingDecendants.TryGetValue(typeId, out var ids)) return ids.AsUnmutableIdSet();
        } else {
            if (_idsByTypeWithoutDecendants.TryGetValue(typeId, out var ids)) return ids.AsUnmutableIdSet();
        }
        return IdSet.Empty;
    }
    public int GetCountForTypeForStatusInfo(Guid typeId) {
        if (_idsByTypeIncludingDecendants.TryGetValue(typeId, out var ids)) return ids.Count;
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

    public void RegisterActionDuringStateLoad(PrimitiveNodeAction na, bool throwOnErrors, Action<string, Exception> log) {
        var node = na.Node;
        try {
            switch (na.Operation) {
                case PrimitiveOperation.Add: insert(node.__Id, node.NodeType); break;
                case PrimitiveOperation.Remove: delete(node.__Id, node.NodeType); break;
                default: break;
            }
        } catch (Exception ex) {
            var msg = "Error registering action during index type state load for node id: " + node.__Id + " operation: " + na.Operation + " . Error: " + ex.Message;
            log(msg, ex);
            if (throwOnErrors) throw new Exception(msg, ex);
        }
    }
    public void Index(INodeData node) => insert(node.__Id, node.NodeType);
    public void DeIndex(INodeData node) => delete(node.__Id, node.NodeType);

    void insert(int id, Guid nodeTypeId) {
        if (!_shortTypeIdByGuid.TryGetValue(nodeTypeId, out var shortId)) {
            shortId = findNextAvailableShortId();
            _shortTypeIdByGuid.Add(nodeTypeId, shortId);
            _guidByShortTypeId[shortId] = nodeTypeId;
        }
        if (_guidByShortTypeId[shortId] == Guid.Empty) throw new Exception("Internal error. Unable to index node with id: " + id + " as the short id: " + shortId + " is already in use.");
        _typeByIds.Add(id, shortId);
        foreach (var typeId in _definition.Datamodel.NodeTypes[nodeTypeId].ThisAndAllInheritedTypes.Keys) {
            if (!_idsByTypeIncludingDecendants.TryGetValue(typeId, out var ids)) _idsByTypeIncludingDecendants.Add(typeId, ids = new());
            ids.Index(id);
        }
        {
            if (!_idsByTypeWithoutDecendants.TryGetValue(nodeTypeId, out var ids)) _idsByTypeWithoutDecendants.Add(nodeTypeId, ids = new());
            ids.Index(id);
        }
    }
    void delete(int id, Guid nodeTypeId) {
        _typeByIds.Remove(id);
        foreach (var typeId in _definition.Datamodel.NodeTypes[nodeTypeId].ThisAndAllInheritedTypes.Keys) {
            if (_idsByTypeIncludingDecendants.TryGetValue(typeId, out var ids)) {
                ids.DeIndex(id);
                if (ids.Count == 0) _idsByTypeIncludingDecendants.Remove(typeId);
            } else {
                throw new Exception("Internal error. Unable to deindex node with id: " + id + " from type: " + typeId + " as it is not indexed.");
            }
        }
        {
            if (_idsByTypeWithoutDecendants.TryGetValue(nodeTypeId, out var ids)) {
                ids.DeIndex(id);
                if (ids.Count == 0) _idsByTypeWithoutDecendants.Remove(nodeTypeId);
            } else {
                throw new Exception("Internal error. Unable to deindex node with id: " + id + " from type: " + nodeTypeId + " as it is not indexed.");
            }
        }
    }

    public void SaveState(IAppendStream stream) {
        stream.WriteVerifiedInt(_typeByIds.Count); // Count of types
        foreach (var kv in _typeByIds) {
            stream.WriteUInt((uint)kv.Key); // NodeId
            stream.WriteGuid(_guidByShortTypeId[kv.Value]); // NodeType
        }
    }
    public void ReadState(IReadStream stream) {
        var noIds = stream.ReadVerifiedInt();
        for (var i = 0; i < noIds; i++) {
            var nodeId = (int)stream.ReadUInt();
            var nodeTypeId = stream.ReadGuid();
            insert(nodeId, nodeTypeId);
        }
    }

}
