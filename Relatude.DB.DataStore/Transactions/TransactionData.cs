﻿using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;

namespace Relatude.DB.Transactions;
public class TransactionData {
    public TransactionData() {
        Actions = [];
    }
    public TransactionData(List<ActionBase> actions) {
        Actions = actions;
    }
    public Action? InnerCallbackBeforeCommitting;
    public List<Guid>? LockExcemptions { get; set; }
    public List<ActionBase> Actions { get; set; }
    public long Timestamp { get; set; }

    public void Add(ActionBase action) => Actions.Add(action);

    //public void RemoveCulture(Guid nodeGuid, int lcid) => Add(NodeAction.RemoveCulture(nodeGuid, lcid));
    public void InsertNode(INodeData node) => Add(NodeAction.Insert(node));
    public void InsertIfNotExists(INodeData node) => Add(NodeAction.InsertIfNotExists(node));
    public void ForceUpdateNode(INodeData node) => Add(NodeAction.ForceUpdate(node));
    public void UpdateNode(INodeData node) => Add(NodeAction.Update(node));
    public void ForceUpsertNode(INodeData node) => Add(NodeAction.ForceUpsert(node));
    public void UpsertNode(INodeData node) => Add(NodeAction.Upsert(node));

    public void DeleteNodeOrFail(int nodeId) => Add(NodeAction.DeleteOrFail(nodeId));
    public void DeleteNodeOrFail(Guid nodeGuid) => Add(NodeAction.DeleteOrFail(nodeGuid));
    public void DeleteNode(int nodeId) => Add(NodeAction.Delete(nodeId));
    public void DeleteNode(Guid nodeGuid) => Add(NodeAction.Delete(nodeGuid));

    public void AddRelation(Guid relationId, int source, int target) => Add(new RelationAction(RelationOperation.Add, relationId) { Source = source, Target = target, ChangeUtc = DateTime.UtcNow });
    public void AddRelation(Guid relationId, int source, int target, DateTime dtUtc) => Add(new RelationAction(RelationOperation.Add, relationId) { Source = source, Target = target, ChangeUtc = dtUtc });
    public void AddRelation(Guid relationId, Guid source, Guid target) => Add(new RelationAction(RelationOperation.Add, relationId) { SourceGuid = source, TargetGuid = target, ChangeUtc = DateTime.UtcNow });
    public void AddRelation(Guid relationId, Guid source, Guid target, DateTime dtUtc, bool ensuring) => Add(new RelationAction(RelationOperation.Add, relationId) { SourceGuid = source, TargetGuid = target, ChangeUtc = dtUtc });

    public void UpdateProperty(Guid nodeId, Guid propetyId, object value) => Add(new NodePropertyAction(NodePropertyOperation.Update, null, [nodeId], null, propetyId, value));
    public void UpdateProperty(int nodeId, Guid propetyId, object value) => Add(new NodePropertyAction(NodePropertyOperation.Update, null, null, [nodeId], propetyId, value));
    public void UpdateProperty(Guid[] nodeIds, Guid propetyId, object value) => Add(new NodePropertyAction(NodePropertyOperation.Update, null, nodeIds, null, propetyId, value));
    public void UpdateProperty(int[] nodeIds, Guid propetyId, object value) => Add(new NodePropertyAction(NodePropertyOperation.Update, null, null, nodeIds, propetyId, value));

    public void UpdateProperties(Guid nodeId, Guid[] propetyIds, object[] values) => Add(new NodePropertyAction(NodePropertyOperation.Update, null, [nodeId], null, propetyIds, values));
    public void UpdateProperties(int nodeId, Guid[] propetyIds, object[] values) => Add(new NodePropertyAction(NodePropertyOperation.Update, null, null, [nodeId], propetyIds, values));
    public void UpdateProperties(int[] nodeIds, Guid[] propetyIds, object[] values) => Add(new NodePropertyAction(NodePropertyOperation.Update, null, null, nodeIds, propetyIds, values));
    public void UpdateProperties(Guid[] nodeIds, Guid[] propetyIds, object[] values) => Add(new NodePropertyAction(NodePropertyOperation.Update, null, nodeIds, null, propetyIds, values));

