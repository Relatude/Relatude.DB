using Relatude.DB.Datamodels;
using Relatude.DB.Tasks;
using Relatude.DB.Transactions;
namespace Relatude.DB.DataStores.Transactions;

internal class ActionConverter {
    ResultingOperation? _lastResultingOperation = null; // only possible as there is only one thread per transaction
    public PrimitiveActionBase[] Convert(DataStoreLocal db, ActionBase complexAction, bool transformValues, List<KeyValuePair<TaskData, string?>> newTasks, QueryContext ctx, out ResultingOperation resultingOperation) {
        _lastResultingOperation = ResultingOperation.None; // default
        var src = complexAction switch {
            RelationAction relationAction => toPrimitiveActions(db, relationAction, newTasks),
            NodeAction nodeAction => toPrimitiveActions(db, nodeAction, transformValues, newTasks),
            NodePropertyAction nodePropertyAction => toPrimitiveActions(db, nodePropertyAction, transformValues, newTasks, ctx),
            NodePropertyValidation nodePropertyValidation => toPrimitiveActions(db, nodePropertyValidation, newTasks),
            NodeRevisionAction nodeRevisionAction => toPrimitiveActions(db, nodeRevisionAction, transformValues, newTasks),
            _ => throw new NotSupportedException(),
        };
        resultingOperation = _lastResultingOperation == null ? ResultingOperation.None : _lastResultingOperation.Value;
        try {
            return src.ToArray(); // force conversion of action first, then return the array
        } catch (Exception err) {
            // converting an action should never change data, so a failed should not cause any integrity loss:
            throw new ExceptionWithoutIntegrityLoss("Failed to " + complexAction.ToString(db.Datamodel) + err.Message, err);
        }
    }
    void ensureIdAndGuid(DataStoreLocal db, INodeData node) {
        if (node.Id == Guid.Empty) {
            if (node.__Id == 0) { // both emtpy
                throw new Exception("Missing ID given. ");
            } else { // uid is set, so look up or create guid
                node.Id = db._guids.GetGuid(node.__Id);
            }
        } else {
            if (node.__Id == 0) {
                node.__Id = db._guids.GetId(node.Id);
            } else {
                db._guids.ValidateExistence(node.__Id, node.Id);
            }
        }
    }
    void ensureIdsAndCreateIdIfMissing(DataStoreLocal db, INodeDataInner node) {
        if (node.Id == Guid.Empty) {
            if (node.__Id == 0) { // both emtpy, so create new for both
                node.Id = Guid.NewGuid();
                node.__Id = db._guids.GetIdOrCreate(node.Id);
            } else { // uid is set, so look up or create guid
                node.Id = db._guids.GetGuidOrCreate(node.__Id);
            }
        } else { // guid is set
            if (node.__Id == 0) { // get or create uid
                node.__Id = db._guids.GetIdOrCreate(node.Id);
            } else { // both set, validate and chech that both id are either existing as a pair or both are available
                db._guids.ValidateCombinationAndRegisterIfNew(node.__Id, node.Id);
            }
        }
    }
    int getIdAndVerifyIdAndGuidIfGuidIsGiven(DataStoreLocal db, NodeAction nodeAction) {
        var id = nodeAction.Node.__Id;
        var guid = nodeAction.Node.Id;
        if (guid == Guid.Empty && id == 0) throw new Exception("Both ID and GUID are empty, cannot perform action. ");
        if (id == 0) {
            id = db._guids.GetId(guid);
        } else if (guid != Guid.Empty) {
            db._guids.ValidateExistence(id, guid);
        }
        return id;
    }
    bool nodeExists(DataStoreLocal db, INodeData node) {
        int id = 0;
        if (node.Id != Guid.Empty) db._guids.TryGetId(node.Id, out id);
        else if (node.__Id != 0) id = node.__Id;
        if (id == 0) return false;
        return db._nodes.Contains(id);
    }
    bool tryDetermineRevisionId(INodeData newNode, INodeData oldNode, Datamodel definition, out Guid revisionId) {
        // first look for revision id in Node;
        // first from revisionId property
        // then by using the QueryContextLanguage.
        // This requires there is a revision for the culture and that there only is one?
        throw new NotImplementedException("Node revisions are not yet implemented in DataStoreLocal. ");
    }
    IEnumerable<PrimitiveActionBase> toPrimitiveActions(DataStoreLocal db, NodeAction nodeAction, bool transformValues, List<KeyValuePair<TaskData, string?>> newTasks, Guid[]? doNotRegenTheseProps = null) {
        switch (nodeAction.Operation) {
            case NodeOperation.InsertOrFail: {
                    if (nodeAction.Node is not INodeDataInner node) throw new Exception("NodeAction with operation InsertOrFail requires node to be of type INodeDataInner. ");
                    ensureIdsAndCreateIdIfMissing(db, node);
                    if (node.CreatedUtc == DateTime.MinValue) node.CreatedUtc = DateTime.UtcNow;
                    Utils.ForceTypeValidateValuesAndCopyMissing(db._definition, node, null, transformValues);
                    Utils.EnsureOrQueueIndex(db, node, doNotRegenTheseProps, newTasks);
                    if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.CreateNode;
                    yield return new PrimitiveNodeAction(PrimitiveOperation.Add, node); // will eventually throw if node with id already exists
                }
                break;
            case NodeOperation.InsertIfNotExists: {
                    if (nodeExists(db, nodeAction.Node)) yield break;
                    nodeAction.Operation = NodeOperation.InsertOrFail;
                    if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.CreateNode;
                    foreach (var a in toPrimitiveActions(db, nodeAction, transformValues, newTasks)) yield return a;
                }
                break;
            case NodeOperation.DeleteOrFail: { // delete if exists, otherwise throw exception
                    var id = getIdAndVerifyIdAndGuidIfGuidIsGiven(db, nodeAction);
                    if (!db._nodes.TryGet(id, out var oldNode, out _)) {
                        throw new Exception("Node with id " + nodeAction.Node.Id + " does not exist, cannot delete. Use DeleteIfExists to ignore missing nodes. ");
                    } else {
                        // adding transactions to first remove any relation related to node,
                        // NB: important to not use "yield return" here as the values of the relation will change during the loop
                        if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.DeleteNode;
                        List<PrimitiveRelationAction> _relRemoveOperations = [];
                        foreach (var r in db._definition.Relations.Values) {
                            foreach (var target in r.GetRelated(id, false).Enumerate()) {
                                _relRemoveOperations.Add(new(PrimitiveOperation.Remove, r.Id, id, target, DateTime.UtcNow));
                            }
                            if (!r.IsSymmetric) { // if symmetric, we have already removed all relations
                                foreach (var source in r.GetRelated(id, true).Enumerate()) {
                                    _relRemoveOperations.Add(new(PrimitiveOperation.Remove, r.Id, source, id, DateTime.UtcNow));
                                }
                            }
                        }
                        foreach (var r in _relRemoveOperations) yield return r;
                        yield return new PrimitiveNodeAction(PrimitiveOperation.Remove, oldNode);
                    }
                }
                break;
            case NodeOperation.DeleteIfExists: { // delete if exists, otherwise do nothing
                    if (!nodeExists(db, nodeAction.Node)) yield break;
                    if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.DeleteNode;
                    nodeAction.Operation = NodeOperation.DeleteOrFail;
                    foreach (var a in toPrimitiveActions(db, nodeAction, transformValues, newTasks)) yield return a;
                }
                break;
            case NodeOperation.UpdateIfExists:
            case NodeOperation.UpdateOrFail:
            case NodeOperation.ForceUpdate: {
                    var node = nodeAction.Node;
                    ensureIdAndGuid(db, node);
                    if (!db._nodes.TryGet(node.__Id, out var oldNode, out _)) {// is new
                        if (nodeAction.Operation != NodeOperation.UpdateIfExists) {
                            throw new Exception("Node with id " + node.Id + " does not exist, cannot update. Use InsertIfNotExists or Upsert instead. ");
                        } else {
                            // ignore if node does not exist
                        }
                    } else {
                        var performUpdate = nodeAction.Operation switch {
                            NodeOperation.UpdateIfExists 
                            or NodeOperation.UpdateOrFail => Utils.AreDifferentIgnoringGeneratedProps(node, oldNode, db._definition),
                            NodeOperation.ForceUpdate => true,
                            _ => throw new NotImplementedException(),
                        };
                        if (performUpdate) {
                            if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.UpdateNode;
                            yield return new PrimitiveNodeAction(PrimitiveOperation.Remove, oldNode);
                            if (oldNode is NodeDataRevisions revsNode) { // revison handling is relevant
                                var canRevisionIdBeDetermined = tryDetermineRevisionId(node, oldNode, db.Datamodel, out var revisionId);
                                if (!canRevisionIdBeDetermined) throw new Exception("Cannot determine revision to update. ");
                                var oldRevNode = revsNode.Revisions.FirstOrDefault(r => r.RevisionId == revisionId);
                                if (oldRevNode == null) throw new Exception("Revision with id " + revisionId + " does not exist, cannot update. ");
                                Utils.ForceTypeValidateValuesAndCopyMissing(db._definition, node, oldRevNode, transformValues);
                                var typeDef = db.Datamodel.NodeTypes[node.NodeType];
                                var newNode = Utils.CopyAndUpdateRevision(revsNode, node, revisionId, oldRevNode.RevisionType, typeDef);
                                if (newNode.CreatedUtc == DateTime.MinValue) newNode.CreatedUtc = oldNode.CreatedUtc;
                                yield return new PrimitiveNodeAction(PrimitiveOperation.Add, newNode);
                            } else {
                                if (node is not INodeDataInner nodeInner) throw new Exception("NodeAction with operation ForceUpdate requires node to be of type INodeDataInner. ");
                                if (node.CreatedUtc == DateTime.MinValue) node.CreatedUtc = oldNode.CreatedUtc;
                                Utils.ForceTypeValidateValuesAndCopyMissing(db._definition, node, oldNode, transformValues);
                                Utils.EnsureOrQueueIndex(db, node, doNotRegenTheseProps, newTasks);
                                yield return new PrimitiveNodeAction(PrimitiveOperation.Add, nodeInner);
                            }
                        }
                    }
                }
                break;
            case NodeOperation.Upsert: {
                    if (nodeAction.Node is not INodeDataInner node) throw new Exception("NodeAction with operation InsertOrFail requires node to be of type INodeDataInner. ");
                    if (!db._nodes.TryGet(node.__Id, out var oldNode, out _)) { // is new
                        if (node.CreatedUtc == DateTime.MinValue) node.CreatedUtc = DateTime.UtcNow;
                        Utils.ForceTypeValidateValuesAndCopyMissing(db._definition, node, null, transformValues);
                        Utils.EnsureOrQueueIndex(db, node, doNotRegenTheseProps, newTasks);
                        if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.CreateNode;
                        yield return new PrimitiveNodeAction(PrimitiveOperation.Add, node);
                    } else { // existing
                        if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.UpdateNode;
                        nodeAction.Operation = NodeOperation.UpdateOrFail;
                        foreach (var a in toPrimitiveActions(db, nodeAction, transformValues, newTasks)) yield return a;
                    }
                }
                break;
            case NodeOperation.ForceUpsert: {
                    if (nodeExists(db, nodeAction.Node)) {
                        nodeAction.Operation = NodeOperation.ForceUpdate;
                        if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.UpdateNode;
                    } else {
                        nodeAction.Operation = NodeOperation.InsertOrFail;
                        if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.CreateNode;
                    }
                    foreach (var a in toPrimitiveActions(db, nodeAction, transformValues, newTasks)) yield return a;
                }
                break;
            case NodeOperation.ChangeType: {
                    var idNode = nodeAction.Node;
                    int uid;
                    if (nodeAction.Node is not NodeDataOnlyTypeAndId nodeUid) {
                        if (nodeAction.Node is not NodeDataOnlyTypeAndGuid nodeId) {
                            throw new Exception("NodeAction.ChangeType requires NodeDataOnlyTypeAndUId. ");
                        } else {
                            uid = db._guids.GetId(nodeId.Id);
                        }
                    } else {
                        uid = nodeUid.__Id;
                    }
                    if (!db._nodes.TryGet(uid, out var oldNode, out _)) {
                        throw new Exception("Node with id " + idNode.Id + " does not exist, cannot change type. ");
                    } else {
                        var newNode = ((NodeData)oldNode).CopyWithNewNodeType(idNode.NodeType);
                        if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.UpdateNode;
                        yield return new PrimitiveNodeAction(PrimitiveOperation.Remove, oldNode);
                        if (newNode.CreatedUtc == DateTime.MinValue) newNode.CreatedUtc = oldNode.CreatedUtc;
                        Utils.ForceTypeValidateValuesAndCopyMissing(db._definition, newNode, oldNode, transformValues);
                        Utils.EnsureOrQueueIndex(db, newNode, doNotRegenTheseProps, newTasks);
                        yield return new PrimitiveNodeAction(PrimitiveOperation.Add, newNode);
                    }
                }
                break;
            case NodeOperation.ReIndex: {
                    int id = getIdAndVerifyIdAndGuidIfGuidIsGiven(db, nodeAction);
                    Utils.QueueIndexing(db, id, db._definition.GetTypeOfNode(id), doNotRegenTheseProps, newTasks);
                }
                break;
            default:
                throw new NotImplementedException();
        }
    }
    IEnumerable<PrimitiveActionBase> toPrimitiveActions(DataStoreLocal db, RelationAction a, List<KeyValuePair<TaskData, string?>> newTasks) {
        if (a.Operation == RelationOperation.Add) {
            var source = a.Source > default(int) ? a.Source : db._guids.GetId(a.SourceGuid);
            var target = a.Target > default(int) ? a.Target : db._guids.GetId(a.TargetGuid);
            var date = a.ChangeUtc > default(DateTime) ? a.ChangeUtc : DateTime.UtcNow;
            var operation = PrimitiveOperation.Add;
            if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.AddedRelation;
            yield return new PrimitiveRelationAction(operation, a.RelationId, source, target, date);
        } else if (a.Operation == RelationOperation.Remove) {
            var source = a.Source != 0 ? a.Source : db._guids.GetId(a.SourceGuid);
            var target = a.Target != 0 ? a.Target : db._guids.GetId(a.TargetGuid);
            var date = a.ChangeUtc > default(DateTime) ? a.ChangeUtc : DateTime.UtcNow;
            var operation = PrimitiveOperation.Remove;
            if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.RemovedRelation;
            yield return new PrimitiveRelationAction(operation, a.RelationId, source, target, date);
        } else if (a.Operation == RelationOperation.Set) {
            var source = a.Source != 0 ? a.Source : db._guids.GetId(a.SourceGuid);
            var target = a.Target != 0 ? a.Target : db._guids.GetId(a.TargetGuid);
            var r = db._definition.Relations[a.RelationId];
            if (r.Contains(source, target)) yield break; // nothing to do
            // NB: important to not use "yield return" here as the values of the relation will change during the loop
            List<PrimitiveRelationAction> _relRemoveOperations = [];
            if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.SetRelation;
            foreach (var rel in r.GetOtherRelationsThatNeedsToRemovedBeforeAdd(source, target)) {
                _relRemoveOperations.Add(new(PrimitiveOperation.Remove, a.RelationId, rel.Source, rel.Target, a.ChangeUtc));
            }
            foreach (var ra in _relRemoveOperations) yield return ra;
            yield return new PrimitiveRelationAction(PrimitiveOperation.Add, a.RelationId, source, target, a.ChangeUtc);
        } else if (a.Operation == RelationOperation.Clear) {
            // if id is set use it, if guid is set look up uid, if empty return 0, indicating not given
            var source = a.Source != 0 ? a.Source : (a.SourceGuid != Guid.Empty ? db._guids.GetId(a.SourceGuid) : 0);
            var target = a.Target != 0 ? a.Target : (a.TargetGuid != Guid.Empty ? db._guids.GetId(a.TargetGuid) : 0);
            var relation = db._definition.Relations[a.RelationId];
            if (source != 0 && target != 0) {
                if (!relation.Contains(source, target)) yield break; // nothing to do
                if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.RemovedRelation;
                yield return new PrimitiveRelationAction(PrimitiveOperation.Remove, a.RelationId, source, target, a.ChangeUtc);
            } else if (source == 0 && target != 0) { // remove all relations to target
                foreach (var r in relation.Values) { // remove all relations to target
                    if (r.Target == target) {
                        if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.RemovedAllRelationsToTarget;
                        yield return new PrimitiveRelationAction(PrimitiveOperation.Remove, a.RelationId, r.Source, r.Target, a.ChangeUtc);
                    }
                }
            } else if (source != 0 && target == 0) { // remove all relations from source
                foreach (var r in relation.Values) { // remove all relations from source
                    if (r.Source == source) {
                        if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.RemovedAllRelationsFromSource;
                        yield return new PrimitiveRelationAction(PrimitiveOperation.Remove, a.RelationId, r.Source, r.Target, a.ChangeUtc);
                    }
                }
            } else { // source == 0 && target == 0 // remove all relations
                foreach (var r in relation.Values) { // remove all relations
                    if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.RemovedAllRelations;
                    yield return new PrimitiveRelationAction(PrimitiveOperation.Remove, a.RelationId, r.Source, r.Target, a.ChangeUtc);
                }
            }
        } else {
            throw new NotImplementedException();
        }
    }
    IEnumerable<PrimitiveActionBase> toPrimitiveActions(DataStoreLocal db, NodePropertyAction a, bool transformValues, List<KeyValuePair<TaskData, string?>> newTasks, QueryContext ctx) {
        List<int> uints = [];
        if (a.NodeIds != null) uints.AddRange(a.NodeIds);
        if (a.NodeGuids != null) uints.AddRange(a.NodeGuids.Select(db._guids.GetId));
        if (a.TypeId != null) {
            if (uints.Count > 0) throw new("Cannot update property for multiple nodes and a type. ");
            uints.AddRange(db._definition.GetAllIdsForType(a.TypeId.Value, ctx).Enumerate());
        }
        // ignore nodes that does not exist, copy to avoid changing original node in case of error:
        var nodesInner = db._nodes.Get([.. uints.Where(db._nodes.Contains)]).Select(n => n.Copy());
        var nodesOuter = db.ToOuter(nodesInner, ctx);
        foreach (var node in nodesOuter) {
            switch (a.Operation) {
                case NodePropertyOperation.ForceUpdate: {
                        if (a.Values == null) throw new("Value cannot be null if updating a property. ");
                        for (var i = 0; i < a.PropertyIds.Length; i++) {
                            node.AddOrUpdate(a.PropertyIds[i], a.Values[i]);
                        }
                        if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.ChangedProperty;
                        var actions = toPrimitiveActions(db, NodeAction.ForceUpdate(node), transformValues, newTasks, a.PropertyIds);
                        foreach (var action in actions) yield return action;
                        yield break;
                    }
                case NodePropertyOperation.UpdateIfDifferent: {
                        if (a.Values == null) throw new("Value cannot be null if ensuring a property. ");
                        bool anyChanged = false;
                        for (var i = 0; i < a.PropertyIds.Length; i++) {
                            var propDef = db._definition.Properties[a.PropertyIds[i]];
                            var isMissingOrIsDifferent = !node.TryGetValue(a.PropertyIds[i], out var existingValue)
                                || !propDef.ForceValueType(a.Values[i], out _).Equals(propDef.ForceValueType(existingValue, out _));
                            if (isMissingOrIsDifferent) {
                                node.AddOrUpdate(a.PropertyIds[i], a.Values[i]);
                                anyChanged = true;
                            }
                        }
                        if (anyChanged) { // nothing changed, so no need to update
                            if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.ChangedProperty;
                            var actions = toPrimitiveActions(db, NodeAction.ForceUpdate(node), transformValues, newTasks, a.PropertyIds);
                            foreach (var action in actions) yield return action;
                        }
                        yield break;
                    }
                case NodePropertyOperation.Reset: {
                        for (var i = 0; i < a.PropertyIds.Length; i++) {
                            node.RemoveIfPresent(a.PropertyIds[i]);
                        }
                        if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.ChangedProperty;
                        var actions = toPrimitiveActions(db, NodeAction.ForceUpdate(node), transformValues, newTasks, a.PropertyIds);
                        foreach (var action in actions) yield return action;
                        yield break;
                    }
                case NodePropertyOperation.Add: {
                        if (a.Values == null) throw new("Value cannot be null if adding to a property. ");
                        for (var i = 0; i < a.PropertyIds.Length; i++) {
                            var propDef = db._definition.Properties[a.PropertyIds[i]];
                            if (node.TryGetValue(a.PropertyIds[i], out var oldValue)) {
                                node.AddOrUpdate(a.PropertyIds[i], UtilsMath.Add(propDef.Model, oldValue, a.Values[i]));
                            } else {
                                node.AddOrUpdate(a.PropertyIds[i], a.Values[i]);
                            }
                        }
                        if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.ChangedProperty;
                        var actions = toPrimitiveActions(db, NodeAction.ForceUpdate(node), transformValues, newTasks, a.PropertyIds);
                        foreach (var action in actions) yield return action;
                        yield break;
                    }
                case NodePropertyOperation.Multiply: {
                        if (a.Values == null) throw new("Value cannot be null if adding to a property. ");
                        for (var i = 0; i < a.PropertyIds.Length; i++) {
                            var propDef = db._definition.Properties[a.PropertyIds[i]];
                            if (node.TryGetValue(a.PropertyIds[i], out var oldValue)) {
                                node.AddOrUpdate(a.PropertyIds[i], UtilsMath.Multiply(propDef.Model, oldValue, a.Values[i]));
                            } else {
                                node.AddOrUpdate(a.PropertyIds[i], a.Values[i]);
                            }
                        }
                        if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.ChangedProperty;
                        var actions = toPrimitiveActions(db, NodeAction.ForceUpdate(node), transformValues, newTasks, a.PropertyIds);
                        foreach (var action in actions) yield return action;
                        yield break;
                    }
                default:
                    throw new NotImplementedException();
            }
        }
    }
    IEnumerable<PrimitiveActionBase> toPrimitiveActions(DataStoreLocal db, NodePropertyValidation a, List<KeyValuePair<TaskData, string?>> newTasks) {
        List<int> uints = [];
        if (a.NodeIds != null) uints.AddRange(a.NodeIds);
        if (a.NodeGuids != null) uints.AddRange(a.NodeGuids.Select(db._guids.GetId));
        var propDef = db._definition.Properties[a.PropertyId];
        var nodes = db._nodes.Get(uints.ToArray());
        foreach (var node in nodes) {
            var value = node.TryGetValue(a.PropertyId, out var v) ? v : propDef.GetDefaultValue();
            if (!propDef.SatisfyValueRequirement(value, a.Value!, a.Requirement)) {
                throw new("Node with id " + node.Id + " does not satisfy the requirement for property " + propDef.CodeName + ". ");
            }
        }
        yield break;
    }
    IEnumerable<PrimitiveActionBase> toPrimitiveActions(DataStoreLocal db, NodeRevisionAction a, bool transformValues, List<KeyValuePair<TaskData, string?>> newTasks) {
        throw new NotImplementedException("NodeRevisionAction is not yet implemented in DataStoreLocal. ");
    }
}
