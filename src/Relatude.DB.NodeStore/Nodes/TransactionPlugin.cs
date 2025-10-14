using System.Linq.Expressions;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Transactions;
namespace Relatude.DB.Nodes;
public class PropertyValuePair(Guid propertyId, object? oldValue, object? newValue) {
    public Guid PropertyId { get; } = propertyId;
    public object? OldValue { get; } = oldValue;
    public object? NewValue { get; } = newValue;
}
public class PropertyHelper<T>(NodeStore store, Transaction transaction) where T : notnull {
    public Guid GetPropertyId(Expression<Func<T, object>> expression) => store.Mapper.GetProperty(expression).Id;
    public Guid GetPropertyId(string propertyName) => store.Mapper.GetProperty<T>(propertyName).Id;
    public IEnumerable<T> GetNodes(Guid[]? nodeIds) {
        if (nodeIds == null || nodeIds.Length == 0) return [];
        return store.Query<T>(nodeIds).Execute();
    }
    public void SetProperty(Guid nodeId, Expression<Func<T, object>> property, object value) {
        transaction.UpdateProperty(nodeId, GetPropertyId(property), value);
    }
    public object GetProperty(Guid nodeId, Expression<Func<T, object>> property) {
        var nodeData = store.Datastore.Get(nodeId);
        if (nodeData == null) throw new InvalidOperationException($"Node with ID {nodeId} not found.");
        var propertyId = GetPropertyId(property);
        if (nodeData.TryGetValue(propertyId, out var value)) return value;
        throw new InvalidOperationException($"Property {propertyId} not found on node with ID {nodeId}.");
    }
}
public class NodeHelper<T>(NodeStore store, NodeAction action) where T : notnull {
    public List<PropertyValuePair> GetChangedPropertiesAndValues() {
        var nodeData = action.Node;
        var oldNodeData = store.Datastore.Get(nodeData.Id);
        if (oldNodeData == null) return [];
        var changedProperties = new List<PropertyValuePair>();
        foreach (var p in nodeData.Values) {
            if (oldNodeData.TryGetValue(p.PropertyId, out var oldValue)) {
                if (!object.Equals(oldValue, p.Value)) {
                    changedProperties.Add(new(p.PropertyId, oldValue, p.Value)); // value has changed
                }
            } else {
                changedProperties.Add(new(p.PropertyId, null, p.Value)); // property was added
            }
        }
        foreach (var p in oldNodeData.Values) {
            if (!nodeData.TryGetValue(p.PropertyId, out var oldValue)) {
                changedProperties.Add(new(p.PropertyId, p.Value, null)); // property was removed
            }
        }
        return changedProperties;
    }
    public List<Guid> GetChangedPropertyIds() {
        var nodeData = action.Node;
        var oldNodeData = store.Datastore.Get(nodeData.Id);
        if (oldNodeData == null) return [];
        var changedProperties = new List<Guid>();
        foreach (var p in nodeData.Values) {
            if (oldNodeData.TryGetValue(p.PropertyId, out var oldValue)) {
                if (!object.Equals(oldValue, p.Value)) changedProperties.Add(p.PropertyId); // value has changed                
            } else {
                changedProperties.Add(p.PropertyId); // property was added
            }
        }
        foreach (var p in oldNodeData.Values) {
            if (!nodeData.TryGetValue(p.PropertyId, out _)) changedProperties.Add(p.PropertyId); // property was removed
        }
        return changedProperties;
    }
    public Guid GetPropertyId(Expression<Func<T, object>> expression) => store.Mapper.GetProperty(expression).Id;
    public Guid GetPropertyId(string propertyName) => store.Mapper.GetProperty<T>(propertyName).Id;
    public T GetNode() => store.Mapper.CreateObjectFromNodeData<T>(action.Node);
    public void SetNode(T node) => action.Node = store.Mapper.CreateNodeDataFromObject(node, null);
    public bool HasPropertyChanged(Expression<Func<T, object>> expression) {
        var nodeData = action.Node;
        var oldNodeData = store.Datastore.Get(nodeData.Id);
        if (oldNodeData == null) return false;
        var propertyId = store.Mapper.GetProperty(expression).Id;
        if (nodeData.TryGetValue(propertyId, out var newValue)) {
            if (oldNodeData.TryGetValue(propertyId, out var oldValue)) {
                return !object.Equals(oldValue, newValue);
            }
        }
        return true;
    }
    public void SetProperty(Expression<Func<T, object>> property, object value) {
        action.Node.AddOrUpdate(GetPropertyId(property), value);
    }
    public object GetProperty(Expression<Func<T, object>> property) {
        var propertyId = GetPropertyId(property);
        if (action.Node.TryGetValue(propertyId, out var existingValue)) return existingValue;
        throw new InvalidOperationException($"Property {propertyId} not found in node {action.Node.Id}.");
    }
}
public interface IPlugin {
    NodeStore Store { get; set; }
}
public interface ITransactionPlugin : IPlugin {
}
public interface INodeTransactionPlugin : ITransactionPlugin {
    void AddIdKeysThatNeedTypeInfo(ActionBase action, ref List<IdKey>? keys);
    List<IdKey> GetRelevantNodeIds(ActionBase action, Dictionary<IdKey, Guid>? typeInfo);
    void OnBefore(IdKey id, ActionBase action, Transaction transaction);
    void OnAfter(IdKey id, ActionBase action, ResultingOperation resultingOperation);
    void OnAfterError(IdKey id, ActionBase action, Exception error);
}
public interface IRelationTransactionPlugin : IPlugin {
    void OnBefore(IdKey id, RelationAction action, Transaction transaction);
    void OnAfter(IdKey id, RelationAction action);
    void OnError(IdKey id, RelationAction action, Exception error);
    bool IsRelevant(RelationAction action);
}
public abstract class NodeTransactionPlugin<T> : INodeTransactionPlugin where T : notnull {
    public NodeStore Store { get; set; } = null!; // will be set by the NodeStore
    NodeTypeModel? _nodeTypeModel;
    NodeTypeModel nodeTypeModel {
        get {
            if (_nodeTypeModel == null) {
                var typeId = Store.Mapper.GetNodeTypeId(typeof(T));
                _nodeTypeModel = Store.Datastore.Datamodel.NodeTypes[typeId];
            }
            return _nodeTypeModel;
        }
    }
    public void OnBefore(IdKey id, ActionBase action, Transaction transaction) {
        if (action is NodeAction nodeAction) {
            OnBeforeNodeAction(nodeAction.Node.IdKey, nodeAction.Operation, transaction, new(Store, nodeAction));
        } else if (action is NodePropertyAction nodePropertyAction) {
            OnBeforePropertyAction(id, nodePropertyAction.PropertyIds, nodePropertyAction.Operation, nodePropertyAction.Values, transaction, new(Store, transaction));
        }
    }
    public void OnAfter(IdKey id, ActionBase action, ResultingOperation resultingOperation) {
        if (action is NodeAction nodeAction) {
            OnAfterNodeAction(id, nodeAction.Operation, resultingOperation);
        } else if (action is NodePropertyAction nodePropertyAction) {
            OnAfterPropertyAction(id, nodePropertyAction.PropertyIds, nodePropertyAction.Operation);
        }
    }
    public void OnAfterError(IdKey id, ActionBase action, Exception error) {
        if (action is NodeAction nodeAction) {
            OnErrorNodeAction(id, nodeAction.Operation, error);
        } else if (action is NodePropertyAction nodePropertyAction) {
            OnErrorPropertyAction(id, nodePropertyAction.PropertyIds, nodePropertyAction.Operation, error);
        }
    }