    public void UpdateIfDifferentProperty(Guid nodeId, Guid propetyId, object value) => Add(new NodePropertyAction(NodePropertyOperation.UpdateIfDifferent, null, [nodeId], null, propetyId, value));
    public void UpdateIfDifferentProperty(int nodeId, Guid propetyId, object value) => Add(new NodePropertyAction(NodePropertyOperation.UpdateIfDifferent, null, null, [nodeId], propetyId, value));
    public void UpdateIfDifferentProperty(Guid[] nodeIds, Guid propetyId, object value) => Add(new NodePropertyAction(NodePropertyOperation.UpdateIfDifferent, null, nodeIds, null, propetyId, value));
    public void UpdateIfDifferentProperty(int[] nodeIds, Guid propetyId, object value) => Add(new NodePropertyAction(NodePropertyOperation.UpdateIfDifferent, null, null, nodeIds, propetyId, value));
    public void ResetProperty(Guid nodeId, Guid propetyId) => Add(new NodePropertyAction(NodePropertyOperation.Reset, null, [nodeId], null, propetyId, null));
    public void ResetProperty(int nodeId, Guid propetyId) => Add(new NodePropertyAction(NodePropertyOperation.Reset, null, null, [nodeId], propetyId, null));
    public void ResetProperty(Guid[] nodeIds, Guid propetyId) => Add(new NodePropertyAction(NodePropertyOperation.Reset, null, nodeIds, null, propetyId, null));
    public void ResetProperty(int[] nodeIds, Guid propetyId) => Add(new NodePropertyAction(NodePropertyOperation.Reset, null, null, nodeIds, propetyId, null));
    public void AddToProperty(Guid nodeId, Guid propetyId, object value) => Add(new NodePropertyAction(NodePropertyOperation.Add, null, [nodeId], null, propetyId, value));
    public void AddToProperty(int nodeId, Guid propetyId, object value) => Add(new NodePropertyAction(NodePropertyOperation.Add, null, null, [nodeId], propetyId, value));
    public void AddToProperty(Guid[] nodeIds, Guid propetyId, object value) => Add(new NodePropertyAction(NodePropertyOperation.Add, null, nodeIds, null, propetyId, value));
    public void AddToProperty(int[] nodeIds, Guid propetyId, object value) => Add(new NodePropertyAction(NodePropertyOperation.Add, null, null, nodeIds, propetyId, value));
    public void MultiplyProperty(Guid nodeId, Guid propetyId, object value) => Add(new NodePropertyAction(NodePropertyOperation.Multiply, null, [nodeId], null, propetyId, value));
    public void MultiplyProperty(int nodeId, Guid propetyId, object value) => Add(new NodePropertyAction(NodePropertyOperation.Multiply, null, null, [nodeId], propetyId, value));
    public void MultiplyProperty(Guid[] nodeIds, Guid propetyId, object value) => Add(new NodePropertyAction(NodePropertyOperation.Multiply, null, nodeIds, null, propetyId, value));
    public void MultiplyProperty(int[] nodeIds, Guid propetyId, object value) => Add(new NodePropertyAction(NodePropertyOperation.Multiply, null, null, nodeIds, propetyId, value));

    public void ValidateProperty(Guid nodeId, Guid propetyId, ValueRequirement requirement, object value) => Add(new NodePropertyValidation(requirement, [nodeId], null, propetyId, value));
    public void ValidateProperty(int nodeId, Guid propetyId, ValueRequirement requirement, object value) => Add(new NodePropertyValidation(requirement, null, [nodeId], propetyId, value));
    public void ValidateProperty(Guid[] nodeIds, Guid propetyId, ValueRequirement requirement, object value) => Add(new NodePropertyValidation(requirement, nodeIds, null, propetyId, value));
    public void ValidateProperty(int[] nodeIds, Guid propetyId, ValueRequirement requirement, object value) => Add(new NodePropertyValidation(requirement, null, nodeIds, propetyId, value));

