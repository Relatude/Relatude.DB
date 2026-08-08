using Relatude.DB.Nodes;

namespace Relatude.DB.Datamodels;

public interface INodeShellAccess {
    [System.Text.Json.Serialization.JsonIgnore]
    public NodeDataShell __NodeDataShell { get; }
}
public class NodeDataShell {
    List<Guid>? changed;
    public INodeDataExternal NodeData;
    bool _copyBeforeUpdate;
    public NodeStore Store;
    Datamodel _dm;
    public NodeDataShell(NodeStore store, INodeDataExternal nodeData, bool copyBeforeUpdate) {
        NodeData = nodeData;
        _dm = store.Datastore.Datamodel;
        Store = store;
        _copyBeforeUpdate = copyBeforeUpdate;
    }
    public T? GetValue<T>(Guid propertyId) {
        if (NodeData.TryGetValue(propertyId, out var value)) {
            if (value is T typedValue) return typedValue;
            // enums are stored as boxed int, and "is T" is false for boxed int when T is an enum
            if (typeof(T).IsEnum && value is int i) return (T)(object)i;
        }
        var prop = _dm.Properties[propertyId];
        return (T?)prop.GetDefaultValue();
    }
    public void SetValue(Guid propertyId, object newValue) {
        if (newValue is Enum e) newValue = Convert.ToInt32(e); // stored as int, like IntegerPropertyModel.ForceValueType
        if (_copyBeforeUpdate) {
            _copyBeforeUpdate = false;
            NodeData = NodeData.CopyExternal();
        }
        NodeData.AddOrUpdate(propertyId, newValue);
        changed ??= [];
        if (!changed.Contains(propertyId)) changed.Add(propertyId);
    }
    // TODO - ChangeTracking etc..
    //public bool HasChanged() => changed != null && changed.Count > 0;
    //public IEnumerable<Guid> GetChangedProperties() {
    //    if (changed == null) return [];
    //    return changed;
    //}
}
