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
    public DateTime _createdUsingNowUtc;
    public DateTime? ValidFrom;
    public DateTime? ValidTo;
    public ids(DateTime nowUtc) {
        _createdUsingNowUtc = nowUtc;
    }
    public DateTime CreatedWithNowUtc => _createdUsingNowUtc;
    public void Add(int id, DateTime? releaseDate, DateTime? expireDate) {
        _ids.Add(id);
        _state.RegisterAddition(id);
        _lastSet = null;
        // update time constraints:
        if (releaseDate.HasValue) {
            if (!ValidFrom.HasValue || releaseDate.Value > ValidFrom.Value) {
                ValidFrom = releaseDate.Value;
            }
        }
        if (expireDate.HasValue) {
            if (!ValidTo.HasValue || expireDate.Value < ValidTo.Value) {
                ValidTo = expireDate.Value;
            }
        }
    }
    public void Remove(int id) {
        _ids.Remove(id);
        _state.RegisterRemoval(id);
        _lastSet = null;
        // cannot update time constraints on removal, so just keep interval the smallest it has been
    }
    public int Count => _ids.Count;
    public IdSet AsUnmutableIdSet() {
        if (_lastSet != null) return _lastSet;
        return _lastSet ??= new(_ids, _state.Current);
    }
    internal bool IsWithinTimeConstraints(DateTime nowUtc) {
        if (ValidFrom.HasValue && nowUtc < ValidFrom.Value) return false;
        if (ValidTo.HasValue && nowUtc >= ValidTo.Value) return false;
        // either no time constraints, or nowUtc is within the time constraints:
        return true;

    }
}

public class NodeIds {
    readonly Definition _definition;

    uint shortIdCounter = 0;
    readonly Dictionary<uint, NodeMetaWithType> _metaByShort = new();
    readonly Dictionary<NodeMetaWithType, uint> _shortByMeta = new();
    readonly Dictionary<int, uint[]> _nodeMetasByNodeId = new();
    readonly Dictionary<Guid, int> _countByType = new();

    readonly Cache<QueryContextKey, ids> _cachedNodeIdsByCtx;
    
