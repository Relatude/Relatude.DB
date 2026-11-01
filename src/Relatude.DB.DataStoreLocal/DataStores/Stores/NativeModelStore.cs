using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.IO;
using Relatude.DB.Native;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace Relatude.DB.DataStores.Stores;

public class NativeModelStore(DataStoreLocal store) {
    Dictionary<int, NativeSystemUser> _users = [];
    Dictionary<int, NativeSystemUserGroup> _userGroups = [];
    Dictionary<int, NativeSystemCollection> _collections = [];
    Dictionary<int, NativeSystemCulture> _cultures = [];
    Dictionary<int[], Guid[]> _effectiveMembershipsCache = new(new IntArrayEqualityComparer());
    Dictionary<Guid, NativeNodeType> _nodeTypeCache = [];
    Dictionary<Guid, NativeRelationType> _relationTypeCache = [];
    NativeNodeType getNativeNodeType(Guid nodeTypeId) {
        if (_nodeTypeCache.TryGetValue(nodeTypeId, out var nativeType)) return nativeType;
        var inheritedTypes = store.Datamodel.NodeTypes[nodeTypeId].ThisAndAllInheritedTypes;
        if (inheritedTypes.ContainsKey(NodeConstants.BaseUserId)) nativeType = NativeNodeType.SystemUser;
        else if (inheritedTypes.ContainsKey(NodeConstants.BaseUserGroupId)) nativeType = NativeNodeType.SystemUserGroup;
        else if (inheritedTypes.ContainsKey(NodeConstants.BaseCollectionId)) nativeType = NativeNodeType.Collection;
        else if (inheritedTypes.ContainsKey(NodeConstants.BaseCultureId)) nativeType = NativeNodeType.SystemCulture;
        else nativeType = NativeNodeType.NotRelevant;
        _nodeTypeCache[nodeTypeId] = nativeType;
        return nativeType;
    }
    NativeRelationType getNativeRelationType(Guid relationTypeId) {
        if (_relationTypeCache.TryGetValue(relationTypeId, out var nativeType)) return nativeType;
        if (relationTypeId == NodeConstants.RelationUsersToGroups) nativeType = NativeRelationType.UsersToGroups;
        else if (relationTypeId == NodeConstants.RelationGroupsToGroups) nativeType = NativeRelationType.GroupsToGroups;
        else if (relationTypeId == NodeConstants.RelationCollectionsToCultures) nativeType = NativeRelationType.CollectionsToCultures;
        else nativeType = NativeRelationType.NotRelevant;
        _relationTypeCache[relationTypeId] = nativeType;
        return nativeType;
    }
    // The entry the last primitive action removed. An update reaches this store as the old node removed and the new
    // one added straight after, so what the relations gave the entry - a user's groups, a group's members, a
    // collection's cultures - is carried over to the new entry; the node gives the rest. A delete removes the node's
    // relations first, so what a deleted entry carries is empty, and any other action in between drops it.
    (NativeNodeType Type, int Id, object Entry)? _removed;
    // Under db write lock, so no locking needed for updates
    public void UpdateNodeActionIfRelevant(PrimitiveNodeAction action) {
        var removed = _removed;
        _removed = null;
        var nativeType = getNativeNodeType(action.Node.NodeType);
        if (nativeType == NativeNodeType.NotRelevant) return;
        var id = action.Node.__Id;
        switch (action.Operation) {
            case PrimitiveOperation.Add:
                var replaced = removed is { } r && r.Type == nativeType && r.Id == id ? r.Entry : null;
                switch (nativeType) {
                    case NativeNodeType.SystemUser: addUser(action.Node, replaced as NativeSystemUser); break;
                    case NativeNodeType.SystemUserGroup: addUserGroup(id, replaced as NativeSystemUserGroup); break;
                    case NativeNodeType.Collection: addCollection(id, replaced as NativeSystemCollection); break;
                    case NativeNodeType.SystemCulture: addCulture(action.Node, replaced as NativeSystemCulture); break;
                    default: throw new InvalidOperationException("Node is not of a native type.");
                }
                break;
            case PrimitiveOperation.Remove:
                object? entry = nativeType switch {
                    NativeNodeType.SystemUser => _users.GetValueOrDefault(id),
                    NativeNodeType.SystemUserGroup => _userGroups.GetValueOrDefault(id),
                    NativeNodeType.Collection => _collections.GetValueOrDefault(id),
                    NativeNodeType.SystemCulture => _cultures.GetValueOrDefault(id),
                    _ => throw new InvalidOperationException("Node is not of a native type."),
                };
                switch (nativeType) {
                    case NativeNodeType.SystemUser: deleteUser(id); break;
                    case NativeNodeType.SystemUserGroup: deleteUserGroup(id); break;
                    case NativeNodeType.Collection: deleteCollection(id); break;
                    case NativeNodeType.SystemCulture: deleteCulture(id); break;
                }
                if (entry != null) _removed = (nativeType, id, entry);
                break;
            default:
                break;
        }
    }
    public void UpdateRelationActionIfRelevant(PrimitiveRelationAction action) {
        _removed = null;
        var nativeType = getNativeRelationType(action.RelationId);
        if (nativeType == NativeRelationType.NotRelevant) return;
        switch (nativeType) {
            case NativeRelationType.UsersToGroups: {
                    if (action.Operation == PrimitiveOperation.Add) {
                        if (_users.TryGetValue(action.Source, out var user)) {
                            add(ref user.Memberships, action.Target);
                        }
                        if (_userGroups.TryGetValue(action.Target, out var group)) {
                            group.UserMembers.Add(action.Source);
                        }
                    } else if (action.Operation == PrimitiveOperation.Remove) {
                        if (_users.TryGetValue(action.Source, out var user)) {
                            remove(action.Target, ref user.Memberships);
                        }
                        if (_userGroups.TryGetValue(action.Target, out var group)) {
                            group.UserMembers.Remove(action.Source);
                        }
                    }
                    resetEffectiveMembershipsCache();
                }
                break;
            case NativeRelationType.GroupsToGroups: {
                    if (action.Operation == PrimitiveOperation.Add) {
                        if (_userGroups.TryGetValue(action.Source, out var sourceGroup)) {
                            add(ref sourceGroup.GroupMembers, action.Target);
                        }
                        if (_userGroups.TryGetValue(action.Target, out var targetGroup)) {
                            add(ref targetGroup.GroupMemberships, action.Source);
                        }
                    } else if (action.Operation == PrimitiveOperation.Remove) {
                        if (_userGroups.TryGetValue(action.Source, out var sourceGroup)) {
                            remove(action.Target, ref sourceGroup.GroupMembers);
                        }
                        if (_userGroups.TryGetValue(action.Target, out var targetGroup)) {
                            remove(action.Source, ref targetGroup.GroupMemberships);
                        }
                    }
                    resetEffectiveMembershipsCache();
                }
                break;
            case NativeRelationType.CollectionsToCultures: {
                    if (action.Operation == PrimitiveOperation.Add) {
                        if (_collections.TryGetValue(action.Source, out var collection)) {
                            add(ref collection.Cultures, action.Target);
                        }
                        if (_cultures.TryGetValue(action.Target, out var culture)) {
                            add(ref culture.Collections, action.Source);
                        }
                    } else if (action.Operation == PrimitiveOperation.Remove) {
                        if (_collections.TryGetValue(action.Source, out var collection)) {
                            remove(action.Target, ref collection.Cultures);
                        }
                        if (_cultures.TryGetValue(action.Target, out var culture)) {
                            remove(action.Source, ref culture.Collections);
                        }
                    }
                }
                break;
            default:
                throw new InvalidOperationException("Relation is not of a native type.");
        }
    }
    public void SaveState(IAppendStream stream) {
        stream.WriteVerifiedInt(_users.Count);
        foreach (var user in _users.Values) {
            stream.WriteInt(user.Id);
            stream.WriteInt((int)user.UserType); // from state file version 1003; before it a user read back from the file was anonymous
            stream.WriteIntArray(user.Memberships);
        }
        stream.WriteVerifiedInt(_userGroups.Count);
        foreach (var group in _userGroups.Values) {
            stream.WriteInt(group.Id);
            stream.WriteIntArray(group.UserMembers.ToArray());
            stream.WriteIntArray(group.GroupMembers);
            stream.WriteIntArray(group.GroupMemberships);
        }
        stream.WriteVerifiedInt(_collections.Count);
        foreach (var collection in _collections.Values) {
            stream.WriteInt(collection.Id);
            stream.WriteIntArray(collection.Cultures);
        }
        stream.WriteVerifiedInt(_cultures.Count);
        foreach (var culture in _cultures.Values) {
            stream.WriteInt(culture.Id);
            stream.WriteString(culture.CultureCode);
            stream.WriteIntArray(culture.Collections);
        }
    }
    public void ReadState(BufferReader stream) {
        _users.Clear();
        var noUsers = stream.ReadVerifiedInt();
        for (var i = 0; i < noUsers; i++) {
            var userId = stream.ReadInt();
            var userType = (SystemUserType)stream.ReadInt();
            var memberships = stream.ReadIntArray();
            var user = new NativeSystemUser {
                Id = userId,
                UserType = userType,
                Memberships = memberships,
            };
            _users.Add(user.Id, user);
        }
        _userGroups.Clear();
        var noGroups = stream.ReadVerifiedInt();
        for (var i = 0; i < noGroups; i++) {
            var groupId = stream.ReadInt();
            var userMembers = stream.ReadIntArray().ToHashSet();
            var groupMembers = stream.ReadIntArray();
            var groupMemberships = stream.ReadIntArray();
            var group = new NativeSystemUserGroup {
                Id = groupId,
                UserMembers = userMembers,
                GroupMembers = groupMembers,
                GroupMemberships = groupMemberships,
            };
            _userGroups.Add(group.Id, group);
        }
        _collections.Clear();
        var noCollections = stream.ReadVerifiedInt();
        for (var i = 0; i < noCollections; i++) {
            var collectionId = stream.ReadInt();
            var cultures = stream.ReadIntArray();
            var collection = new NativeSystemCollection {
                Id = collectionId,
                Cultures = cultures,
            };
            _collections.Add(collection.Id, collection);
        }
        _cultures.Clear();
        var noCultures = stream.ReadVerifiedInt();
        for (var i = 0; i < noCultures; i++) {
            var cultureId = stream.ReadInt();
            var cultureCode = stream.ReadString();
            var collections = stream.ReadIntArray();
            var culture = new NativeSystemCulture {
                Id = cultureId,
                // not in the state file: the id registry, read before, knows it
                Guid = store._guids.TryGetId(cultureId, out var guid) ? guid : Guid.Empty,
                CultureCode = cultureCode,
                Collections = collections,
            };
            _cultures.Add(culture.Id, culture);
        }
        refreshCultureOrder();
    }
    public void RegisterActionDuringStateLoad(PrimitiveActionBase action, bool throwOnErrors, Action<string, Exception> log) {
        try {
            if (action is PrimitiveNodeAction na) {
                UpdateNodeActionIfRelevant(na);
            } else if (action is PrimitiveRelationAction ra) {
                UpdateRelationActionIfRelevant(ra);
            }
        } catch (Exception err) {
            var msg = "Error during native model state load. " + err.Message;
            log(msg, err);
            if (throwOnErrors) throw new Exception(msg, err);
        }
    }

