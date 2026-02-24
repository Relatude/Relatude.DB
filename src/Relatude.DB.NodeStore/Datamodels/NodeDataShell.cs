using Relatude.DB.Nodes;

namespace Relatude.DB.Datamodels;
public interface INodeShellAccess {
    public NodeDataShell __NodeDataShell { get; }
}
public class NodeDataShell {
    List<Guid>? changed;
    public INodeDataOuter NodeData;
    bool _copyBeforeUpdate;
    public NodeStore Store;
    Datamodel _dm;
    public NodeDataShell(NodeStore store, INodeDataOuter nodeData, bool copyBeforeUpdate) {
        NodeData = nodeData;
        _dm = store.Datastore.Datamodel;
        Store = store;
        _copyBeforeUpdate = copyBeforeUpdate;
    }
    public T GetValue<T>(Guid propertyId) {
        if (NodeData.TryGetValue(propertyId, out var value) && value is T typedValue) return typedValue;
        var prop = _dm.Properties[propertyId];
        return (T)prop.GetDefaultValue();
    }
    public void SetValue(Guid propertyId, object newValue) {
        if (_copyBeforeUpdate) {
            _copyBeforeUpdate = false;
            NodeData = NodeData.Copy() as INodeDataOuter ?? throw new Exception("Copy did not return INodeDataOuter");
        }
        NodeData.AddOrUpdate(propertyId, newValue);
        changed ??= [];
        if (!changed.Contains(propertyId)) changed.Add(propertyId);
    }
    public bool HasChanged() => changed != null && changed.Count > 0;
    public IEnumerable<Guid> GetChangedProperties() {
        if (changed == null) return [];
        return changed;
    }
}
