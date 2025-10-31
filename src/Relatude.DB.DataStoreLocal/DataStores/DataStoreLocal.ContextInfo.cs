using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Transactions;

namespace Relatude.DB.DataStores;
public sealed partial class DataStoreLocal : IDataStore {
    Dictionary<Guid, bool> _cacheOfNativeNodeTypes = [];
    bool isActionOnNodeTypeRelevantToNativeContextModel(Guid nodeTypeId) {
        if (_cacheOfNativeNodeTypes.TryGetValue(nodeTypeId, out var isRelevant)) return isRelevant;
        isRelevant = _isActionOnNodeTypeRelevantToNativeContextModel(nodeTypeId);
        _cacheOfNativeNodeTypes[nodeTypeId] = isRelevant;
        return isRelevant;
    }
    bool _isActionOnNodeTypeRelevantToNativeContextModel(Guid nodeTypeId) {
        var inheritedTypes = Datamodel.NodeTypes[nodeTypeId].ThisAndAllInheritedTypes;
        if (inheritedTypes.ContainsKey(NodeConstants.BaseCollectionId)) return true;
        if (inheritedTypes.ContainsKey(NodeConstants.BaseUserGroupId)) return true;
        if (inheritedTypes.ContainsKey(NodeConstants.BaseUserId)) return true;
        if (inheritedTypes.ContainsKey(NodeConstants.BaseCultureId)) return true;
        return false;
    }
    Dictionary<Guid, bool> _cacheOfNativeRelations = [];
    bool isActionOnRelationRelevantToNativeContextModel(Guid relationId) {
        if (_cacheOfNativeRelations.TryGetValue(relationId, out var isRelevant)) return isRelevant;
        isRelevant = _isActionOnRelationRelevantToNativeContextModel(relationId);
        _cacheOfNativeRelations[relationId] = isRelevant;
        return isRelevant;
    }
    bool _isActionOnRelationRelevantToNativeContextModel(Guid relationId) {
        if (relationId == NodeConstants.RelationCollectionsToCultures) return true;
        if (relationId == NodeConstants.RelationGroupsToGroups) return true;
        if (relationId == NodeConstants.RelationUsersToGroups) return true;
        return false;
    }
    enum NativeType {
        SystemUser,
        SystemUserGroup,
        SystemCulture,
        Collection
    }
    void syncNativeInfo(IEnumerable<int> nodeIds) {
        foreach (var nodeId in nodeIds) {
            bool exists = _nodes.TryGet(nodeId, out var node, out _);
            NativeType nativeType;
            if (exists) {
                var typeId = node!.NodeType;
                var types = Datamodel.NodeTypes[typeId].ThisAndAllInheritedTypes;
                if (types.ContainsKey(NodeConstants.BaseUserId)) nativeType = NativeType.SystemUser;
                else if (types.ContainsKey(NodeConstants.BaseUserGroupId)) nativeType = NativeType.SystemUserGroup;
                else if (types.ContainsKey(NodeConstants.BaseCultureId)) nativeType = NativeType.SystemCulture;
                else if (types.ContainsKey(NodeConstants.BaseCollectionId)) nativeType = NativeType.Collection;
                else throw new InvalidOperationException("Node is not of a native type.");
            } else {
                if (_nativeModelStore._userGroups.ContainsKey(nodeId)) nativeType = NativeType.SystemUserGroup;
                else if (_nativeModelStore._users.ContainsKey(nodeId)) nativeType = NativeType.SystemUser;
                else if (_nativeModelStore._cultures.ContainsKey(nodeId)) nativeType = NativeType.SystemCulture;
                else if (_nativeModelStore._collections.ContainsKey(nodeId)) nativeType = NativeType.Collection;
                else throw new InvalidOperationException("Node is not of a native type.");
            }
            var deleted = !exists;
            switch (nativeType) {
                case NativeType.SystemUser: syncNativeSystemUser(nodeId, deleted); break;
                case NativeType.SystemUserGroup: syncNativeSystemUserGroup(nodeId, deleted); break;
                case NativeType.SystemCulture: syncNativeSystemCulture(nodeId, deleted); break;
                case NativeType.Collection: syncNativeCollection(nodeId, deleted); break;
            }
        }
    }
    void syncNativeSystemUser(int nodeId, bool deleted) {
        if( deleted) {
            if (_nativeModelStore._users.TryGetValue(nodeId, out var existingUser)) {
                _nativeModelStore._users.Remove(nodeId);
            }
            return;
        }

    }
    void syncNativeCollection(int nodeId, bool deleted) {

    }
    void syncNativeSystemUserGroup(int nodeId, bool deleted) {

    }
    void syncNativeSystemCulture(int nodeId, bool deleted) {

    }
}


