using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.DataStores.Stores;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.IO;
using Relatude.DB.Native;
namespace Relatude.DB.DataStores.Indexes.Meta;

// Could consider using short instead of uint for the ID of metas, to save memory.
// It is very unlikely to have more than 65k different metas

class idSet {
    readonly StateIdTracker _state = new();
    public long StateId { get => _state.Current; }
    readonly MutableSet _ids = new(); // becomes a bit set once large: a few bits per node instead of a HashSet entry
    IdSet? _lastSet;
    DateTime _createdUsingNowUtc;
    public DateTime? ValidFrom;
    public DateTime? ValidTo;
    public idSet(DateTime nowUtc) {
        _createdUsingNowUtc = nowUtc;
    }
    public DateTime CreatedWithNowUtc => _createdUsingNowUtc;
    public void Add(int id) {
        _ids.Add(id);
        _state.RegisterAddition(id);
        _lastSet = null;
    }
    public void Remove(int id) {
        _ids.Remove(id);
        _state.RegisterRemoval(id);
        _lastSet = null;
        // cannot update time constraints on removal, so just keep interval the smallest it has been
    }
    /// <summary>
    /// Narrows the time the set is valid for to the time a node's answer holds (isMetaRelevantForContext): from the
    /// release or expiry it last changed at, to the one it next changes at.
    /// </summary>
    public void Bound(DateTime? validFrom, DateTime? validTo) {
        if (validFrom.HasValue && (!ValidFrom.HasValue || validFrom.Value > ValidFrom.Value)) ValidFrom = validFrom.Value;
        if (validTo.HasValue && (!ValidTo.HasValue || validTo.Value < ValidTo.Value)) ValidTo = validTo.Value;
    }
    /// <summary>When a reader last took the set, to keep the sets in use (Environment.TickCount64).</summary>
    public long LastUsed;
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
/// <summary>
/// The sets of one context and type, each valid for a time of its own, so readers at different times (a preview of a
/// later date beside the live site) keep a set each rather than replacing one another's.
/// </summary>
class idSets {
    const int max = 3;
    idSet[] _sets = [];
    /// <summary>Every set held, for writes, which run under the store's write lock.</summary>
    public idSet[] All => _sets;
    public idSet? For(DateTime nowUtc) {
        foreach (var set in _sets) {
            if (!set.IsWithinTimeConstraints(nowUtc)) continue;
            set.LastUsed = Environment.TickCount64;
            return set;
        }
        return null;
    }
    public void Add(idSet set) {
        set.LastUsed = Environment.TickCount64;
        lock (this) {
            // sets that only a reader of the past could use go first, then the least recently used:
            var now = DateTime.UtcNow;
            var kept = _sets.Where(s => !(s.ValidTo <= now)).OrderByDescending(s => s.LastUsed).Take(max - 1);
            _sets = [set, .. kept];
        }
    }
}
/// <summary>
/// See <see cref="NodeTypesByIds.Readable"/>. For one read, under the store's read lock, on one thread: the sets it
/// takes are the cache's as they were then.
/// </summary>
internal sealed class ReadableIds {
    readonly NodeTypesByIds _index;
    readonly QueryContextKey _key;
    readonly DateTime _nowUtc;
    // the set of the one type a relation end holds, its descendants included, when the end has one: a node of it is
    // then a single lookup, whatever its own type (the set of a type holds a node exactly when its own type's set does)
    readonly Guid _endType;
    readonly HashSet<Guid>? _endTypes;
    IdSet? _end;
    Guid _lastType;
    IdSet? _lastSet;
    Dictionary<Guid, IdSet>? _byType;
    internal ReadableIds(NodeTypesByIds index, QueryContextKey key, DateTime nowUtc, Guid endType, HashSet<Guid>? endTypes) {
        _index = index;
        _key = key;
        _nowUtc = nowUtc;
        // what NodeTypesByIds.isMetaRelevantForContext lets through whatever the meta, so every node is read
        // (QueryContext.AllIncludingDescendants): the type need not be asked, as a node is tested against its own type's set
        ReadsAll = key.IncludeDeleted && key.IncludeUnpublished && key.IncludeHidden && key.IncludeCultureFallback && !key.OnlyWithCulture
            && key.CollectionIds is not { Length: > 0 } && key.UserType == SystemUserType.Admin;
        if (endType != Guid.Empty && endTypes != null && !key.ExcludeDescendants) { // excluding descendants, a type's set holds that type alone
            _endType = endType;
            _endTypes = endTypes;
        }
    }
    /// <summary>Whether the context reads every node, whatever its meta, so nothing need be asked.</summary>
    public bool ReadsAll { get; }
    public bool Has(int id) {
        if (ReadsAll) return true;
        if (_endTypes != null) {
            if ((_end ??= setOf(_endType)).Has(id)) return true;
            // not read, unless it is of a type the end does not hold (related before the datamodel changed)
            return _index.TryGetType(id, out var other) && !_endTypes.Contains(other) && setOf(other).Has(id);
        }
        return _index.TryGetType(id, out var typeId) && setOf(typeId).Has(id);
    }
    IdSet setOf(Guid typeId) {
        if (_lastSet != null && typeId == _lastType) return _lastSet;
        _byType ??= [];
        if (!_byType.TryGetValue(typeId, out var ids)) _byType[typeId] = ids = _index.GetAllNodeIdsForTypeFilteredByContext(typeId, _key, _nowUtc);
        _lastType = typeId;
        return _lastSet = ids;
    }
}
public class metaAndType(IInnerNodeMeta meta, Guid typeId) : IEquatable<metaAndType> {
    public readonly Guid TypeId = typeId;
    public readonly IInnerNodeMeta Meta = meta;
    public bool Equals(metaAndType? other) {
        if (other is null)
            return false;
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
    ValueByIdMap<uint> _single = new();
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
        // Console.WriteLine("Adding metaId " + metaId + " to nodeId " + nodeId);
        if (_single.TryGetValue(nodeId, out var existingMetaId)) {
            // move to multiple:
            _single.Remove(nodeId);
            _multiple[nodeId] = [existingMetaId, metaId];
        } else if (_multiple.TryGetValue(nodeId, out var existingMetaIds)) {
            _multiple[nodeId] = [.. existingMetaIds, metaId];
        } else {
            _single.Set(nodeId, metaId);
        }
    }
    public void Remove(int nodeId, uint metaId) {
        // Console.WriteLine("Removing metaId " + metaId + " from nodeId " + nodeId);
        if (_single.TryGetValue(nodeId, out var existingMetaId)) {
            if (existingMetaId == metaId) {
                _single.Remove(nodeId);
            } else {
                throw new Exception("Internal error. Attempting to remove non-existing metaId from nodeId.");
            }
        } else if (_multiple.TryGetValue(nodeId, out var existingMetaIds)) {
            // remove exactly ONE occurrence, as duplicates are legal (two identical revisions of one node):
            var indexToRemove = Array.IndexOf(existingMetaIds, metaId);
            if (indexToRemove < 0) {
                throw new Exception("Internal error. Attempting to remove non-existing metaId from nodeId.");
            }
            var newMetaIds = existingMetaIds.Where((_, i) => i != indexToRemove).ToArray();
            if (newMetaIds.Length == 0) {
                _multiple.Remove(nodeId);
            } else if (newMetaIds.Length == 1) {
                _multiple.Remove(nodeId);
                _single.Set(nodeId, newMetaIds[0]);
            } else {
                _multiple[nodeId] = newMetaIds;
            }
        } else {
            throw new Exception("Internal error. Attempting to remove metaId from nodeId that has no metas.");
        }
    }
    public void ReadFromStream(IReadStream stream) {
        var singleCount = stream.ReadInt();
        _single = new(singleCount);
        for (int i = 0; i < singleCount; i++) {
            var nodeId = stream.ReadInt();
            var metaId = stream.ReadUInt();
            _single.Set(nodeId, metaId);
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
    uint metaIdCounter = 0;
    readonly Dictionary<uint, metaAndType> _metaById = new();
    readonly Dictionary<metaAndType, uint> _idByMeta = new();
    readonly nodeMetasByNodeId _metaIdsByNodeId = new();
    readonly Dictionary<Guid, int> _countByType = new();
    readonly Cache<ctxAndType, idSets> _cachedNodeIdsByCtx;
    readonly NativeModelStore _nativeModelStore;
    readonly SettingsLocal _settings;
    readonly Dictionary<Guid, Guid[]> _embeddedPropertiesByType; // only the types that have any
    internal NodeTypesByIds(Definition definition, NativeModelStore nativeModelStore, SettingsLocal settings) {
        _definition = definition;
        _cachedNodeIdsByCtx = new(1000); // TODO: Make this configurable
        _nativeModelStore = nativeModelStore;
        _settings = settings;
        _embeddedPropertiesByType = definition.NodeTypes
            .Select(kv => (typeId: kv.Key, propertyIds: kv.Value.AllProperties.Values.Where(p => p.PropertyType == PropertyType.Embedded).Select(p => p.Id).ToArray()))
            .Where(t => t.propertyIds.Length > 0)
            .ToDictionary(t => t.typeId, t => t.propertyIds);
    }
    bool isReleased(DateTime nowUtc, IInnerNodeMeta? meta) {
        if (meta == null) return true;
        if (meta.ReleaseUtc.HasValue && nowUtc < meta.ReleaseUtc.Value) return false;
        if (meta.ExpireUtc.HasValue && nowUtc >= meta.ExpireUtc.Value) return false;
        return true;
    }
    idSet evaluateRelevantIds(Guid ctxTypeId, QueryContextKey ctxKey, DateTime nowUtc) {
        var ids = new idSet(nowUtc);
        var relevantMetaIds = new HashSet<uint>();
        Dictionary<uint, (DateTime? From, DateTime? To)>? boundsByMetaId = null; // metas whose answer changes at a release or an expiry
        foreach (var kv in _idByMeta) {
            if (isMetaRelevantForContext(kv.Key, ctxTypeId, ctxKey, nowUtc, out var validFrom, out var validTo)) relevantMetaIds.Add(kv.Value);
            if (validFrom.HasValue || validTo.HasValue) (boundsByMetaId ??= []).TryAdd(kv.Value, (validFrom, validTo));
        }
        _metaIdsByNodeId.ForEach((nodeId, shortMetaId) => {
            // a node read or left out until a release or an expiry bounds the time the set is valid for:
            if (boundsByMetaId != null && boundsByMetaId.TryGetValue(shortMetaId, out var bounds)) ids.Bound(bounds.From, bounds.To);
            if (relevantMetaIds.Contains(shortMetaId)) {
                ids.Add(nodeId);
                return true; // break inner loop as we found a relevant meta for this node
            }
            return false;
        });
        return ids;
    }

    bool isMetaRelevantForContext(metaAndType mt, Guid ctxTypeId, QueryContextKey ctx, DateTime nowUtc)
        => isMetaRelevantForContext(mt, ctxTypeId, ctx, nowUtc, out _, out _);
    // validFrom and validTo are the time the answer holds, for a caller bounding the time a cached set is valid for: the
    // release or expiry the answer last changed at, and the one it next changes at. Only a release or an expiry that
    // decides the answer bounds it, so none does in a context reading unpublished content, which ignores them:
    bool isMetaRelevantForContext(metaAndType mt, Guid ctxTypeId, QueryContextKey ctx, DateTime nowUtc, out DateTime? validFrom, out DateTime? validTo) {
        validFrom = null;
        validTo = null;
        var meta = mt.Meta;

        // Filter based on node type
        var typeId = mt.TypeId;
        if (ctx.ExcludeDescendants) {
            if (typeId != ctxTypeId) return false;
        } else {
            var ctxTypeDef = _definition.NodeTypes[ctxTypeId].Model;
            if (!ctxTypeDef.ThisAndDescendingTypes.ContainsKey(typeId))
                return false;
        }

        // Showing deleted?
        if (!ctx.IncludeDeleted && meta.Deleted) return false;

        // A revision that is not published - a draft, an archived one - is read in edit view only, or the reader would
        // find a node it has no revision of to show (PickBestOuter):
        if (!ctx.IncludeUnpublished && meta.RevisionType != RevisionType.Published) return false;

        // Culture:
        if (ctx.OnlyWithCulture && meta.CultureId == Guid.Empty) return false;
        if (!ctx.IncludeCultureFallback) if (meta.CultureId != ctx.CultureId) return false;

        // Collection:
        if (ctx.CollectionIds != null && ctx.CollectionIds.Length > 0 && !ctx.CollectionIds.Contains(meta.CollectionId)) return false;

        NodeTypeModel? typeDef = null;
        Guid readAccessGroup;
        Guid editViewAccessGroup;
        if (meta.ReadAccess != NodeConstants.UserGroupUnspecified && meta.EditViewAccess != NodeConstants.UserGroupUnspecified) {
            readAccessGroup = meta.ReadAccess;
            editViewAccessGroup = meta.EditViewAccess;
        } else {
            typeDef = _definition.NodeTypes[typeId].Model; // only look up typeDef if needed, as it is a bit expensive to look up
            readAccessGroup = FindUserGroup(meta.ReadAccess, typeDef.DefaultReadAccess, _settings.DefaultReadAccess);
            editViewAccessGroup = FindUserGroup(meta.EditViewAccess, typeDef.DefaultEditViewAccess, _settings.DefaultReadAccess);
        }

        // Read Access:
        if (!IsUserAMember(readAccessGroup, ctx.UserType, ctx.MembershipIds)) return false;

        // View Access Edit:
        if (ctx.EditView && !IsUserAMember(editViewAccessGroup, ctx.UserType, ctx.MembershipIds)) return false;

        // Include hidden:
        if (typeDef == null) typeDef = _definition.NodeTypes[typeId].Model;
        if (!ctx.IncludeHidden && typeDef.Hidden) return false;

        // Release/expire date, last, so a date it gives is one the answer changes at, not one of a node left out anyway:
        if (!ctx.IncludeUnpublished) {
            if (meta.ReleaseUtc.HasValue) {
                if (nowUtc < meta.ReleaseUtc.Value) { validTo = meta.ReleaseUtc.Value; return false; } // read from its release
                validFrom = meta.ReleaseUtc.Value; // not read before it
            }
            if (meta.ExpireUtc.HasValue) {
                if (nowUtc >= meta.ExpireUtc.Value) { validFrom = meta.ExpireUtc.Value; return false; } // read until its expiry
                validTo = meta.ExpireUtc.Value; // not read from it
            }
        }

        return true;
    }
    // the node's own group, else the type's default, else the database's (shared with the write access check)
    internal static Guid FindUserGroup(Guid fromMeta, Guid fromType, SystemGroupType fromSettings) {
        if (fromMeta != NodeConstants.UserGroupUnspecified) return fromMeta;
        if (fromType != NodeConstants.UserGroupUnspecified) return fromType;
        return fromSettings switch {
            SystemGroupType.Everyone => NodeConstants.UserGroupEveryone,
            SystemGroupType.Member => NodeConstants.UserGroupMember,
            SystemGroupType.Admins => NodeConstants.UserGroupAdmins,
            _ => throw new InvalidOperationException(),
        };
    }
    internal static bool IsUserAMember(Guid requiredMembership, SystemUserType userType, Guid[]? userMemberships) {

        if (requiredMembership == NodeConstants.UserGroupEveryone)
            return true; // everyone is member of the "everyone" group

        if (userType == SystemUserType.Admin)
            return true; // admins are member of all groups

        if (requiredMembership == NodeConstants.UserGroupAdmins)
            return false; // only admins can be members of the "admins" group

        if (requiredMembership == NodeConstants.UserGroupMember
            && userType != SystemUserType.Anonymous)
            return true; // any authenticated user is member of the "any member"

        // from here on we know that the required membership is not "everyone" or "admins",
        // and that the user is not an admin so a specific membership is required

        if (userType == SystemUserType.Anonymous)
            return false; // anonymous users does not have any memberships


        if (userMemberships == null || userMemberships.Length == 0)
            return false; // if user has no memberships, they cannot be member of the required group

        foreach (var userMembership in userMemberships) {
            if (userMembership == requiredMembership)
                return true; // the user is a member of the required group
        }

        return false; // the users memberships does not include the required group

    }
    public IdSet GetAllNodeIdsForTypeFilteredByContext(Guid typeId, QueryContext ctx) {
        var key = _nativeModelStore.GetQueryContextKey(ctx, out var nowUtc);
        return GetAllNodeIdsForTypeFilteredByContext(typeId, key, nowUtc);
    }
    /// <summary>The same with the context already resolved, for a caller asking about several types in one context.</summary>
    public IdSet GetAllNodeIdsForTypeFilteredByContext(Guid typeId, QueryContextKey key, DateTime nowUtc) {
        var ids = getIds(typeId, key, nowUtc).AsUnmutableIdSet();
        return key.SelectedRevisions is { Length: > 0 } && !key.IncludeUnpublished ? withSelectedRevisions(ids, typeId, key, nowUtc) : ids;
    }
    /// <summary>
    /// Which nodes a context reads, asked one node at a time, as a read that walks a relation asks: it takes the related
    /// ids in the relation's order, which intersecting with a set would not keep. The context is resolved once and each
    /// set taken from the cache once. Given the one type a relation end holds (<paramref name="endType"/>, and with its
    /// descendants <paramref name="endTypes"/>), a node costs a lookup in that type's set; otherwise its type and a
    /// lookup in that type's set.
    /// </summary>
    public ReadableIds Readable(QueryContext ctx, Guid endType = default, HashSet<Guid>? endTypes = null) {
        var key = _nativeModelStore.GetQueryContextKey(ctx, out var nowUtc);
        return new(this, key, nowUtc, endType, endTypes);
    }
    idSet getIds(Guid typeId, QueryContext ctx) => getIds(typeId, _nativeModelStore.GetQueryContextKey(ctx, out var nowUtc), nowUtc);
    idSet getIds(Guid typeId, QueryContextKey key, DateTime nowUtc) {
        // the same ids whichever revisions the context selects: one cached set for them all
        var ctxKey = key.WithoutSelectedRevisions();
        var ctxAndTypeKey = new ctxAndType(typeId, ctxKey);
        if (!_cachedNodeIdsByCtx.TryGet(ctxAndTypeKey, out var sets)) {
            sets = new();
            if (!_cachedNodeIdsByCtx.TryAdd(ctxAndTypeKey, sets, 1) && _cachedNodeIdsByCtx.TryGet(ctxAndTypeKey, out var added)) sets = added;
        }
        if (sets.For(nowUtc) is { } ids) return ids;
        ids = evaluateRelevantIds(typeId, ctxKey, nowUtc); // takes time! // could consider lock here to avoid double eval
        sets.Add(ids);
        return ids;
    }
    /// <summary>
    /// The ids with the nodes added whose revision the context selects (QueryContext.SelectedRevisions) when the
    /// context would read that revision were it published and released: a selected revision is read whatever its
    /// publication, as a read by id reads it (TryPickBestOuter), so a preview of a draft finds a node with no published
    /// version in queries and relation ends too. Every other check stands: type, deletion, culture, collection, read
    /// access, hidden types. Most sets lack none of them, and are answered as they are.
    /// </summary>
    IdSet withSelectedRevisions(IdSet ids, Guid typeId, QueryContextKey key, DateTime nowUtc) {
        var store = _definition.Store;
        QueryContextKey? anyPublication = null;
        foreach (var selected in key.SelectedRevisions!) {
            if (!store._guids.TryGetId(selected.NodeId, out var id) || ids.Has(id)) continue;
            // the type first, as relation ends ask for the set of each related node's type: no read for most
            if (!TryGetType(id, out var nodeTypeId)) continue;
            if (key.ExcludeDescendants ? nodeTypeId != typeId : !_definition.NodeTypes[typeId].Model.ThisAndDescendingTypes.ContainsKey(nodeTypeId)) continue;
            if (store._nodes.Get(id, out _) is not NodeDataRevisions node) continue; // a node without revisions has none to select
            var revision = node.Revisions.FirstOrDefault(r => r.RevisionKey == selected.RevisionId);
            if (revision == null) continue;
            anyPublication ??= new(key.CultureId, key.CollectionIds, key.MembershipIds, key.IncludeDeleted, key.IncludeCultureFallback, key.OnlyWithCulture,
                includeUnpublished: true, key.EditView, key.IncludeHidden, key.ExcludeDescendants, key.UserType, null);
            if (!isMetaRelevantForContext(new(revision.Meta ?? IInnerNodeMeta.Empty, revision.NodeType), typeId, anyPublication, nowUtc)) continue;
            ids = _definition.Sets.Union(ids, _definition.Sets.SingleValueIdSet(id));
        }
        return ids;
    }
    /// <summary>
    /// How many nodes of the type and its descendants are stored, whatever their meta says (unpublished,
    /// hidden, marked deleted); a node with several revisions counts once. Read off the cached set without
    /// copying it, which the set kept up to date by every add and remove makes cheap after the first call.
    /// </summary>
    public int CountAllNodesIncludingDescendants(Guid typeId) => getIds(typeId, QueryContext.AllIncludingDescendants).Count;
    public IdSet GetAllNodeIdsForTypeNoFilter(Guid typeId, bool includeDescendants) {
        return GetAllNodeIdsForTypeFilteredByContext(typeId, includeDescendants ? QueryContext.AllIncludingDescendants : QueryContext.AllExcludingDescendants);
    }
    public int GetCountForTypeForStatusInfo(Guid typeId) {
        if (_countByType.TryGetValue(typeId, out var count)) return count;
        return 0;
    }
    /// <summary>Every stored revision, a node without revisions counting as one: the counts by type added up, kept as
    /// they change so it is a field to read, without a lock, for the counters the dashboard polls (PeekCounters).</summary>
    public long RevisionCount => _revisionCount;
    long _revisionCount;
    // Two more counts of what is stored now, kept as the counts by type are: up on every add, down on every remove, an
    // update being a remove and an add. They are saved in the main state file (DataStoreLocal.saveMainState), not in
    // this index's own section, whose format version cannot change without every index being rebuilt.
    /// <summary>The embedded inner nodes of every revision, nested ones included: each revision holds its own copy.</summary>
    public long EmbeddedNodeCount { get; internal set; }
    /// <summary>The nodes marked deleted in their meta, which stay stored until they are deleted for good.</summary>
    public int MarkedDeletedNodeCount { get; internal set; }
    void countEmbeddedAndMarkedDeleted(INodeData node, int sign) {
        var markedDeleted = false;
        if (node is NodeDataRevisions ndr) {
            foreach (var rev in ndr.Revisions) {
                EmbeddedNodeCount += sign * countEmbedded(rev);
                markedDeleted |= rev.Meta?.Deleted == true;
            }
        } else {
            EmbeddedNodeCount += sign * countEmbedded(node);
            markedDeleted = node.Meta?.Deleted == true;
        }
        if (markedDeleted) MarkedDeletedNodeCount += sign;
    }
    long countEmbedded(INodeData node) {
        if (!_embeddedPropertiesByType.TryGetValue(node.NodeType, out var propertyIds)) return 0; // most types have none: one lookup
        long count = 0;
        foreach (var propertyId in propertyIds) {
            if (!node.TryGetValue(propertyId, out var value) || value is not IInnerNodeDataMap map) continue;
            foreach (var inner in map) count += 1 + countEmbedded(inner); // enumerated: map.Count builds the map's key index
        }
        return count;
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
                if (na.Node is NodeData nd) index(nd.__Id, nd.NodeType, nd.Meta, null);
                else if (na.Node is NodeDataRevisions ndr) {
                    var addedTo = new HashSet<idSet>(); // once into each cached set, as in Index
                    foreach (var rev in ndr.Revisions) {
                        index(rev.__Id, rev.NodeType, rev.Meta, addedTo);
                    }
                } else throw new Exception("Internal error. Unsupported node data type in primitive action during state load: " + na.Node.GetType().FullName);
                countEmbeddedAndMarkedDeleted(na.Node, 1);
            } else if (na.Operation == PrimitiveOperation.Remove) {
                if (na.Node is NodeData nd) deIndex(nd.__Id, nd.NodeType, nd.Meta);
                else if (na.Node is NodeDataRevisions ndr) {
                    foreach (var rev in ndr.Revisions) {
                        deIndex(rev.__Id, rev.NodeType, rev.Meta);
                    }
                } else throw new Exception("Internal error. Unsupported node data type in primitive action during state load: " + na.Node.GetType().FullName);
                countEmbeddedAndMarkedDeleted(na.Node, -1); // a remove in the log carries the whole node it removed
            }
        } catch (Exception ex) {
            if (throwOnErrors) throw;
            log("Error registering node action during state load: " + na, ex);
        }
    }
    public string asText() {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("NodeTypesByIds:");
        sb.AppendLine("MetaById:");
        sb.AppendLine(
            string.Join(Environment.NewLine, _idByMeta.Select(kv => "  " + kv.Key.TypeId + " meta: " + kv.Key.Meta))
        );
        return sb.ToString();
    }
    /// <summary>Whether a node, or a revision of one, is in the culture.</summary>
    public bool AnyInCulture(Guid cultureId) {
        var inCulture = _metaById.Where(kv => kv.Value.Meta.CultureId == cultureId).Select(kv => kv.Key).ToHashSet();
        if (inCulture.Count == 0) return false;
        var found = false;
        _metaIdsByNodeId.ForEach((nodeId, metaId) => found |= inCulture.Contains(metaId));
        return found;
    }
    public void Index(INodeData node) {
        if (node is NodeData nd) index(nd.__Id, nd.NodeType, nd.Meta, null);
        else if (node is NodeDataRevisions ndr) {
            // the node goes into a cached context's ids once, whichever of its revisions makes it one the context reads:
            // a set holds a node once, as one made afresh does (evaluateRelevantIds)
            var addedTo = new HashSet<idSet>();
            foreach (var rev in ndr.Revisions) {
                index(rev.__Id, rev.NodeType, rev.Meta, addedTo);
            }
        } else throw new Exception("Internal error. Unsupported node data type in primitive action during indexing: " + node.GetType().FullName);
        countEmbeddedAndMarkedDeleted(node, 1);
    }
    void index(int nodeId, Guid nodeTypeId, IInnerNodeMeta? meta, HashSet<idSet>? addedTo) {
        metaAndType mt = new(meta ?? IInnerNodeMeta.Empty, nodeTypeId);
        //Console.WriteLine("Indexing node " + node.__Id + " meta: " + node.Meta);
        if (!_idByMeta.TryGetValue(mt, out var shortId)) {
#if DEBUG
            if (metaIdCounter == 10000) throw new Exception("Warning: To many metas?");
#endif
            shortId = metaIdCounter++;
            _metaById[shortId] = mt;
            _idByMeta.Add(mt, shortId);
        }
        _metaIdsByNodeId.Add(nodeId, shortId);
        foreach (var kv in _cachedNodeIdsByCtx.AllNotThreadSafe()) {
            var ctx = kv.Key;
            foreach (var ids in kv.Value.All) {
                // judged at the time the set was made, so it stays the set of that time, valid only while the answer holds:
                var relevant = isMetaRelevantForContext(mt, ctx.TypeId, ctx.CxtKey, ids.CreatedWithNowUtc, out var validFrom, out var validTo);
                ids.Bound(validFrom, validTo);
                if (relevant && (addedTo == null || addedTo.Add(ids))) ids.Add(nodeId);
            }
        }
        if (_countByType.TryGetValue(nodeTypeId, out var count)) {
            _countByType[nodeTypeId] = count + 1;
        } else {
            _countByType[nodeTypeId] = 1;
        }
        _revisionCount++;
    }
    public void DeIndex(INodeData node) {
        if (node is NodeData nd) deIndex(nd.__Id, nd.NodeType, nd.Meta);
        else if (node is NodeDataRevisions ndr) {
            foreach (var rev in ndr.Revisions) {
                deIndex(rev.__Id, rev.NodeType, rev.Meta);
            }
        } else throw new Exception("Internal error. Unsupported node data type in primitive action during state load: " + node.GetType().FullName);
        countEmbeddedAndMarkedDeleted(node, -1);
    }
    void deIndex(int nodeId, Guid nodeTypeId, IInnerNodeMeta? meta) {
        //Console.WriteLine("Deindexing node " + node.__Id + " meta: " + node.Meta);
        var shortId = _idByMeta[new(meta ?? IInnerNodeMeta.Empty, nodeTypeId)];
        _metaIdsByNodeId.Remove(nodeId, shortId);
        foreach (var kv in _cachedNodeIdsByCtx.AllNotThreadSafe()) {
            var ctx = kv.Key;
            foreach (var ids in kv.Value.All) {
                if (isMetaRelevantForContext(_metaById[shortId], ctx.TypeId, ctx.CxtKey, ids.CreatedWithNowUtc)) {
                    ids.Remove(nodeId);
                }
            }
        }
        if (_countByType.TryGetValue(nodeTypeId, out var count)) {
            if (count <= 1) _countByType.Remove(nodeTypeId);
            else _countByType[nodeTypeId] = count - 1;
            _revisionCount--;
        } else {
            throw new Exception("Internal error. Attempting to deindex node of type that has no count registered: " + nodeTypeId);
        }
    }