    // building up a cache to look up node types by one call, needed for performance while evaluating which nodes are relevant for a specific plugin
    public void AddIdKeysThatNeedTypeInfo(ActionBase action, ref List<IdKey>? keys) {
        if (action is NodeAction nodeAction) {
            if (nodeAction.Node is INodeData_NoNodeType) {
                keys ??= [];
                keys.Add(nodeAction.Node.IdKey);
            }
        } else if (action is NodePropertyAction nodePropertyAction) {
            keys ??= [];
            if (nodePropertyAction.NodeGuids != null) foreach (var guid in nodePropertyAction.NodeGuids) keys.Add(new(guid));
            else if (nodePropertyAction.NodeIds != null) foreach (var id in nodePropertyAction.NodeIds) keys.Add(new(id));
        }
    }
    public List<IdKey> GetRelevantNodeIds(ActionBase action, Dictionary<IdKey, Guid>? typeInfo) {
        if (action is NodeAction nodeAction) {
            Guid nodeTypeId;
            if (nodeAction.Node is INodeData_NoNodeType) {
                nodeTypeId = typeInfo![nodeAction.Node.IdKey]; // typeInfo should never be null here, as we added the IdKey in AddIdKeysThatNeedTypeInfo
            } else {
                nodeTypeId = nodeAction.Node.NodeType;
            }
            var isRelevant = nodeTypeModel.ThisAndDescendingTypes.ContainsKey(nodeTypeId);
            if (isRelevant) return [nodeAction.Node.IdKey];
        } else if (action is NodePropertyAction nodePropertyAction) {
            List<IdKey> nodeIds = [];
            if (nodePropertyAction.NodeGuids != null) {
                foreach (var guid in nodePropertyAction.NodeGuids) {
                    var nodeTypeId = typeInfo![new(guid)];
                    var isRelevant = nodeTypeModel.ThisAndDescendingTypes.ContainsKey(nodeTypeId);
                    if (isRelevant) nodeIds.Add(new(guid));
                }
            } else if (nodePropertyAction.NodeIds != null) {
                foreach (var id in nodePropertyAction.NodeIds) {
                    var nodeTypeId = typeInfo![new(id)];
                    var isRelevant = nodeTypeModel.ThisAndDescendingTypes.ContainsKey(nodeTypeId);
                    if (isRelevant) nodeIds.Add(new(id));
                }
            }
            return nodeIds;
        }
        return [];
    }