    internal bool TryGetCultureId(string cultureCode, out Guid cultureId) {
        // optimization possible...
        foreach (var culture in _cultures.Values) {
            if (string.Equals(culture.CultureCode, cultureCode, StringComparison.OrdinalIgnoreCase)) {
                cultureId = culture.Guid;
                return true;
            }
        }
        cultureId = Guid.Empty;
        return false;
    }
    internal bool TryGetCultureCode(Guid? cultureId, out string? cultureCode) {
        if (cultureId.HasValue) {
            foreach (var culture in _cultures.Values) {
                if (culture.Guid == cultureId.Value) {
                    cultureCode = culture.CultureCode;
                    return true;
                }
            }
        }
        cultureCode = null;
        return false;
    }


    public int CountUsers => _users.Count;
    public int CountUserGroups => _userGroups.Count;
    public int CountCollections => _collections.Count;
    public int CountCultures => _cultures.Count;
    public IEnumerable<NativeSystemCulture> Cultures => _cultures.Values;
    void deleteUser(int nodeId) {
        _users.Remove(nodeId);
    }
    // replaced: the entry of the node this one updates, whose groups it keeps (the node gives the user type)
    void addUser(INodeData node, NativeSystemUser? replaced) {
        Native.SystemUserType userType = Native.SystemUserType.Anonymous;
        if (node.TryGetValue(NodeConstants.NativeUserPropertyUserType, out var objUserType)) {
            // enum properties are stored as int, and a boxed int cannot be unboxed straight to the enum:
            userType = (Native.SystemUserType)(int)objUserType;
        }
        var user = new NativeSystemUser {
            Id = node.__Id,
            UserType = userType,
            Memberships = replaced?.Memberships ?? [],
        };
        _users.Add(user.Id, user);
    }
    // The user's groups and every group they are in through groups of groups, by guid in order. Each group is visited
    // once, so a cycle of groups (which the relation refuses, but a model from before that may hold) ends by itself,
    // and a hierarchy of any size is followed to its end; a group id the registry no longer knows is passed over.
    Guid[] calculateEffectiveMemberships(NativeSystemUser user) {
        var visited = new HashSet<int>();
        var effectiveMemberships = new HashSet<Guid>();
        var toProcess = new Queue<int>();
        foreach (var groupId in user.Memberships) {
            if (visited.Add(groupId)) toProcess.Enqueue(groupId);
        }
        while (toProcess.Count > 0) {
            var currentGroupId = toProcess.Dequeue();
            if (store._guids.TryGetId(currentGroupId, out Guid groupGuid)) effectiveMemberships.Add(groupGuid);
            if (_userGroups.TryGetValue(currentGroupId, out var group)) {
                foreach (var parentGroupId in group.GroupMemberships) {
                    if (visited.Add(parentGroupId)) toProcess.Enqueue(parentGroupId);
                }
            }
        }
        return [.. effectiveMemberships.Order()];
    }
    void resetEffectiveMembershipsCache() {
        // readers fill the cache under its lock, and some read without the store's
        lock (_effectiveMembershipsCache) _effectiveMembershipsCache.Clear();
    }
    public Guid[] GetEffectiveMembershipsOfUser(int userId) {
        lock (_effectiveMembershipsCache) {
            if (_users.TryGetValue(userId, out var user)) {
                if (!_effectiveMembershipsCache.TryGetValue(user.Memberships, out var effectiveMemberships)) {
                    effectiveMemberships = calculateEffectiveMemberships(user);
                    _effectiveMembershipsCache[user.Memberships] = effectiveMemberships;
                }
                return effectiveMemberships;
            }
        }
        return [];
    }
    void deleteUserGroup(int nodeId) {
        _userGroups.Remove(nodeId);
    }
    void addUserGroup(int nodeId, NativeSystemUserGroup? replaced) {
        var group = new NativeSystemUserGroup {
            Id = nodeId,
            UserMembers = replaced?.UserMembers ?? [],
            GroupMembers = replaced?.GroupMembers ?? [],
            GroupMemberships = replaced?.GroupMemberships ?? [],
        };
        _userGroups.Add(nodeId, group);
    }
    void deleteCollection(int nodeId) {
        _collections.Remove(nodeId);
    }
    void addCollection(int nodeId, NativeSystemCollection? replaced) {
        var collection = new NativeSystemCollection {
            Id = nodeId,
            Cultures = replaced?.Cultures ?? [],
        };
        _collections.Add(nodeId, collection);
    }
    void deleteCulture(int nodeId) {
        _cultures.Remove(nodeId);
        refreshCultureOrder();
    }
    void addCulture(INodeData node, NativeSystemCulture? replaced) {
        var culture = new NativeSystemCulture {
            Id = node.__Id,
            Guid = node.Id,
            CultureCode = node.TryGetValue(NodeConstants.NativeCulturePropertyCultureCode, out var objCultureCode) ? (string)objCultureCode : string.Empty,
            Collections = replaced?.Collections ?? [],
        };
        _cultures.Add(node.__Id, culture);
        refreshCultureOrder();
    }

