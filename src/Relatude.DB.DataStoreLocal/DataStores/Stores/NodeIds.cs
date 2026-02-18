
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Stores;

// Could consider using short instead of uint for the ID of metas, to save memory.
// It is very unlikely to have more than 65k different metas

class idSet {
    readonly StateIdTracker _state = new();
    public long StateId { get => _state.Current; }
    HashSet<int> _ids = [];
    IdSet? _lastSet;
    DateTime _createdUsingNowUtc;
    public DateTime? ValidFrom;
    public DateTime? ValidTo;
    public idSet(DateTime nowUtc) {
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
class metaAndType(INodeMeta meta, Guid typeId) : IEquatable<metaAndType> {
    public readonly Guid TypeId = typeId;
    public readonly INodeMeta Meta = meta;
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
class ctxAndType(Guid ctxTypeId, QueryContextKey ctxKey) : IEquatable<ctxAndType> {
    public readonly Guid TypeId = ctxTypeId;
    public readonly QueryContextKey CxtKey = ctxKey;
    public bool Equals(ctxAndType? other) {
        if (other is null) return false;
        return TypeId == other.TypeId && CxtKey.Equals(other.CxtKey);
    }
    public override bool Equals(object? obj) {
        return obj is ctxAndType other && Equals(other);
    }
    public override int GetHashCode() {
        return HashCode.Combine(TypeId, CxtKey);
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
        if (_single.ContainsKey(nodeId)) {
            // move to multiple:
            var existingMetaId = _single[nodeId];
            _single.Remove(nodeId);
            _multiple[nodeId] = [existingMetaId, metaId];
        } else if (_multiple.TryGetValue(nodeId, out var existingMetaIds)) {
            _multiple[nodeId] = [.. existingMetaIds, metaId];
        } else {
            _single[nodeId] = metaId;
        }
    }
    public void Remove(int nodeId, uint metaId) {
        if (_single.TryGetValue(nodeId, out var existingMetaId)) {
            if (existingMetaId == metaId) {
                _single.Remove(nodeId);
            } else {
                throw new Exception("Internal error. Attempting to remove non-existing metaId from nodeId.");
            }
        } else if (_multiple.TryGetValue(nodeId, out var existingMetaIds)) {
            var newMetaIds = existingMetaIds.Where(id => id != metaId).ToArray();
            if (newMetaIds.Length == existingMetaIds.Length) {
                throw new Exception("Internal error. Attempting to remove non-existing metaId from nodeId.");
            } else if (newMetaIds.Length == 0) {
                _multiple.Remove(nodeId);
            } else if (newMetaIds.Length == 1) {
                _multiple.Remove(nodeId);
                _single[nodeId] = newMetaIds[0];
            } else {
                _multiple[nodeId] = newMetaIds;
            }
        } else {
            throw new Exception("Internal error. Attempting to remove metaId from nodeId that has no metas.");
        }
    }
    public void ReadFromStream(IReadStream stream) {
        var singleCount = stream.ReadInt();
        for (int i = 0; i < singleCount; i++) {
            var nodeId = stream.ReadInt();
            var metaId = stream.ReadUInt();
            _single[nodeId] = metaId;
        }
        var multipleCount = stream.ReadInt();
        for (int i = 0; i < multipleCount; i++) {
            var nodeId = stream.ReadInt();
            var metaIdCount = stream.ReadInt();
            var metaIds = new uint[metaIdCount];
            for (int j = 0; j < metaIdCount; j++) {
                metaIds[j] = stream.ReadUInt();
            }
            _multiple[nodeId] = metaIds;
        }
    }
    public void WriteToStream(IAppendStream stream) {
        stream.WriteInt(_single.Count);
        foreach (var kv in _single) {
            stream.WriteInt(kv.Key);
            stream.WriteUInt(kv.Value);
        }
        stream.WriteInt(_multiple.Count);
        foreach (var kv in _multiple) {
            stream.WriteInt(kv.Key);
            stream.WriteInt(kv.Value.Length);
            foreach (var metaId in kv.Value) {
                stream.WriteUInt(metaId);
            }
        }
    }
}
internal class NodeTypesByIds {
    readonly Definition _definition;
    uint shortIdCounter = 0;
    readonly Dictionary<uint, metaAndType> _metaById = new();
    readonly Dictionary<metaAndType, uint> _idByMeta = new();
    readonly nodeMetasByNodeId _metaIdsByNodeId = new();
    readonly Dictionary<Guid, int> _countByType = new();
    readonly Cache<ctxAndType, idSet> _cachedNodeIdsByCtx;
    readonly NativeModelStore _nativeModelStore;
    internal NodeTypesByIds(Definition definition, NativeModelStore nativeModelStore) {
        _definition = definition;
        _cachedNodeIdsByCtx = new(1000); // TODO: Make this configurable
        _nativeModelStore = nativeModelStore;
    }
    bool isReleased(DateTime nowUtc, INodeMeta meta) {
        if (meta.ReleaseUtc.HasValue && nowUtc < meta.ReleaseUtc.Value) return false;
        if (meta.ExpireUtc.HasValue && nowUtc >= meta.ExpireUtc.Value) return false;
        return true;
    }
    idSet evaluateRelevantIds(Guid ctxTypeId, QueryContextKey ctxKey, DateTime nowUtc) {
        var ids = new idSet(nowUtc);
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
        if (ctx.ExcludeDecendants) {
            if (typeId != ctxTypeId) return false;
        } else {
            var ctxTypeDef = _definition.NodeTypes[ctxTypeId].Model;
            if (!ctxTypeDef.ThisAndDescendingTypes.ContainsKey(typeId)) 
                return false;
        }
        if (!ctx.IncludeDeleted && meta.Deleted) return false;
        if (!ctx.IncludeCultureFallback) if (meta.CultureId != ctx.CultureId) return false;
        if (!ctx.IncludeUnpublished && !meta.AnyPublishedContentAnyDate) {
            if(!isReleased(nowUtc, meta)) return false;
        }
        if (!ctx.IncludeHidden && meta.Hidden) return false;
        if (ctx.CollectionIds != null && ctx.CollectionIds.Length > 0 && !ctx.CollectionIds.Contains(meta.CollectionId)) return false;

        // Access control:
        switch (ctx.UserType) {
            case Native.SystemUserType.Anonymous:
                if (meta.ReadAccess != Guid.Empty) return false;
                break;
            case Native.SystemUserType.User:
                if (!ctx.IsMember(meta.ReadAccess)) return false;
                if (ctx.EditView && !ctx.IsMember(meta.EditViewAccess)) return false;
                break;
            case Native.SystemUserType.Admin: // admins have access to everything
                break;
            default:
                throw new Exception("Internal error. Unknown system user type: " + ctx.UserType);
        }

        return true;
    }
    public IdSet GetAllNodeIdsForTypeFilteredByContext(Guid typeId, QueryContext ctx) {
        var ctxKey = _nativeModelStore.GetQueryContextKey(ctx, out var nowUtc);
        var ctxAndTypeKey = new ctxAndType(typeId, ctxKey);
        if (_cachedNodeIdsByCtx.TryGet(ctxAndTypeKey, out var ids)) {
            if (ids.IsWithinTimeConstraints(nowUtc)) return ids.AsUnmutableIdSet();
        }
        ids = evaluateRelevantIds(typeId, ctxKey, nowUtc); // takes time! // could consider lock here to avoid double eval
        _cachedNodeIdsByCtx.Set(ctxAndTypeKey, ids, 1);
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
    public void Index(INodeDataInner node) {
        if (node is not NodeData && node is not NodeDataRevision) {
            throw new Exception("Internal error. Attempting to deindex unsupported node data type: " + node.GetType().FullName);
            // must be root node data type, not a sub version or id type
        }
        metaAndType mt = new(node.Meta ?? INodeMeta.Empty, node.NodeType);
        if (!_idByMeta.TryGetValue(mt, out var shortId)) {
            if (shortIdCounter == short.MaxValue) throw new Exception("Internal error. Node meta short id overflow.");
            shortId = shortIdCounter++;
            _metaById[shortId] = mt;
            _idByMeta.Add(mt, shortId);
        }
        _metaIdsByNodeId.Add(node.__Id, shortId);
        foreach (var kv in _cachedNodeIdsByCtx.AllNotThreadSafe()) {
            var ctx = kv.Key;
            var ids = kv.Value;
            if (isMetaRelevantForContext(mt, ctx.TypeId, ctx.CxtKey, ids.CreatedWithNowUtc)) { // no time constraint
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
        if (node is not NodeData && node is not NodeDataRevision) {
            throw new Exception("Internal error. Attempting to deindex unsupported node data type: " + node.GetType().FullName);
            // must be root node data type, not a sub version or id type
        }
        var shortId = _idByMeta[new(node.Meta ?? INodeMeta.Empty, node.NodeType)];
        _metaIdsByNodeId.Remove(node.__Id, shortId);
        foreach (var kv in _cachedNodeIdsByCtx.AllNotThreadSafe()) {
            var ctx = kv.Key;
            var ids = kv.Value;
            if (isMetaRelevantForContext(_metaById[shortId], ctx.TypeId, ctx.CxtKey, ids.CreatedWithNowUtc)) {
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

    public INodeDataOuter PickBestOuter(INodeDataInner node, QueryContextKey ctxKey, DateTime nowUtc) {

        // no access control, type, deteleted, as this is already taken care of in source of ids: nodesIds or relation
        // this is just picking the best revision for the context, which is based on culture and release/expire date

        if (node is INodeDataOuter nd) return nd; // no revisions, return as is

        if (node is not NodeDataRevisions ndr) throw new Exception("Internal error, expected NodeDataRevisions");

        // first, look for references to specified revisions: ( typically used for previewing unpublished content )
        if (ctxKey.SelectedRevisions != null) {
            foreach (var rev in ctxKey.SelectedRevisions) {
                if (rev.NodeId == node.Id) {
                    var r = ndr.Revisions.FirstOrDefault(r => r.RevisionId == rev.RevisionId);
                    if (r != null) return r;
                }
            }
        }

        // then any published revision with matching culture
        foreach (var r in ndr.Revisions) {
            if (
                r.RevisionType == RevisionType.Published
                && r.Meta!.CultureId == ctxKey.CultureId
                && isReleased(nowUtc, r.Meta)
                ) {
                return r;
            }
        }

        // then any published revision with fallback culture
        if (ctxKey.IncludeCultureFallback) {
            var match = ndr.Revisions
                .Where(r => r.RevisionType == RevisionType.Published && isReleased(nowUtc, r.Meta!))
                .OrderBy(r => _definition.GetCulturePriority(r.Meta!.CultureId, r.Meta!.CollectionId))
                .FirstOrDefault();
            if (match != null) return match;
        }

        throw new InvalidOperationException("No suitable revision found for node " + node.__Id + " and context " + ctxKey);
    }
    static int formatVersion = 1000;
    public void SaveState(IAppendStream stream) {
        stream.WriteInt(formatVersion);
        stream.WriteUInt(shortIdCounter);
        stream.WriteInt(_metaById.Count);
        foreach (var kv in _metaById) {
            stream.WriteUInt(kv.Key);
            stream.WriteByteArray(INodeMeta.ToBytes(kv.Value.Meta));
            stream.WriteGuid(kv.Value.TypeId);
        }
        _metaIdsByNodeId.WriteToStream(stream);
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
            var meta = INodeMeta.FromBytes(metaBytes);
            if (meta == null) meta = INodeMeta.Empty;
            var typeId = stream.ReadGuid();
            var mt = new metaAndType(meta, typeId);
            _metaById[shortId] = mt;
            _idByMeta[mt] = shortId;
        }
        _metaIdsByNodeId.ReadFromStream(stream);
        var countByTypeCount = stream.ReadInt();
        for (int i = 0; i < countByTypeCount; i++) {
            var typeId = stream.ReadGuid();
            var count = stream.ReadInt();
            _countByType[typeId] = count;
        }
    }

}