    public virtual void OnBeforeNodeAction(IdKey nodeId, NodeOperation operation, Transaction transaction, NodeHelper<T> helper) { }
    public virtual void OnAfterNodeAction(IdKey nodeId, NodeOperation operation, ResultingOperation resultingOperation) { }
    public virtual void OnErrorNodeAction(IdKey nodeId, NodeOperation operation, Exception error) { }

    public virtual void OnBeforePropertyAction(IdKey nodeId, Guid[] propertyIds, NodePropertyOperation operation, object[]? values, Transaction transaction, PropertyHelper<T> helper) { }
    public virtual void OnAfterPropertyAction(IdKey nodeId, Guid[] propertyIds, NodePropertyOperation operation) { }
    public virtual void OnErrorPropertyAction(IdKey nodeId, Guid[] propertyIds, NodePropertyOperation operation, Exception error) { }

}
public abstract class RelationTransactionPlugin<T> : IRelationTransactionPlugin where T : notnull {
    public NodeStore Store { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public bool IsRelevant(RelationAction action) {
        throw new NotImplementedException();
    }
    public void OnAfter(IdKey id, RelationAction action) {
        throw new NotImplementedException();
    }
    public void OnBefore(IdKey id, RelationAction action, Transaction transaction) {
        throw new NotImplementedException();
    }
    public void OnError(IdKey id, RelationAction action, Exception error) {
        throw new NotImplementedException();
    }


    public virtual void OnBeforeRelationAction(RelationOperation operation, Transaction transaction) { }
    public virtual void OnAfterRelationAction(RelationOperation operation, ResultingOperation resultingOperation) { }
    public virtual void OnErrorRelationAction(RelationOperation operation, Exception error) { }


}
