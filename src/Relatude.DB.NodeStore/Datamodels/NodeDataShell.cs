using Relatude.DB.Common;
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
    PropertyPath? _propertyPath; // set when this node is an inner node of an embedded structure, null when it is a root node
    public NodeDataShell(NodeStore store, INodeDataExternal nodeData, bool copyBeforeUpdate, PropertyPath? propertyPath = null) {
        NodeData = nodeData;
        _dm = store.Datastore.Datamodel;
        Store = store;
        _copyBeforeUpdate = copyBeforeUpdate;
        _propertyPath = propertyPath;
    }
    /// <summary>
    /// The path addressing one of this node's properties, so that values like FileValue can carry their own
    /// address. Mirrors what the generated mapper does for class node types.
    /// </summary>
    public PropertyPath GetPropertyPath(Guid propertyId) {
        var nodePath = _propertyPath == null ? new NodePath(NodeData.Id) : _propertyPath.CreatePathToInnerNode(NodeData.Id);
        return nodePath.CreatePropertyPath(propertyId);
    }
    public T? GetValue<T>(Guid propertyId) {
        if (NodeData.TryGetValue(propertyId, out var value)) {
            if (value is T typedValue) {
                // HTML/Markdown values are stored with internal rdb: link tokens; reads emit current public URLs
                if (typedValue is string s && s.Length > 0
                    && _dm.Properties[propertyId] is Properties.StringPropertyModel sp
                    && (sp.StringType == Properties.StringValueType.HTML || sp.StringType == Properties.StringValueType.Markdown)) {
                    return (T)(object)Store.ExternalizeContentLinks(s)!;
                }
                return typedValue;
            }
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
