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
    void syncNativeInfo(HashSet<int> nodeIds) {
        //_nodes.Contains(nodeIds);
    }
    void syncNativeSystemUser(int nodeId) { }
    void syncNativeCollection(int nodeId) { }
    void syncNativeSystemUserGroup(int nodeId) { }
    void syncNativeSystemCulture(int nodeId) { }
}