    internal NodeIds(Definition definition) {
        _definition = definition;
        _cachedNodeIdsByCtx = new(1000); // TODO: Make this configurable
    }
    ids evaluateRelevantIds(Guid typeId, QueryContext ctx, DateTime nowUtc) {
        var ids = new ids(nowUtc);
        var relevantMetaIds = _shortByMeta
            .Where(kv => isMetaRelevantForContext(kv.Key, ctx.CtxKey, nowUtc))
            .Select(kv => kv.Value).ToHashSet();
        foreach (var kv in _nodeMetasByNodeId) {
            foreach (var shortMetaId in kv.Value) {
                if (relevantMetaIds.Contains(shortMetaId)) {
                    var meta = _metaByShort[shortMetaId];
                    ids.Add(kv.Key, meta.ReleaseUtc, meta.ExpireUtc);
                    break;
                }
            }
        }
        return ids;
    }
    bool isMetaRelevantForContext(NodeMetaWithType meta, QueryContextKey ctx, DateTime nowUtc) {
        var typeDef = _definition.NodeTypes[meta.NodeTypeId].Model;
        if (ctx.ExcludeDecendants) {
            if (typeDef.Id != meta.NodeTypeId) return false;
        } else {
            if (!typeDef.ThisAndDescendingTypes.ContainsKey(meta.NodeTypeId)) return false;
        }
        if (!ctx.IncludeDeleted && meta.Deleted) return false;
        if (!ctx.IncludeCultureFallback) if (meta.CultureId != ctx.CultureId) return false;
        if (!ctx.IncludeUnpublished && !meta.AnyPublishedContentAnyDate) {
            if (meta.ReleaseUtc.HasValue && meta.ReleaseUtc.Value > nowUtc) return false;
            if (meta.ExpireUtc.HasValue && meta.ExpireUtc.Value <= nowUtc) return false;
        }
        if (!ctx.IncludeHidden && meta.Hidden) return false;
        if (ctx.CollectionIds != null && ctx.CollectionIds.Length > 0 && !ctx.CollectionIds.Contains(meta.CollectionId)) return false;
        return true;
    }
    public IdSet GetAllNodeIdsForTypeFilteredByContext(Guid typeId, QueryContext ctx) {
        DateTime nowUtc = ctx.NowUtc ?? DateTime.UtcNow;
        if (_cachedNodeIdsByCtx.TryGet(ctx.CtxKey, out var ids)) {
            if (ids.IsWithinTimeConstraints(nowUtc)) return ids.AsUnmutableIdSet();
        }
        ids = evaluateRelevantIds(typeId, ctx, nowUtc); // takes time! // could consider lock here to avoid double eval
        _cachedNodeIdsByCtx.Set(ctx.CtxKey, ids, 1);
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
            if (isMetaRelevantForContext(meta, kv.Key, kv.Value.CreatedWithNowUtc)) { // no time constraint
                kv.Value.Add(node.__Id, meta.ReleaseUtc, meta.ExpireUtc);
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
            var newShortIds = new uint[shortIds.Length - 1];
            for (int i = 0, j = 0; i < shortIds.Length; i++) {
                if (shortIds[i] != shortId) newShortIds[j++] = shortIds[i];
            }
            if (newShortIds.Length == shortIds.Length) throw new Exception("Internal error. Attempting to deindex node meta that is not indexed for node id: " + node.__Id);
            _nodeMetasByNodeId[node.__Id] = newShortIds;
        }
        foreach (var kv in _cachedNodeIdsByCtx.AllNotThreadSafe()) {
            if (isMetaRelevantForContext(_metaByShort[shortId], kv.Key, kv.Value.CreatedWithNowUtc)) {
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
    static int formatVersion= 1000;
    public void SaveState(IAppendStream stream) {
        stream.WriteInt(formatVersion);
        stream.WriteUInt(shortIdCounter);  
        stream.WriteInt(_metaByShort.Count);
        foreach (var kv in _metaByShort) {
            stream.WriteUInt(kv.Key);
            stream.WriteByteArray(kv.Value.ToBytes());
        }
        stream.WriteInt(_nodeMetasByNodeId.Count);
        foreach (var kv in _nodeMetasByNodeId) {
            stream.WriteInt(kv.Key);
            stream.WriteInt(kv.Value.Length);
            foreach (var shortId in kv.Value) {
                stream.WriteUInt(shortId);
            }
        }
        stream.WriteInt(_countByType.Count);
        foreach (var kv in _countByType) {
            stream.WriteGuid(kv.Key);
            stream.WriteInt(kv.Value);
        }
    }
    public void ReadState(IReadStream stream) {
        var version = stream.ReadInt();
        if (version != formatVersion) throw new Exception("Incompatible format version for NodeIds store: " + version);
        shortIdCounter = stream.ReadUInt();
        var metaCount = stream.ReadInt();
        for (int i = 0; i < metaCount; i++) {
            var shortId = stream.ReadUInt();
            var metaBytes = stream.ReadByteArray();
            var meta = NodeMetaWithType.FromBytes(metaBytes);
            _metaByShort[shortId] = meta;
            _shortByMeta[meta] = shortId;
        }
        var nodeMetaCount = stream.ReadInt();
        for (int i = 0; i < nodeMetaCount; i++) {
            var nodeId = stream.ReadInt();
            var shortIdArrayLength = stream.ReadInt();
            var shortIds = new uint[shortIdArrayLength];
            for (int j = 0; j < shortIdArrayLength; j++) {
                shortIds[j] = stream.ReadUInt();
            }
            _nodeMetasByNodeId[nodeId] = shortIds;
        }
        var countByTypeCount = stream.ReadInt();
        for (int i = 0; i < countByTypeCount; i++) {
            var typeId = stream.ReadGuid();
            var count = stream.ReadInt();
            _countByType[typeId] = count;
        }
    }
}