    /// <summary>
    /// Where this store disagrees with the nodes and relations it is made from, everything here being derived from
    /// them: a user's type and groups, a group's members and the groups it is in, a collection's cultures, a culture's
    /// code and collections, each entry having its node and each node its entry. Empty when they agree. For tests and
    /// diagnostics: it reads every native node, so it is not for a request.
    /// </summary>
    internal List<string> FindInconsistencies() {
        var problems = new List<string>();
        var def = store._definition;
        int[] related(Guid relationId, int id, bool fromTargetToSource) =>
            def.Relations.TryGetValue(relationId, out var relation) ? [.. relation.GetRelated(id, fromTargetToSource).Enumerate().Order()] : [];
        static bool same(IEnumerable<int> held, int[] stored) => held.Order().SequenceEqual(stored);
        void entries<T>(Guid typeId, string kind, Dictionary<int, T> held, Action<int, T, INodeData> check) {
            var ids = def.GetAllIdsForTypeNoAccessControl(typeId, true).Enumerate().ToHashSet();
            foreach (var id in ids) {
                if (!held.TryGetValue(id, out var entry)) { problems.Add(kind + " " + id + " has no entry. "); continue; }
                if (!store._nodes.TryGet(id, out var node, out _)) { problems.Add(kind + " " + id + " cannot be read. "); continue; }
                check(id, entry, node);
            }
            foreach (var id in held.Keys) if (!ids.Contains(id)) problems.Add(kind + " " + id + " has an entry but no node. ");
        }
        entries(NodeConstants.BaseUserId, "User", _users, (id, user, node) => {
            var userType = node.TryGetValue(NodeConstants.NativeUserPropertyUserType, out var t) ? (SystemUserType)(int)t : SystemUserType.Anonymous;
            if (user.UserType != userType) problems.Add("User " + id + " is held as " + user.UserType + " but is " + userType + ". ");
            if (!same(user.Memberships, related(NodeConstants.RelationUsersToGroups, id, false))) problems.Add("User " + id + " is held in other groups than its relations say. ");
        });
        entries(NodeConstants.BaseUserGroupId, "Group", _userGroups, (id, group, node) => {
            if (!same(group.UserMembers, related(NodeConstants.RelationUsersToGroups, id, true))) problems.Add("Group " + id + " is held with other users than its relations say. ");
            if (!same(group.GroupMembers, related(NodeConstants.RelationGroupsToGroups, id, false))) problems.Add("Group " + id + " is held with other member groups than its relations say. ");
            if (!same(group.GroupMemberships, related(NodeConstants.RelationGroupsToGroups, id, true))) problems.Add("Group " + id + " is held in other groups than its relations say. ");
        });
        entries(NodeConstants.BaseCollectionId, "Collection", _collections, (id, collection, node) => {
            if (!same(collection.Cultures, related(NodeConstants.RelationCollectionsToCultures, id, false))) problems.Add("Collection " + id + " is held with other cultures than its relations say. ");
        });
        entries(NodeConstants.BaseCultureId, "Culture", _cultures, (id, culture, node) => {
            var code = node.TryGetValue(NodeConstants.NativeCulturePropertyCultureCode, out var c) ? (string)c : string.Empty;
            if (culture.CultureCode != code) problems.Add("Culture " + id + " is held as " + culture.CultureCode + " but is " + code + ". ");
            if (culture.Guid != node.Id) problems.Add("Culture " + id + " is held with another guid. ");
            if (!same(culture.Collections, related(NodeConstants.RelationCollectionsToCultures, id, true))) problems.Add("Culture " + id + " is held in other collections than its relations say. ");
        });
        return problems;
    }

