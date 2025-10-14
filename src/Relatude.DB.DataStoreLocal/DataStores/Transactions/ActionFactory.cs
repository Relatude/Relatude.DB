using Relatude.DB.Datamodels;
using Relatude.DB.Tasks;
using Relatude.DB.Transactions;
namespace Relatude.DB.DataStores.Transactions;
internal static class ActionFactory {
    static ResultingOperation? _lastResultingOperation = null; // only possible as there is only one thread per transaction
    public static PrimitiveActionBase[] Convert(DataStoreLocal db, ActionBase complexAction, bool transformValues, List<KeyValuePair<TaskData, string?>> newTasks, out ResultingOperation resultingOperation) {
        _lastResultingOperation = ResultingOperation.None; // default
        var src = complexAction switch {
            RelationAction relationAction => toPrimitiveActions(db, relationAction, newTasks),
            NodeAction nodeAction => toPrimitiveActions(db, nodeAction, transformValues, newTasks),
            NodePropertyAction nodePropertyAction => toPrimitiveActions(db, nodePropertyAction, transformValues, newTasks),
            NodePropertyValidation nodePropertyValidation => toPrimitiveActions(db, nodePropertyValidation, newTasks),
            _ => throw new NotImplementedException(),
        };
        resultingOperation = _lastResultingOperation == null ? ResultingOperation.None : _lastResultingOperation.Value; 
        try {
            return src.ToArray(); // force conversion of action first, then return the array
        } catch (Exception err) {
            // converting an action should never change data, so a failed should not cause any integrity loss:
            throw new ExceptionWithoutIntegrityLoss("Failed to " + complexAction.ToString(db.Datamodel) + err.Message, err);
        }
    }
    static void ensureIdAndGuid(DataStoreLocal db, INodeData node) {
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
    static void ensureIdsAndCreateIdIfMissing(DataStoreLocal db, INodeData node) {
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
    static int getIdAndVerifyIdAndGuidIfGuidIsGiven(DataStoreLocal db, NodeAction nodeAction) {
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
    static bool nodeExists(DataStoreLocal db, INodeData node) {
        int id = 0;
        if (node.Id != Guid.Empty) db._guids.TryGetId(node.Id, out id);
        else if (node.__Id != 0) id = node.__Id;
        if (id == 0) return false;
        return db._nodes.Contains(id);
    }
    static IEnumerable<PrimitiveActionBase> toPrimitiveActions(DataStoreLocal db, NodeAction nodeAction, bool transformValues, List<KeyValuePair<TaskData, string?>> newTasks, Guid[]? doNotRegenTheseProps = null) {
        switch (nodeAction.Operation) {
            case NodeOperation.InsertOrFail: {
                    var node = nodeAction.Node;
                    ensureIdsAndCreateIdIfMissing(db, node);
                    if (node.CreatedUtc == DateTime.MinValue) node.CreatedUtc = DateTime.UtcNow;
                    Utils.ForceTypeValidateValuesAndCopyMissing(db._definition, node, null, transformValues);
                    Utils.EnsureOrQueueIndex(db, node, doNotRegenTheseProps, newTasks);
                    if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.CreateNode;
                    yield return new PrimitiveNodeAction(PrimitiveOperation.Add, nodeAction.Node); // will eventually throw if node with id already exists
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
            case NodeOperation.ForceUpdate: {
                    var node = nodeAction.Node;
                    ensureIdAndGuid(db, node);
                    if (!db._nodes.TryGet(node.__Id, out var oldNode, out _)) {// is new
                        throw new Exception("Node with id " + node.Id + " does not exist, cannot update. Use InsertIfNotExists or Upsert instead. ");
                    } else {
                        if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.UpdateNode;
                        yield return new PrimitiveNodeAction(PrimitiveOperation.Remove, oldNode);
                        if (node.CreatedUtc == DateTime.MinValue) node.CreatedUtc = oldNode.CreatedUtc;
                        Utils.ForceTypeValidateValuesAndCopyMissing(db._definition, node, oldNode, transformValues);
                        Utils.EnsureOrQueueIndex(db, node, doNotRegenTheseProps, newTasks);
                        yield return new PrimitiveNodeAction(PrimitiveOperation.Add, node);
                    }
                }
                break;
            case NodeOperation.UpdateIfExists: {
                    var node = nodeAction.Node;
                    ensureIdAndGuid(db, node);
                    if (!db._nodes.TryGet(node.__Id, out var oldNode, out _)) { // is new
                                                                                // ignore if node does not exist
                    } else {
                        if (Utils.AreDifferentIgnoringGeneratedProps(node, oldNode, db._definition)) {
                            if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.UpdateNode;
                            yield return new PrimitiveNodeAction(PrimitiveOperation.Remove, oldNode);
                            if (node.CreatedUtc == DateTime.MinValue) node.CreatedUtc = oldNode.CreatedUtc;
                            Utils.ForceTypeValidateValuesAndCopyMissing(db._definition, node, oldNode, transformValues);
                            Utils.EnsureOrQueueIndex(db, node, doNotRegenTheseProps, newTasks);
                            yield return new PrimitiveNodeAction(PrimitiveOperation.Add, node);
                        }
                    }
                }
                break;
            case NodeOperation.UpdateOrFail: {
                    var node = nodeAction.Node;
                    ensureIdAndGuid(db, node);
                    if (!db._nodes.TryGet(node.__Id, out var oldNode, out _)) { // is new                        
                        throw new Exception("Node with id " + node.Id + " does not exist, cannot update if different. Use insert or upsert instead. ");
                    } else {
                        if (Utils.AreDifferentIgnoringGeneratedProps(node, oldNode, db._definition)) {
                            if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.UpdateNode;
                            yield return new PrimitiveNodeAction(PrimitiveOperation.Remove, oldNode);
                            if (node.CreatedUtc == DateTime.MinValue) node.CreatedUtc = oldNode.CreatedUtc;
                            Utils.ForceTypeValidateValuesAndCopyMissing(db._definition, node, oldNode, transformValues);
                            Utils.EnsureOrQueueIndex(db, node, doNotRegenTheseProps, newTasks);
                            yield return new PrimitiveNodeAction(PrimitiveOperation.Add, node);
                        }
                    }

                }
                break;
            case NodeOperation.Upsert: {
                    var node = nodeAction.Node;
                    ensureIdsAndCreateIdIfMissing(db, node);
                    if (!db._nodes.TryGet(node.__Id, out var oldNode, out _)) { // is new
                        if (node.CreatedUtc == DateTime.MinValue) node.CreatedUtc = DateTime.UtcNow;
                        Utils.ForceTypeValidateValuesAndCopyMissing(db._definition, node, null, transformValues);
                        Utils.EnsureOrQueueIndex(db, node, doNotRegenTheseProps, newTasks);
                        if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.CreateNode;
                        yield return new PrimitiveNodeAction(PrimitiveOperation.Add, node);
                    } else { // existing
                        if (Utils.AreDifferentIgnoringGeneratedProps(node, oldNode, db._definition)) {
                            if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.UpdateNode;
                            yield return new PrimitiveNodeAction(PrimitiveOperation.Remove, oldNode);
                            if (node.CreatedUtc == DateTime.MinValue) node.CreatedUtc = oldNode.CreatedUtc;
                            Utils.ForceTypeValidateValuesAndCopyMissing(db._definition, node, oldNode, transformValues);
                            Utils.EnsureOrQueueIndex(db, node, doNotRegenTheseProps, newTasks);
                            yield return new PrimitiveNodeAction(PrimitiveOperation.Add, node);
                        }
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
                    if (nodeAction.Node is not NodeDataOnlyTypeAndUId nodeUid) {
                        if (nodeAction.Node is not NodeDataOnlyTypeAndId nodeId) {
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
    static IEnumerable<PrimitiveActionBase> toPrimitiveActions(DataStoreLocal db, RelationAction a, List<KeyValuePair<TaskData, string?>> newTasks) {
        if (a.Operation == RelationOperation.Add || a.Operation == RelationOperation.Remove) {
            var source = a.Source > default(int) ? a.Source : db._guids.GetId(a.SourceGuid);
            var target = a.Target > default(int) ? a.Target : db._guids.GetId(a.TargetGuid);
            var date = a.ChangeUtc > default(DateTime) ? a.ChangeUtc : DateTime.UtcNow;
            var operation = a.Operation == RelationOperation.Add ? PrimitiveOperation.Add : PrimitiveOperation.Remove;
            if (!_lastResultingOperation.HasValue) _lastResultingOperation = operation == PrimitiveOperation.Add ? ResultingOperation.AddedRelation : ResultingOperation.RemovedRelation;
            yield return new PrimitiveRelationAction(operation, a.RelationId, source, target, date);
        } else if (a.Operation == RelationOperation.Set) {
            var source = a.Source > default(int) ? a.Source : db._guids.GetId(a.SourceGuid);
            var target = a.Target > default(int) ? a.Target : db._guids.GetId(a.TargetGuid);
            var r = db._definition.Relations[a.RelationId];
            if (r.Contains(source, target)) yield break; // nothing to do
            // NB: important to not use "yield return" here as the values of the relation will change during the loop
            List<PrimitiveRelationAction> _relRemoveOperations = [];
            foreach (var rel in r.GetOtherRelationsThatNeedsToRemovedBeforeAdd(source, target)) {
                _relRemoveOperations.Add(new(PrimitiveOperation.Remove, a.RelationId, rel.Source, rel.Target, a.ChangeUtc));
            }
            foreach (var ra in _relRemoveOperations) yield return ra;
            yield return new PrimitiveRelationAction(PrimitiveOperation.Add, a.RelationId, source, target, a.ChangeUtc);
        } else if (a.Operation == RelationOperation.Clear) {
            // if uid is set use it, if guid is set look up uid, if empty return 0, indicating not given
            var source = a.Source != 0 ? a.Source : (a.SourceGuid != Guid.Empty ? db._guids.GetId(a.SourceGuid) : 0);
            var target = a.Target != 0 ? a.Target : (a.TargetGuid != Guid.Empty ? db._guids.GetId(a.TargetGuid) : 0);
            var relation = db._definition.Relations[a.RelationId];
            if (source != 0 && target != 0) {
                if (!relation.Contains(source, target)) yield break; // nothing to do
                yield return new PrimitiveRelationAction(PrimitiveOperation.Remove, a.RelationId, source, target, a.ChangeUtc);
            } else if (source == 0 && target != 0) {
                foreach (var r in relation.Values) { // remove all relations to target
                    if (r.Target == target) yield return new PrimitiveRelationAction(PrimitiveOperation.Remove, a.RelationId, r.Source, r.Target, a.ChangeUtc);
                }
            } else if (source != 0 && target == 0) {
                foreach (var r in relation.Values) { // remove all relations from source
                    if (r.Source == source) yield return new PrimitiveRelationAction(PrimitiveOperation.Remove, a.RelationId, r.Source, r.Target, a.ChangeUtc);
                }
            } else { // source == 0 && target == 0                
                foreach (var r in relation.Values) { // remove all relations
                    yield return new PrimitiveRelationAction(PrimitiveOperation.Remove, a.RelationId, r.Source, r.Target, a.ChangeUtc);
                }
            }
        } else {
            throw new NotImplementedException();
        }
    }
    static IEnumerable<PrimitiveActionBase> toPrimitiveActions(DataStoreLocal db, NodePropertyAction a, bool transformValues, List<KeyValuePair<TaskData, string?>> newTasks) {
        List<int> uints = [];
        if (a.NodeIds != null) uints.AddRange(a.NodeIds);
        if (a.NodeGuids != null) uints.AddRange(a.NodeGuids.Select(db._guids.GetId));
        if (a.TypeId != null) {
            if (uints.Count > 0) throw new("Cannot update property for multiple nodes and a type. ");
            uints.AddRange(db._definition.GetAllIdsForType(a.TypeId.Value).Enumerate());
        }
        // ignore nodes that does not exist, copy to avoid changing original node in case of error:
        var nodes = db._nodes.Get(uints.Where(db._nodes.Contains).ToArray()).Select(n => n.Copy());
        foreach (var node in nodes) {
            switch (a.Operation) {
                case NodePropertyOperation.Update: {
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
    static IEnumerable<PrimitiveActionBase> toPrimitiveActions(DataStoreLocal db, NodePropertyValidation a, List<KeyValuePair<TaskData, string?>> newTasks) {
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
}