    public INodeDataExternal PickBestOuter(INodeDataInternal node, QueryContextKey ctxKey, DateTime nowUtc) {
        return TryPickBestOuter(node, ctxKey, nowUtc) ?? throw new InvalidOperationException("No suitable revision found for node " + node.__Id + " and context " + ctxKey);
    }
    /// <summary>
    /// The revision the context reads, or null when it reads none: a node holding only a draft, say, outside edit view.
    /// What a read by id answers as a node not found, where a query would not have listed it.
    /// </summary>
    public INodeDataExternal? TryPickBestOuter(INodeDataInternal node, QueryContextKey ctxKey, DateTime nowUtc) {

        // no access control, type, deteleted, as this is already taken care of in source of ids: nodesIds or relation
        // this is just picking the best revision for the context, which is based on culture and release/expire date

        if (node is INodeDataExternal nd) return nd; // no revisions, return as is

        if (node is not NodeDataRevisions ndr) throw new Exception("Internal error, expected NodeDataRevisions");

        // first, look for references to specified revisions: ( typically used for previewing unpublished content )
        if (ctxKey.SelectedRevisions != null) {
            foreach (var rev in ctxKey.SelectedRevisions) {
                if (rev.NodeId == node.Id) {
                    var r = ndr.Revisions.FirstOrDefault(r => r.RevisionKey == rev.RevisionId);
                    if (r != null) return r;
                }
            }
        }

        // then any published revision with matching culture
        foreach (var r in ndr.Revisions) {
            var meta = r.Meta ?? IInnerNodeMeta.Empty;
            if (
                r.RevisionType == RevisionType.Published
                && r.CultureId == ctxKey.CultureId
                && isReleased(nowUtc, r.Meta)
                ) {
                return r;
            }
        }

        // in edit view, the culture's own revision comes before any other culture's: one published but not yet
        // released, else a draft or one awaiting approval, else an archived one, else one in the bin
        if (ctxKey.IncludeUnpublished) {
            var own = ndr.Revisions
                .Where(r => r.CultureId == ctxKey.CultureId)
                .OrderBy(r => unpublishedRank(r.RevisionType))
                .ThenByDescending(r => r.ChangedUtc)
                .FirstOrDefault();
            if (own != null)
                return own;
        }

        // then any published revision with fallback culture
        if (ctxKey.IncludeCultureFallback) {
            var match = ndr.Revisions
                .Where(r => r.RevisionType == RevisionType.Published && isReleased(nowUtc, r.Meta))
                .OrderBy(r => _definition.GetCulturePriority(r.CultureId, r.CollectionId))
                .FirstOrDefault();
            if (match != null)
                return match;
        }

        if (!ctxKey.OnlyWithCulture) { // fallback to any published revision without culture ( invariant culture )
            var match = ndr.Revisions
                .FirstOrDefault(r =>
                    r.RevisionType == RevisionType.Published
                    && isReleased(nowUtc, r.Meta)
                    && r.CultureId == Guid.Empty
                );
            if (match != null)
                return match;
        }

        // in edit view a node with nothing in the culture is still the editor's to see: another culture's revision,
        // in fallback order, and of each culture's the one the culture's own reader would get first
        if (ctxKey.IncludeUnpublished) {
            var match = ndr.Revisions
                .Where(r => !ctxKey.OnlyWithCulture || r.CultureId != Guid.Empty)
                .OrderBy(r => _definition.GetCulturePriority(r.CultureId, r.CollectionId))
                .ThenBy(r => unpublishedRank(r.RevisionType))
                .ThenByDescending(r => r.ChangedUtc)
                .FirstOrDefault();
            if (match != null)
                return match;
        }

        return null;
    }
    static int unpublishedRank(RevisionType type) => type switch {
        RevisionType.Published or RevisionType.PublishOverride => 0,
        RevisionType.Preliminary or RevisionType.AwaitingPublicationApproval => 1,
        RevisionType.AwaitingArchiveApproval or RevisionType.AwaitingDeleteApproval => 2,
        RevisionType.Archived => 3,
        _ => 4, // binned
    };
    static int formatVersion = 1000;
    public void SaveState(IAppendStream stream) {
        stream.WriteInt(formatVersion);
        stream.WriteUInt(metaIdCounter);
        stream.WriteInt(_metaById.Count);
        foreach (var kv in _metaById) {
            stream.WriteUInt(kv.Key);
            stream.WriteByteArray(IInnerNodeMeta.ToBytes(kv.Value.Meta));
            stream.WriteGuid(kv.Value.TypeId);
        }
        _metaIdsByNodeId.WriteToStream(stream);
        stream.WriteInt(_countByType.Count);
        foreach (var kv in _countByType) {
            stream.WriteGuid(kv.Key);
            stream.WriteInt(kv.Value);
        }
    }
    public void ReadState(BufferReader stream) {
        var version = stream.ReadInt();
        if (version != formatVersion) throw new Exception("Incompatible format version for NodeIds store: " + version);
        metaIdCounter = stream.ReadUInt();
        var metaCount = stream.ReadInt();
        for (int i = 0; i < metaCount; i++) {
            var shortId = stream.ReadUInt();
            var metaBytes = stream.ReadByteArray();
            var meta = IInnerNodeMeta.FromBytes(metaBytes);
            if (meta == null) meta = IInnerNodeMeta.Empty;
            var typeId = stream.ReadGuid();
            var mt = new metaAndType(meta, typeId);
            _metaById[shortId] = mt;
            _idByMeta[mt] = shortId;
        }
        _metaIdsByNodeId.ReadFromStream(stream.AsReadStream());
        var countByTypeCount = stream.ReadInt();
        for (int i = 0; i < countByTypeCount; i++) {
            var typeId = stream.ReadGuid();
            var count = stream.ReadInt();
            _countByType[typeId] = count;
            _revisionCount += count;
        }
    }

}