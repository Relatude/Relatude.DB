using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Stores;

class idSet {
    readonly StateIdTracker _state = new();
    public long StateId { get => _state.Current; }
    HashSet<int> _ids = [];
    IdSet? _lastSet;
    DateTime _createdUsingNowUtc;
    Guid _createdUsingTypeId;
    public DateTime? ValidFrom;
    public DateTime? ValidTo;
    public idSet(DateTime nowUtc, Guid typeId) {
        _createdUsingNowUtc = nowUtc;
        _createdUsingTypeId = typeId;
    }
    public DateTime CreatedWithNowUtc => _createdUsingNowUtc;
    public Guid CreatedWithTypeId => _createdUsingTypeId;
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
class metaAndType(NodeMeta meta, Guid typeId) : IEquatable<metaAndType> {
    public readonly Guid TypeId = typeId;
    public readonly NodeMeta Meta = meta;
    public bool Equals(metaAndType? other) {
        if (other is null) return false;
        return TypeId == other.TypeId && Meta.Equals(other.Meta);
    }
    public override bool Equals(object? obj) {
        return obj is metaAndType other && Equals(other);
    }
    public override int GetHashCode() {
        return HashCode.Combine(TypeId, Meta);
    }
}
class nodeMetasByNodeId {
    readonly Dictionary<int, uint> _single = new();
    readonly Dictionary<int, uint[]> _multiple = new();
    public nodeMetasByNodeId() {
    }
    public void ForEach(Func<int, uint, bool> executeAndBreakIfTrue) {
        foreach (var kv in _single) {
            executeAndBreakIfTrue(kv.Key, kv.Value);
        }
        foreach (var kv in _multiple) {
            foreach (var id in kv.Value) {
                if (executeAndBreakIfTrue(kv.Key, id)) break;
            }
        }
    }
    public bool TryGetFirstMetaId(int nodeId, out uint metaId) {
        if (_single.TryGetValue(nodeId, out metaId)) return true;
        if (_multiple.TryGetValue(nodeId, out var multipleMetaIds) && multipleMetaIds.Length > 0) {
            metaId = multipleMetaIds[0];
            return true;
        }
        metaId = default;
        return false;
    }
    public void Add(int nodeId, uint metaId) {
        if (_single.ContainsKey(nodeId) || _multiple.ContainsKey(nodeId)) {
            throw new Exception("Internal error. Attempting to add node meta id for node id that already has meta ids: " + nodeId);
        }
        _single[nodeId] = metaId;
    }
}

public class NodeTypesByIds {
    readonly Definition _definition;
    uint shortIdCounter = 0;
    readonly Dictionary<uint, metaAndType> _metaById = new();
    readonly Dictionary<metaAndType, uint> _idByMeta = new();
    readonly nodeMetasByNodeId _metaIdsByNodeId = new();
    //readonly Dictionary<int, uint[]> _nodeMetasByNodeId = new();    
    readonly Dictionary<Guid, int> _countByType = new();
    readonly Cache<QueryContextKey, idSet> _cachedNodeIdsByCtx;
    readonly NativeModelStore _nativeModelStore;
    internal NodeTypesByIds(Definition definition, NativeModelStore nativeModelStore) {
        _definition = definition;
        _cachedNodeIdsByCtx = new(1000); // TODO: Make this configurable
        _nativeModelStore = nativeModelStore;
    }
    idSet evaluateRelevantIds(Guid ctxTypeId, QueryContextKey ctxKey, DateTime nowUtc) {
        var ids = new idSet(nowUtc, ctxTypeId);
        var relevantMetaIds = _idByMeta
            .Where(kv => isMetaRelevantForContext(kv.Key, ctxTypeId, ctxKey, nowUtc))
            .Select(kv => kv.Value).ToHashSet();
        _metaIdsByNodeId.ForEach((nodeId, shortMetaId) => {
            if (relevantMetaIds.Contains(shortMetaId)) {
                var meta = _metaById[shortMetaId].Meta;
                ids.Add(nodeId, meta.ReleaseUtc, meta.ExpireUtc);
                return true; // break inner loop as we found a relevant meta for this node
            }
            return false;
        });
        return ids;
    }
    bool isMetaRelevantForContext(metaAndType mt, Guid ctxTypeId, QueryContextKey ctx, DateTime nowUtc) {
        var meta = mt.Meta;
        var typeId = mt.TypeId;
        var typeDef = _definition.NodeTypes[typeId].Model;
        if (ctx.ExcludeDecendants) {
            if (typeDef.Id != ctxTypeId) return false;
        } else {
            if (!typeDef.ThisAndDescendingTypes.ContainsKey(ctxTypeId)) return false;
        }
        if (!ctx.IncludeDeleted && meta.Deleted) return false;
        if (!ctx.IncludeCultureFallback) if (meta.CultureId != ctx.CultureId) return false;
        if (!ctx.IncludeUnpublished && !meta.AnyPublishedContentAnyDate) {
            if (meta.ReleaseUtc.HasValue && meta.ReleaseUtc.Value > nowUtc) return false;
            if (meta.ExpireUtc.HasValue && meta.ExpireUtc.Value <= nowUtc) return false;
        }
        if (!ctx.IncludeHidden && meta.Hidden) return false;
        if (ctx.CollectionIds != null && ctx.CollectionIds.Length > 0 && !ctx.CollectionIds.Contains(meta.CollectionId)) return false;
        if (ctx.MembershipIds != null && ctx.MembershipIds.Length > 0 && !ctx.MembershipIds.Contains(meta.ReadAccess)) return false;
        return true;
    }
    public IdSet GetAllNodeIdsForTypeFilteredByContext(Guid typeId, QueryContext ctx) {
        DateTime nowUtc = ctx.NowUtc ?? DateTime.UtcNow;
        var ctxKey = _nativeModelStore.GetQueryContextKey(ctx);
        if (_cachedNodeIdsByCtx.TryGet(ctxKey, out var ids)) {
            if (ids.IsWithinTimeConstraints(nowUtc)) return ids.AsUnmutableIdSet();
        }
        ids = evaluateRelevantIds(typeId, ctxKey, nowUtc); // takes time! // could consider lock here to avoid double eval
        _cachedNodeIdsByCtx.Set(ctxKey, ids, 1);
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
        if (_metaIdsByNodeId.TryGetFirstMetaId(id, out var metaId)) {
            typeId = _metaById[metaId].TypeId;
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
        metaAndType mt = new(node.Meta ?? NodeMeta.Empty, node.NodeType);
        if (!_idByMeta.TryGetValue(mt, out var shortId)) {
            if (shortIdCounter == short.MaxValue) throw new Exception("Internal error. Node meta short id overflow.");
            shortId = shortIdCounter++;
            _metaById[shortId] = mt;
            _idByMeta.Add(mt, shortId);
        }
        if (_metaIdsByNodeId.TryGetValue(node.__Id, out var shortIds)) {
            _metaIdsByNodeId[node.__Id] = [.. shortIds, shortId];
        } else {
            _metaIdsByNodeId.Add(node.__Id, [shortId]);
        }
        foreach (var kv in _cachedNodeIdsByCtx.AllNotThreadSafe()) {
            var ctx = kv.Key;
            var ids = kv.Value;
            if (isMetaRelevantForContext(mt, ids.CreatedWithTypeId, ctx, ids.CreatedWithNowUtc)) { // no time constraint
                kv.Value.Add(node.__Id, mt.Meta.ReleaseUtc, mt.Meta.ExpireUtc);
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
        var shortId = _idByMeta[new(node.Meta ?? NodeMeta.Empty, node.NodeType)];
        var shortIds = _metaIdsByNodeId[node.__Id];
        if (shortIds.Length == 1) {
            if (shortIds[0] != shortId) throw new Exception("Internal error. Attempting to deindex node meta that is not indexed for node id: " + node.__Id);
            _metaIdsByNodeId.Remove(node.__Id);
        } else {
            var newShortIds = new uint[shortIds.Length - 1];
            for (int i = 0, j = 0; i < shortIds.Length; i++) {
                if (shortIds[i] != shortId) newShortIds[j++] = shortIds[i];
            }
            if (newShortIds.Length == shortIds.Length) throw new Exception("Internal error. Attempting to deindex node meta that is not indexed for node id: " + node.__Id);
            _metaIdsByNodeId[node.__Id] = newShortIds;
        }
        foreach (var kv in _cachedNodeIdsByCtx.AllNotThreadSafe()) {
            var ctx = kv.Key;
            var ids = kv.Value;
            if (isMetaRelevantForContext(_metaById[shortId], ids.CreatedWithTypeId, ctx, ids.CreatedWithNowUtc)) {
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
    static int formatVersion = 1000;
    public void SaveState(IAppendStream stream) {
        stream.WriteInt(formatVersion);
        stream.WriteUInt(shortIdCounter);
        stream.WriteInt(_metaById.Count);
        foreach (var kv in _metaById) {
            stream.WriteUInt(kv.Key);
            stream.WriteByteArray(kv.Value.Meta.ToBytes());
            stream.WriteGuid(kv.Value.TypeId);
        }
        stream.WriteInt(_metaIdsByNodeId.Count);
        foreach (var kv in _metaIdsByNodeId) {
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
            var meta = NodeMeta.FromBytes(metaBytes);
            var typeId = stream.ReadGuid();
            var mt = new metaAndType(meta, typeId);
            _metaById[shortId] = mt;
            _idByMeta[mt] = shortId;
        }
        var nodeMetaCount = stream.ReadInt();
        for (int i = 0; i < nodeMetaCount; i++) {
            var nodeId = stream.ReadInt();
            var shortIdArrayLength = stream.ReadInt();
            var shortIds = new uint[shortIdArrayLength];
            for (int j = 0; j < shortIdArrayLength; j++) {
                shortIds[j] = stream.ReadUInt();
            }
            _metaIdsByNodeId[nodeId] = shortIds;
        }
        var countByTypeCount = stream.ReadInt();
        for (int i = 0; i < countByTypeCount; i++) {
            var typeId = stream.ReadGuid();
            var count = stream.ReadInt();
            _countByType[typeId] = count;
        }
    }
}