    public void SetRelation(Guid relationId, int source, int target) => Add(new RelationAction(RelationOperation.Set, relationId) { Source = source, Target = target, ChangeUtc = DateTime.UtcNow });
    public void SetRelation(Guid relationId, int source, int target, DateTime dtUtc) => Add(new RelationAction(RelationOperation.Set, relationId) { Source = source, Target = target, ChangeUtc = dtUtc });
    public void SetRelation(Guid relationId, Guid source, Guid target) => Add(new RelationAction(RelationOperation.Set, relationId) { SourceGuid = source, TargetGuid = target, ChangeUtc = DateTime.UtcNow });
    public void SetRelation(Guid relationId, Guid source, Guid target, DateTime dtUtc, bool ensuring) => Add(new RelationAction(RelationOperation.Set, relationId) { SourceGuid = source, TargetGuid = target, ChangeUtc = dtUtc });

    public void RemoveRelation(Guid relationId, int source, int target) {
        if (source == 0) throw new Exception("Source cannot be 0. "); // to prevent accidental deletion of all relations
        if (target == 0) throw new Exception("Target cannot be 0. "); // to prevent accidental deletion of all relations
        Add(new RelationAction(RelationOperation.Remove, relationId) { Source = source, Target = target });
    }
    public void RemoveRelation(Guid relationId, Guid source, Guid target) {
        if (source == Guid.Empty) throw new Exception("Source cannot be empty. "); // to prevent accidental deletion of all relations
        if (target == Guid.Empty) throw new Exception("Target cannot be empty. "); // to prevent accidental deletion of all relations
        Add(new RelationAction(RelationOperation.Remove, relationId) { SourceGuid = source, TargetGuid = target });
    }
    public void ClearRelationsWithTarget(Guid relationId, int target) {
        if (target == 0) throw new Exception("Target cannot be 0. "); // to prevent accidental deletion of all relations
        Add(new RelationAction(RelationOperation.Clear, relationId) { Target = target });
    }
    public void ClearRelationsWithSource(Guid relationId, int source) {
        if (source == 0) throw new Exception("Source cannot be 0. "); // to prevent accidental deletion of all relations
        Add(new RelationAction(RelationOperation.Clear, relationId) { Source = source });
    }
    public void ClearRelationsWithTarget(Guid relationId, Guid target) {
        if (target == Guid.Empty) throw new Exception("Target cannot be empty. "); // to prevent accidental deletion of all relations
        Add(new RelationAction(RelationOperation.Clear, relationId) { TargetGuid = target });
    }
    public void ClearRelationsWithSource(Guid relationId, Guid source) {
        if (source == Guid.Empty) throw new Exception("Source cannot be empty. "); // to prevent accidental deletion of all relations
        Add(new RelationAction(RelationOperation.Clear, relationId) { SourceGuid = source });
    }
    public void ClearRelationsWithAny(Guid relationId) => Add(new RelationAction(RelationOperation.Remove, relationId));

    public void ClearRelation(Guid relationId, int source, int target) => Add(new RelationAction(RelationOperation.Clear, relationId) { Source = source, Target = target });
    public void ClearRelation(Guid relationId, Guid source, Guid target) => Add(new RelationAction(RelationOperation.Clear, relationId) { SourceGuid = source, TargetGuid = target });

    public IEnumerable<INodeData> GetAllDeletedNodes() {
        var deletedNodes = new Dictionary<Guid, INodeData>();
        foreach (var a in Actions) {
            if (a is NodeAction na && na.Operation == NodeOperation.DeleteOrFail) {
                if (!deletedNodes.ContainsKey(na.Node.Id))
                    deletedNodes.Add(na.Node.Id, na.Node);
            }
        }
        return deletedNodes.Values;
    }
    public void AddCollection(StoreCollection collection) => Add(CollectionAction.Add(collection));
    public void UpdateCollection(StoreCollection collection) => Add(CollectionAction.Update(collection));
    public void RemoveCollection(Guid collectionId) => Add(CollectionAction.Remove(collectionId));

    public void ChangeType(Guid nodeId, Guid nodeTypeId) => Add(NodeAction.ChangeType(nodeId, nodeTypeId));
    public void ChangeType(int nodeId, Guid nodeTypeId) => Add(NodeAction.ChangeType(nodeId, nodeTypeId));

    public void ReIndex(Guid nodeId) => Add(NodeAction.ReIndex(nodeId));
    public void ReIndex(int nodeId) => Add(NodeAction.ReIndex(nodeId));

}