    // culture id -> its place when a reader falls back to another culture. Read without the store lock (a node is
    // converted for a reader after the lock is let go), so it is replaced whole as cultures come and go.
    volatile Dictionary<Guid, int> _cultureOrder = [];
    void refreshCultureOrder() {
        var order = new Dictionary<Guid, int>();
        var place = 1;
        foreach (var c in _cultures.Values.OrderBy(c => c.Id)) order[c.Guid] = place++;
        _cultureOrder = order;
        CultureVersion++;
    }
    /// <summary>Moves on whenever a culture is made or removed, so a transaction can tell it changed the cultures.</summary>
    internal int CultureVersion { get; private set; }
    /// <summary>The cultures' ids in the order they were made in, the order a reader falls back in.</summary>
    internal Guid[] CultureIdsInOrder => [.. _cultures.Values.OrderBy(c => c.Id).Select(c => c.Guid)];
    internal bool IsCulture(Guid cultureId) => _cultureOrder.ContainsKey(cultureId);
    internal bool IsCultureNode(Guid nodeTypeId) => getNativeNodeType(nodeTypeId) == NativeNodeType.SystemCulture;
    /// <summary>
    /// Where a culture comes when a reader falls back to another one: the neutral version first, then the cultures
    /// in the order they were made in, and last one that is gone.
    /// </summary>
    internal int GetCulturePriority(Guid cultureId) =>
        cultureId == Guid.Empty ? 0 : _cultureOrder.TryGetValue(cultureId, out var place) ? place : int.MaxValue;

