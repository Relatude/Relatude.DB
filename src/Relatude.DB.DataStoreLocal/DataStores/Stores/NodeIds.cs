using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Stores;

class ids {
    readonly StateIdTracker _state = new();
    public long StateId { get => _state.Current; }
    HashSet<int> _ids = [];
    IdSet? _lastSet;
    object _lock = new();
    public void Add(int id) {
        _ids.Add(id);
        _state.RegisterAddition(id);
        _lastSet = null;
    }
    public void Remove(int id) {
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

internal class NodeIds {
    readonly Definition _definition;
    readonly NativeModelStore _native;
    short shortIdCounter = 0;
    readonly Dictionary<short, NodeMetaWithType> _metaByShort = new();
    readonly Dictionary<NodeMetaWithType, short> _shortByMeta = new();
    readonly Dictionary<int, short[]> _nodeMetasByNodeId = new();
    readonly Cache<QueryContextKey, ids> _cachedNodeIdsByCtx;
    readonly Dictionary<Guid, int> _countByType = new();
    internal NodeIds(Definition definition, NativeModelStore nativeModelStore) {
        _definition = definition;
        _native = nativeModelStore;
        _cachedNodeIdsByCtx = new(1000); // TODO: Make this configurable
    }
    ids evaluateRelevantIds(Guid typeId, QueryContext ctx) {
        var ids = new ids();
        var relevantMetaIds = _shortByMeta
            .Where(kv => isMetaRelevantForContext(kv.Key, ctx.Key))
            .Select(kv => kv.Value).ToHashSet();
        foreach (var kv in _nodeMetasByNodeId) {
            foreach (var shortMetaId in kv.Value) {
                if (relevantMetaIds.Contains(shortMetaId)) {
                    ids.Add(kv.Key);
                    break;
                }
            }
        }
        return ids;
    }
    bool isMetaRelevantForContext(NodeMetaWithType metaWithType, QueryContextKey ctx) {
        var typeDef = _definition.NodeTypes[metaWithType.NodeTypeId].Model;
        // type filter
        if (ctx.ExcludeDecendants) {
            if (typeDef.Id != metaWithType.NodeTypeId) return false;
        } else {
            if (!typeDef.ThisAndDescendingTypes.ContainsKey(metaWithType.NodeTypeId)) return false;
        }
        // deleted filter
        if (!ctx.IncludeDeleted && metaWithType.Deleted) return false;
        // culture filter
        if (!ctx.IncludeCultureFallback) {
            if (metaWithType.CultureId != ctx.CultureId) return false;
        }
        return true;
    }
    public IdSet GetAllNodeIdsForTypeFilteredByContext(Guid typeId, QueryContext ctx) {
        if (_cachedNodeIdsByCtx.TryGet(ctx.Key, out var ids)) return ids.AsUnmutableIdSet();
        ids = evaluateRelevantIds(typeId, ctx); // takes time! // could consider lock here to avoid double eval
        _cachedNodeIdsByCtx.Set(ctx.Key, ids, 1);
        return ids.AsUnmutableIdSet();
    }
    public IdSet GetAllNodeIdsForTypeNoFilter(Guid typeId, bool excludeDecendants) {
        return GetAllNodeIdsForTypeFilteredByContext(typeId, excludeDecendants ? QueryContext.AllExcludingDecendants : QueryContext.AllIncludingDescendants);
    }
    public int GetCountForTypeForStatusInfo(Guid typeId) {
        if (_countByType.TryGetValue(typeId, out var count)) return count;
        return 0;
    }
    public Guid GetType(int id) {
        if (TryGetType(id, out var typeId)) return typeId;
        throw new Exception("Internal error. Unable to determine type of unknown node with id: " + id);
    }
    public bool TryGetType(int id, out Guid typeId) {
        if (_nodeMetasByNodeId.TryGetValue(id, out var shortMetaIds)) {
            typeId = _metaByShort[shortMetaIds[0]].NodeTypeId;
            return true;
        }
        typeId = default;
        return false;
    }
    public void RegisterActionDuringStateLoad(PrimitiveNodeAction na, bool throwOnErrors, Action<string, Exception?> log) {
        try {
            if (na.Operation == PrimitiveOperation.Add) {
                Index(na.Node);
            } else if (na.Operation == PrimitiveOperation.Remove) {
                DeIndex(na.Node);
            }
        } catch (Exception ex) {
            if (throwOnErrors) throw;
            log("Error registering node action during state load: " + na, ex);
        }
    }
    public void Index(INodeData node) {
        if (node is not NodeData && node is not NodeDataVersion) {
            throw new Exception("Internal error. Attempting to deindex unsupported node data type: " + node.GetType().FullName);
            // must be root node data type, not a sub version or id type
        }
        NodeMetaWithType meta = new(node.Meta ?? NodeMeta.Empty, node.NodeType);
        if (!_shortByMeta.TryGetValue(meta, out var shortId)) {
            if (shortIdCounter == short.MaxValue) throw new Exception("Internal error. Node meta short id overflow.");
            shortId = shortIdCounter++;
            _metaByShort[shortId] = meta;
            _shortByMeta.Add(meta, shortId);
        }
        if (_nodeMetasByNodeId.TryGetValue(node.__Id, out var shortIds)) {
            _nodeMetasByNodeId[node.__Id] = [.. shortIds, shortId];
        } else {
            _nodeMetasByNodeId.Add(node.__Id, [shortId]);
        }
        foreach (var kv in _cachedNodeIdsByCtx.AllNotThreadSafe()) {
            if (isMetaRelevantForContext(meta, kv.Key)) {
                kv.Value.Add(node.__Id);
            }
        }
        if (_countByType.TryGetValue(node.NodeType, out var count)) {
            _countByType[node.NodeType] = count + 1;
        } else {
            _countByType[node.NodeType] = 1;
        }
    }
    public void DeIndex(INodeData node) {
        if (node is not NodeData && node is not NodeDataVersion) {
            throw new Exception("Internal error. Attempting to deindex unsupported node data type: " + node.GetType().FullName);
            // must be root node data type, not a sub version or id type
        }
        var shortId = _shortByMeta[new(node.Meta ?? NodeMeta.Empty, node.NodeType)];
        var shortIds = _nodeMetasByNodeId[node.__Id];
        if (shortIds.Length == 1) {
            if (shortIds[0] != shortId) throw new Exception("Internal error. Attempting to deindex node meta that is not indexed for node id: " + node.__Id);
            _nodeMetasByNodeId.Remove(node.__Id);
        } else {
            var newShortIds = new short[shortIds.Length - 1];
            for (int i = 0, j = 0; i < shortIds.Length; i++) {
                if (shortIds[i] != shortId) newShortIds[j++] = shortIds[i];
            }
            if (newShortIds.Length == shortIds.Length) throw new Exception("Internal error. Attempting to deindex node meta that is not indexed for node id: " + node.__Id);
            _nodeMetasByNodeId[node.__Id] = newShortIds;
        }
        foreach (var kv in _cachedNodeIdsByCtx.AllNotThreadSafe()) {
            if (isMetaRelevantForContext(_metaByShort[shortId], kv.Key)) {
                kv.Value.Remove(node.__Id);
            }
        }
        if (_countByType.TryGetValue(node.NodeType, out var count)) {
            if (count <= 1) _countByType.Remove(node.NodeType);
            else _countByType[node.NodeType] = count - 1;
        } else {
            throw new Exception("Internal error. Attempting to deindex node of type that has no count registered: " + node.NodeType);
        }
    }
    public void SaveState(IAppendStream stream) {
    }
    public void ReadState(IReadStream stream) {
    }
}
