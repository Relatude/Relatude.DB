using Relatude.DB.Datamodels;
namespace Relatude.DB.Query.Cache;
/// <summary>
/// This represents a set of dependencies that a query is based on. 
/// </summary>
public class QueryDependencies {
    HashSet<Guid>? _properties;
    HashSet<Guid>? _relations;
    HashSet<Guid>? _nodeTypes;
    public void AddPropertyDependence(Guid propertyId, Datamodel datamodel) {
        if (_properties == null) _properties = new();
        _properties.Add(propertyId);
        var property = datamodel.Properties[propertyId];
        AddNodesTypeDependency(property.NodeType);
    }
    public void AddRelationDependency(Guid relationId) {
        if (_relations == null) _relations = new();
        _relations.Add(relationId);
    }
    public void AddNodesTypeDependency(Guid nodeTypeId) {
        if (_nodeTypes == null) _nodeTypes = new();
        _nodeTypes.Add(nodeTypeId);
    }
    public bool CouldBeAffectedByChanges(TransactionChanges changes, Datamodel datamodel) {
        if (_properties != null && changes.PropertiesChanged != null) {
            foreach (var p in _properties) if (changes.PropertiesChanged.Contains(p)) return true;
        }
        if (_relations != null && changes.RelationsChanged != null) {
            foreach (var r in _relations) if (changes.RelationsChanged.Contains(r)) return true;
        }
        if (_nodeTypes != null && changes.AddedOrRemovedNodeTypes != null) {
            foreach (var nt in _nodeTypes) if (changes.AddedOrRemovedNodeTypes.Contains(nt)) return true;
        }
        return false;
    }
}
// This represents a set of changes that have been made to the data store.
public class TransactionChanges {
    public List<Guid>? PropertiesChanged;
    public List<Guid>? RelationsChanged;
    public List<Guid>? AddedOrRemovedNodeTypes;
    public void ChangedProperty(Guid propertyId) {
        if (PropertiesChanged == null) PropertiesChanged = new();
        PropertiesChanged.Add(propertyId);
    }
    public void ChangedRelation(Guid relationId) {
        if (RelationsChanged == null) RelationsChanged = new();
        RelationsChanged.Add(relationId);
    }
    public void AddedOrRemovedNodeType(Guid nodeTypeId) {
        if (AddedOrRemovedNodeTypes == null) AddedOrRemovedNodeTypes = new();
        AddedOrRemovedNodeTypes.Add(nodeTypeId);
    }
}