    static void remove(int value, ref int[] array) {
        for (int i = 0; i < array.Length; i++) {
            if (array[i] == value) {
                var newArray = new int[array.Length - 1];
                if (i > 0) Array.Copy(array, 0, newArray, 0, i);
                if (i < array.Length - 1) Array.Copy(array, i + 1, newArray, i, array.Length - i - 1);
                array = newArray;
                break;
            }
        }
    }
    static void add(ref int[] array, int value) {
        var newArray = new int[array.Length + 1];
        Array.Copy(array, newArray, array.Length);
        newArray[array.Length] = value;
        array = newArray;
    }

    public QueryContextKey GetQueryContextKey(QueryContext ctx, out DateTime now) {
        now = ctx.NowUtc ?? DateTime.UtcNow;
        Guid cultureId;
        if (ctx.CultureId.HasValue && ctx.CultureId != Guid.Empty) {
            if (ctx.CultureCode != null) throw new InvalidOperationException("Both CultureId and CultureCode cannot be set at the same time.");
            var culture = _cultures.Values.FirstOrDefault(c => c.Guid == ctx.CultureId);
            if (culture == null) throw new InvalidOperationException($"Culture with id '{ctx.CultureId}' does not exist.");
            cultureId = culture.Guid;
        } else if (!string.IsNullOrWhiteSpace(ctx.CultureCode)) {
            // culture codes are compared as .NET compares culture names, and as TryGetCultureId does
            var culture = _cultures.Values.FirstOrDefault(c => string.Equals(c.CultureCode, ctx.CultureCode, StringComparison.OrdinalIgnoreCase));
            if (culture == null) throw new InvalidOperationException($"Culture with code '{ctx.CultureCode}' does not exist.");
            cultureId = culture.Guid;
        } else {
            cultureId = Guid.Empty;
        }
        Guid[]? collectionIds;
        if (ctx.CollectionIds is not null) {
            List<Guid> collectionIdsList = [];
            foreach (var colId in ctx.CollectionIds) {
                collectionIdsList.Add(colId);
            }
            collectionIds = collectionIdsList.Order().ToArray();
        } else {
            collectionIds = null;
        }
        Guid[]? membershipIds;
        var userType = Native.SystemUserType.Anonymous;
        if (ctx.UserId == Guid.Empty) {
            membershipIds = null;
        } else if (store._guids.TryGetId(ctx.UserId, out var userId) && _users.TryGetValue(userId, out var user)) {
            membershipIds = GetEffectiveMembershipsOfUser(userId);
            userType = user.UserType;
        } else if (ctx.UserId == NodeConstants.MasterAdminUserId) {
            userType = SystemUserType.Admin;
            membershipIds = null;
        } else {
            throw new InvalidOperationException($"User with id '{ctx.UserId}' does not exist.");
        }
        return new QueryContextKey(
            cultureId: cultureId,
            collectionIds: collectionIds,
            membershipIds: membershipIds,
            includeDeleted: ctx.IncludeDeleted,
            includeCultureFallback: ctx.IncludeCultureFallback,
            onlyWithCulture: ctx.OnlyWithCulture,
            includeUnpublished: ctx.IncludeUnpublished,
            editView: ctx.EditView,
            includeHidden: ctx.IncludeHidden,
            excludeDescendants: ctx.ExcludeDescendants,
            userType: userType,
            selectedRevisions: ctx.SelectedRevisions
        );
    }

}
public sealed class IntArrayEqualityComparer : IEqualityComparer<int[]> {
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(int[]? x, int[]? y) {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;
        if (x.Length != y.Length) return false;
        return x.AsSpan().SequenceEqual(y);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetHashCode(int[]? obj) {
        if (obj is null) return 0;
        var hc = new HashCode();
        ReadOnlySpan<int> span = obj;
        hc.AddBytes(MemoryMarshal.AsBytes(span));
        return hc.ToHashCode();
    }
}

