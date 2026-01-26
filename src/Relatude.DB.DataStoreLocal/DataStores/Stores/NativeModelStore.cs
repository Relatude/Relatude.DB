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
    Dictionary<int[], int[]> _effectiveMembershipsCache = new(new IntArrayEqualityComparer());
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
    // Under db write lock, so no locking needed for updates
    public void UpdateNodeActionIfRelevant(PrimitiveNodeAction action) {
        var nativeType = getNativeNodeType(action.Node.NodeType);
        if (nativeType == NativeNodeType.NotRelevant) return;
        switch (action.Operation) {
            case PrimitiveOperation.Add:
                switch (nativeType) {
                    case NativeNodeType.SystemUser: addUser(action.Node); break;
                    case NativeNodeType.SystemUserGroup: addUserGroup(action.Node.__Id); break;
                    case NativeNodeType.Collection: addCollection(action.Node.__Id); break;
                    case NativeNodeType.SystemCulture: addCulture(action.Node); break;
                    default: throw new InvalidOperationException("Node is not of a native type.");
                }
                break;
            case PrimitiveOperation.Remove:
                switch (nativeType) {
                    case NativeNodeType.SystemUser: deleteUser(action.Node.__Id); break;
                    case NativeNodeType.SystemUserGroup: deleteUserGroup(action.Node.__Id); break;
                    case NativeNodeType.Collection: deleteCollection(action.Node.__Id); break;
                    case NativeNodeType.SystemCulture: deleteCulture(action.Node.__Id); break;
                    default: throw new InvalidOperationException("Node is not of a native type.");
                }
                break;
            default:
                break;
        }
    }
    public void UpdateRelationActionIfRelevant(PrimitiveRelationAction action) {
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
            stream.WriteIntArray(culture.Collections);
        }
    }
    public void ReadState(IReadStream stream) {
        _users.Clear();
        var noUsers = stream.ReadVerifiedInt();
        for (var i = 0; i < noUsers; i++) {
            var userId = stream.ReadInt();
            var memberships = stream.ReadIntArray();
            var user = new NativeSystemUser {
                Id = userId,
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
            var collections = stream.ReadIntArray();
            var culture = new NativeSystemCulture {
                Id = cultureId,
                Collections = collections,
            };
            _cultures.Add(culture.Id, culture);
        }
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
    public int CountUsers => _users.Count;
    public int CountUserGroups => _userGroups.Count;
    public int CountCollections => _collections.Count;
    public int CountCultures => _cultures.Count;
    void deleteUser(int nodeId) {
        _users.Remove(nodeId);
    }
    public void addUser(INodeData node) {
        var user = new NativeSystemUser {
            Id = node.__Id,
            UserType = node.GetValue(NodeConstants.NativeUserPropertyUserType, SystemUserType.Anonymous),
        };
        _users.Add(user.Id, user);
    }
    int[] calculateEffectiveMemberships(NativeSystemUser user) {
        var iterations = 0;
        var effectiveMemberships = new HashSet<int>(user.Memberships);
        var toProcess = new Queue<int>(user.Memberships);
        while (toProcess.Count > 0) {
            var currentGroupId = toProcess.Dequeue();
            if (++iterations > 1000) { // simple measure to ensure we don't get stuck in cycles:
                try {
                    throw new InvalidOperationException();
                } catch (Exception ex) {
                    store.LogError("Cyclic user group memberships detected. ", ex);
                }
                return [.. effectiveMemberships];
            }
            if (_userGroups.TryGetValue(currentGroupId, out var group)) {
                foreach (var parentGroupId in group.GroupMemberships) {
                    if (effectiveMemberships.Add(parentGroupId)) {
                        toProcess.Enqueue(parentGroupId);
                    }
                }
            }
        }
        return [.. effectiveMemberships.Order()];
    }
    void resetEffectiveMembershipsCache() {
        _effectiveMembershipsCache.Clear();
    }
    public int[] GetEffectiveMembershipsOfUser(int userId) {
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
    void addUserGroup(int nodeId) {
        var group = new NativeSystemUserGroup {
            Id = nodeId,
        };
        _userGroups.Add(nodeId, group);
    }
    void deleteCollection(int nodeId) {
        _collections.Remove(nodeId);
    }
    void addCollection(int nodeId) {
        var collection = new NativeSystemCollection {
            Id = nodeId,
        };
        _collections.Add(nodeId, collection);
    }
    void deleteCulture(int nodeId) {
        _cultures.Remove(nodeId);
    }
    void addCulture(INodeData node) {
        var culture = new NativeSystemCulture {
            Id = node.__Id,
        };
        _cultures.Add(node.__Id, culture);
    }

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

    public QueryContextKey GetQueryContextKey(QueryContext ctx) {
        throw new NotImplementedException();
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

