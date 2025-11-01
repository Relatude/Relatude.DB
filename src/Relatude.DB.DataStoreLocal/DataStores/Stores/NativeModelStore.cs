using Relatude.DB.Datamodels;
using Relatude.DB.Native;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace Relatude.DB.DataStores.Stores;
public class NativeModelStore(DataStoreLocal store) {
    Dictionary<int, NativeSystemUser> _users = [];
    Dictionary<int, NativeSystemUserGroup> _userGroups = [];
    Dictionary<int, NativeSystemCollection> _collections = [];
    Dictionary<int, NativeSystemCulture> _cultures = [];
    Dictionary<int[], int[]> _effectiveMemberships = new (new IntArrayEqualityComparer());
    public NativeType GetNativeType(int nodeId) {
        if (_users.ContainsKey(nodeId)) return NativeType.SystemUser;
        if (_userGroups.ContainsKey(nodeId)) return NativeType.SystemUserGroup;
        if (_cultures.ContainsKey(nodeId)) return NativeType.SystemCulture;
        if (_collections.ContainsKey(nodeId)) return NativeType.Collection;
        throw new InvalidOperationException("Node is not of a native type.");
    }
    public void DeleteUser(int nodeId) {
        foreach (var group in _userGroups.Values) {
            group.UserMembers.Remove(nodeId);
        }
        _users.Remove(nodeId);
    }
    public void AddUser(INodeData node) {
        var userType = node.GetValue(NodeConstants.NativeUserPropertyUserType, () => SystemUserType.Anonymous);
        var membershipRelation = store._definition.Relations[NodeConstants.RelationUsersToGroups];
        var memberships = membershipRelation.GetRelated(node.__Id, false).ToArray();
        var user = new NativeSystemUser {
            Id = node.__Id,
            UserType = userType,
            Memberships = memberships,
        };
        foreach (var id in memberships) {
            if (_userGroups.TryGetValue(id, out var group)) {
                group.UserMembers.Add(user.Id);
            }
        }
        user.EffectiveMemberships = calculateEffectiveMemberships(user);
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
        return [.. effectiveMemberships];
    }
    public int[] GetEffectiveMembershipsOfUser(int userId) {
        lock(_effectiveMemberships) {
            if (_users.TryGetValue(userId, out var user)) {
                if (!_effectiveMemberships.TryGetValue(user.Memberships, out var effectiveMemberships)) {
                    effectiveMemberships = calculateEffectiveMemberships(user);
                    _effectiveMemberships[user.Memberships] = effectiveMemberships;
                }
                return effectiveMemberships;
            }
        }
        return [];
    }
    public void DeleteUserGroup(int nodeId) {
        foreach (var user in _users.Values) {
            remove(nodeId, ref user.Memberships);
        }
        foreach (var group in _userGroups.Values) {
            remove(nodeId, ref group.GroupMembers);
            remove(nodeId, ref group.GroupMemberships);
        }
        _userGroups.Remove(nodeId);
    }
    public void AddUserGroup(int nodeId) {
        var membershipRelationUsersToGroups = store._definition.Relations[NodeConstants.RelationUsersToGroups];
        var membershipRelationGroupsToGroups = store._definition.Relations[NodeConstants.RelationGroupsToGroups];
        var userMembers = membershipRelationUsersToGroups.GetRelated(nodeId, true).ToArray();
        var groupMembers = membershipRelationGroupsToGroups.GetRelated(nodeId, true).ToArray();
        var groupMemberships = membershipRelationGroupsToGroups.GetRelated(nodeId, false).ToArray();
        var group = new NativeSystemUserGroup {
            Id = nodeId,
            UserMembers = userMembers.ToHashSet(),
            GroupMembers = groupMembers,
            GroupMemberships = groupMemberships,
        };
        _userGroups.Add(nodeId, group);
    }
    public void DeleteCollection(int nodeId) {
        foreach (var culture in _cultures.Values) {
            remove(nodeId, ref culture.Collections);
        }
        _collections.Remove(nodeId);
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
    internal void AddCollection(int nodeId) {

    }
    internal void DeleteCulture(int nodeId) {
        throw new NotImplementedException();
    }
    internal void AddCulture(int nodeId) {
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

