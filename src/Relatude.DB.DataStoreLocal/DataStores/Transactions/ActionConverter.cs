using Relatude.DB.Tasks;
using Relatude.DB.Datamodels;
using Relatude.DB.Transactions;
namespace Relatude.DB.DataStores.Transactions;

internal class ActionConverter {
    ResultingOperation? _lastResultingOperation = null; // only possible as there is only one thread per transaction
    public IEnumerable<PrimitiveActionBase> Convert(DataStoreLocal db, ActionBase complexAction, bool transformValues, List<KeyValuePair<TaskData, string?>> newTasks, QueryContext ctx, out ResultingOperation resultingOperation) {
        _lastResultingOperation = ResultingOperation.None; // default
        var src = complexAction switch {
            RelationAction relationAction => toPrimitiveActions(db, relationAction, newTasks),
            NodeAction nodeAction => toPrimitiveActions(db, nodeAction, transformValues, newTasks),
            NodePropertyAction nodePropertyAction => toPrimitiveActions(db, nodePropertyAction, transformValues, newTasks, ctx),
            NodePropertyValidation nodePropertyValidation => toPrimitiveActions(db, nodePropertyValidation, newTasks),
            NodeRevisionAction nodeRevisionAction => toPrimitiveActions(db, nodeRevisionAction, transformValues, ctx, newTasks),
            _ => throw new NotSupportedException(),
        };
        resultingOperation = _lastResultingOperation == null ? ResultingOperation.None : _lastResultingOperation.Value;
        return src;
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
                            or NodeOperation.UpdateOrFail => Utils.AreDifferentIgnoringGeneratedPropsAndMeta(node, oldNode, db._definition),
                            NodeOperation.ForceUpdate => true,
                            _ => throw new NotImplementedException(),
                        };
                        if (performUpdate) {
                            if (!_lastResultingOperation.HasValue) _lastResultingOperation = ResultingOperation.UpdateNode;
                            yield return new PrimitiveNodeAction(PrimitiveOperation.Remove, oldNode); // remove old first
                            if (oldNode is NodeDataRevisions revsNode) { // revison handling is relevant, revision must already exists:
                                if (node is not NodeDataRevision nodeRev) throw new Exception("Cannot determine revision to update. ");
                                var typeDef = db._definition.NodeTypes[oldNode.NodeType];
                                var newNode = Utils.CreateNewRevisionsNodeWithUpdatedValues(revsNode, nodeRev, typeDef, transformValues);
                                if (newNode.CreatedUtc == DateTime.MinValue) newNode.CreatedUtc = oldNode.CreatedUtc;
                                yield return new PrimitiveNodeAction(PrimitiveOperation.Add, newNode);
                            } else {
                                if (node is not INodeDataInner nodeInner) throw new Exception("NodeAction requires node to be of type INodeDataInner. ");
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
                        var newNode = ((NodeData)oldNode).CopyAndChangeNodeType(idNode.NodeType);
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
        var nodesInner = db._nodes.Get([.. uints.Where(db._nodes.Contains)]).Select(n => n.CopyInner());
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
    static bool forgivingRevisionActions = true;
    IEnumerable<PrimitiveActionBase> toPrimitiveActions(DataStoreLocal db, NodeRevisionAction a, bool transformValues, QueryContext key, List<KeyValuePair<TaskData, string?>> newTasks) {
        int nodeId = db._guids.ValidateAndReturnIntId(a.NodeIdKey);
        if (!db._nodes.TryGet(nodeId, out var existingNode, out _)) throw new Exception("Node with id " + a.NodeIdKey + " does not exist, cannot perform revision action. ");
        switch (a.Operation) {
            case NodeRevisionOperation.EnableRevisions: {
                    if (existingNode is NodeData nd) {
                        int revisionType = (int)(a.RevisionType ?? RevisionType.Published);
                        var revisionId = a.RevisionId ?? Guid.NewGuid();
                        var rev = nd.CopyAndConvertToNodeDataRevision(IInnerNodeMeta.ChangeRevision(nd.Meta, revisionType), revisionId);
                        yield return new PrimitiveNodeAction(PrimitiveOperation.Remove, existingNode);
                        var newNode = new NodeDataRevisions(nd.Id, nd.__Id, nd.NodeType, [rev]);
                        yield return new PrimitiveNodeAction(PrimitiveOperation.Add, newNode);
                    } else if (existingNode is NodeDataRevisions) {
                        if (!forgivingRevisionActions) throw new Exception("Cannot enable revisions for node with id " + a.NodeIdKey + " because it is already enabled. ");
                    }
                }
                break;
            case NodeRevisionOperation.DisableRevisions: {
                    if (existingNode is NodeDataRevisions revs) {
                        if (a.RevisionId == null && revs.Revisions.Length > 1) throw new Exception("RevisionId must be given to disable revisions if there are multiple revisions, to determine which revision to keep. ");
                        var revisionToKeep = revs.Revisions.FirstOrDefault(r => r.RevisionId == a.RevisionId);
                        if (revs.Revisions.Length == 1) revisionToKeep = revs.Revisions[0]; // if there is only one revision, we can keep that one even if no revision id is given
                        if (revisionToKeep == null) throw new Exception("Revision with id " + a.RevisionId + " does not exist, cannot disable revisions. ");
                        yield return new PrimitiveNodeAction(PrimitiveOperation.Remove, existingNode);
                        var newNode = revisionToKeep.CopyAndConvertToNodeData();
                        yield return new PrimitiveNodeAction(PrimitiveOperation.Add, newNode);
                    } else if (existingNode is NodeData) {
                        if (!forgivingRevisionActions) throw new Exception("Cannot disable revisions for node with id " + a.NodeIdKey + " because it is not of type NodeDataRevisions. ");
                    }
                }
                break;
            case NodeRevisionOperation.UpdateMeta: {  // changes all in meta except, culture, revision type/key. Also copy culture insensitive values to other props where relevant
                    if (existingNode is NodeDataRevisions revsNode) {
                        yield return new PrimitiveNodeAction(PrimitiveOperation.Remove, existingNode);
                        if (a.RevisionId == null) throw new Exception("RevisionId must be given to update meta of a revision, to determine which revision to update. ");
                        var newNode = Utils.CopyRevisionNodeAndChangeMetaNotRevisionTypeOrCulture(revsNode, a.Meta, a.RevisionId.Value);
                        yield return new PrimitiveNodeAction(PrimitiveOperation.Add, newNode);
                    } else if (existingNode is NodeData nd) {
                        yield return new PrimitiveNodeAction(PrimitiveOperation.Remove, existingNode);
                        var newNode = nd.CopyAndChangeMeta(a.Meta);
                        yield return new PrimitiveNodeAction(PrimitiveOperation.Add, newNode);
                    }
                }
                break;
            case NodeRevisionOperation.CreateRevision: {
                    var sourceRevisionId = a.SourceRevisionId;

                    Guid cultureId = Guid.Empty;
                    if (a.CultureId != null) {
                        cultureId = a.CultureId.Value;
                    } else if (a.CultureCode == string.Empty) {
                        cultureId = Guid.Empty;
                    } else if (a.CultureCode != null) {
                        if (!db._nativeModelStore.TryGetCultureId(a.CultureCode, out cultureId)) {
                            throw new Exception("Culture with code " + a.CultureCode + " does not exist, cannot determine culture for new revision. ");
                        }
                    }

                    if (existingNode is not NodeDataRevisions revs) {
                        if (!forgivingRevisionActions) throw new Exception("Cannot create revision for node with id " + a.NodeIdKey + " because revisions are not enabled for this node. ");
                        if (sourceRevisionId == null) sourceRevisionId = Guid.NewGuid();
                        var enableAction = NodeRevisionAction.EnableRevisions(a.NodeIdKey, sourceRevisionId);
                        foreach (var subAction in toPrimitiveActions(db, enableAction, transformValues, key, newTasks)) yield return subAction;
                        existingNode = db._nodes.Get(nodeId, out _); // get the newly created revisions node
                        existingNode = existingNode.CopyInner();
                        revs = existingNode as NodeDataRevisions ?? throw new Exception("Failed to enable revisions for node with id " + a.NodeIdKey + ", cannot create revision. ");
                    }
                    yield return new PrimitiveNodeAction(PrimitiveOperation.Remove, existingNode);
                    if (a.RevisionId == null) throw new Exception("RevisionId must be given to create a new revision. ");
                    if (sourceRevisionId == null) throw new Exception("SourceRevisionId must be given to create a new revision. ");
                    if (sourceRevisionId == a.RevisionId) throw new Exception("SourceRevisionId cannot be the same as the new revision id. ");
                    var sourceRevision = revs.Revisions.FirstOrDefault(r => r.RevisionId == sourceRevisionId);
                    if (sourceRevision == null) throw new Exception("Revision with id " + a.SourceRevisionId + " does not exist, cannot create revision. ");
                    if (a.RevisionType == null) throw new Exception("RevisionType must be given to create a new revision. ");
                    int revisionKey = RevisionUtil.CreateNewRevisionKey(a.RevisionType.Value, cultureId, revs.Revisions);
                    var newMeta = IInnerNodeMeta.ChangeRevision(sourceRevision.Meta, revisionKey);
                    if (a.CultureCode != null && newMeta?.CultureId != cultureId) newMeta = IInnerNodeMeta.ChangeCulture(newMeta, cultureId);
                    NodeDataRevision newRev = sourceRevision.CopyAndChangeMetaAndRevisionId(newMeta, a.RevisionId.Value);
                    var newRevs = new NodeDataRevision[revs.Revisions.Length + 1];
                    Array.Copy(revs.Revisions, newRevs, revs.Revisions.Length);
                    newRevs[^1] = newRev;
                    var newNode = new NodeDataRevisions(revs.Id, revs.__Id, revs.NodeType, newRevs);
                    yield return new PrimitiveNodeAction(PrimitiveOperation.Add, newNode);
                }
                break;
            case NodeRevisionOperation.DeleteRevision: {
                    if (existingNode is not NodeDataRevisions revs) throw new Exception("Cannot delete revision for node with id " + a.NodeIdKey + " because revisions are not enabled for this node. Enable revisions first if you want to delete a revision. ");
                    if (a.RevisionId == null) throw new Exception("RevisionId must be given to delete a revision. ");
                    var posOfRevToDelete = revs.Revisions.ToList().FindIndex(r => r.RevisionId == a.RevisionId);
                    if (posOfRevToDelete == -1 && forgivingRevisionActions) yield break; // if forgiving, ignore if revision to delete does not exist
                    if (posOfRevToDelete == -1) throw new Exception("Revision with id " + a.RevisionId + " does not exist, cannot delete revision. ");
                    if (revs.Revisions.Length == 1) throw new Exception("Cannot delete the last revision for node with id " + a.NodeIdKey + ". ");
                    var newRevs = revs.Revisions.Where((r, i) => i != posOfRevToDelete).ToArray();
                    var newNode = new NodeDataRevisions(revs.Id, revs.__Id, revs.NodeType, newRevs);
                    yield return new PrimitiveNodeAction(PrimitiveOperation.Remove, existingNode);
                    yield return new PrimitiveNodeAction(PrimitiveOperation.Add, newNode);
                }
                break;
            case NodeRevisionOperation.ChangeRevisionType: {
                    if (existingNode is not NodeDataRevisions revs) throw new Exception("Cannot change revision type for node with id " + a.NodeIdKey + " because revisions are not enabled for this node. Enable revisions first if you want to change revision type. ");
                    if (a.RevisionId == null) throw new Exception("RevisionId must be given to change revision type. ");
                    var posOfRevToChange = revs.Revisions.ToList().FindIndex(r => r.RevisionId == a.RevisionId);
                    if (posOfRevToChange == -1) throw new Exception("Revision with id " + a.RevisionId + " does not exist, cannot change revision type. ");
                    var revToChange = revs.Revisions[posOfRevToChange];
                    if (a.RevisionType == null) throw new Exception("RevisionType must be given to change revision type. ");
                    if (revToChange.RevisionType == a.RevisionType.Value) yield break; // nothing to do if revision type is the same    
                    Guid cultureId= Guid.Empty;
                    if(revToChange.Meta != null) cultureId = revToChange.Meta.CultureId;
                    var newKey = RevisionUtil.CreateNewRevisionKey(a.RevisionType.Value, cultureId, revs.Revisions); // validate that the new revision type can be used with the existing revisions, will throw if not valid
                    var newMeta = IInnerNodeMeta.ChangeRevision(revToChange.Meta, newKey);
                    var newRev = revToChange.CopyAndChangeMeta(newMeta);
                    var newRevs = revs.Revisions.ToArray();
                    newRevs[posOfRevToChange] = newRev;
                    var newNode = new NodeDataRevisions(revs.Id, revs.__Id, revs.NodeType, newRevs);
                    if (a.RevisionType.Value == RevisionType.Published) { // if changing to published, copy culture specific values to culture invariant props if relevant
                        var typeDef = db._definition.NodeTypes[revs.NodeType];
                        Utils.UpdateCultureInsensitiveValues(typeDef, newNode, a.RevisionId.Value); // does not copy node, changes values in place so node must be new and not referenced other places
                    }
                    yield return new PrimitiveNodeAction(PrimitiveOperation.Remove, existingNode);
                    yield return new PrimitiveNodeAction(PrimitiveOperation.Add, newNode);
                }
                break;
            case NodeRevisionOperation.ChangeRevisionCulture: {
                    if (existingNode is not NodeDataRevisions revs) throw new Exception("Cannot change revision culture for node with id " + a.NodeIdKey + " because revisions are not enabled for this node. Enable revisions first if you want to change revision culture. ");
                    if (a.RevisionId == null) throw new Exception("RevisionId must be given to change revision culture. ");
                    var posOfRevToChange = revs.Revisions.ToList().FindIndex(r => r.RevisionId == a.RevisionId);
                    if (posOfRevToChange == -1) throw new Exception("Revision with id " + a.RevisionId + " does not exist, cannot change revision culture. ");
                    var revToChange = revs.Revisions[posOfRevToChange];

                    Guid cultureId = Guid.Empty;
                    if (a.CultureId == null && a.CultureCode == null) {
                        throw new Exception("Culture must be given to change revision culture. ");
                    } else if (a.CultureId != null) {
                        cultureId = a.CultureId.Value;
                    } else if (a.CultureCode == string.Empty) {
                        cultureId = Guid.Empty;
                    } else if (a.CultureCode != null) {
                        if (!db._nativeModelStore.TryGetCultureId(a.CultureCode, out cultureId)) {
                            throw new Exception("Culture with code " + a.CultureCode + " does not exist, cannot change revision culture. ");
                        }
                    }

                    if (revToChange.CultureId == cultureId) yield break; // nothing to do if culture is the same
                    var newMeta = IInnerNodeMeta.ChangeCulture(revToChange.Meta, cultureId);
                    var newRev = revToChange.CopyAndChangeMeta(newMeta);
                    var newRevs = revs.Revisions.ToArray();
                    newRevs[posOfRevToChange] = newRev;
                    var newNode = new NodeDataRevisions(revs.Id, revs.__Id, revs.NodeType, newRevs);
                    yield return new PrimitiveNodeAction(PrimitiveOperation.Remove, existingNode);
                    yield return new PrimitiveNodeAction(PrimitiveOperation.Add, newNode);
                }
                break;
            default:
                break;
        }
    }
}