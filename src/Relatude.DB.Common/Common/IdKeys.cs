using System.Reflection.Metadata;

namespace Relatude.DB.Common;

public readonly struct IdKey {
    public IdKey(Guid guid, int integer) { Guid = guid; Int = integer; }
    public IdKey(Guid guid) => Guid = guid;
    public IdKey(int integer) => Int = integer;
    public Guid Guid { get; }
    public int Int { get; }
    public bool HasGuid => Guid != Guid.Empty;
    public bool HasInt => Int != 0;
    public override string ToString() {
        if (HasGuid && HasInt) {
            return Guid + " (" + Int + ")";
        } else if (HasGuid) {
            return Guid.ToString();
        } else if (HasInt) {
            return Int.ToString();
        } else {
            return string.Empty;
        }
    }
}

public readonly struct InnerProperty(Guid propertyId, Guid innerNodeId) {
    public Guid PropertyId { get; } = propertyId;
    public Guid InnerNodeId { get; } = innerNodeId;
}

/// <summary>
/// Reference to a property on a node or an inner node
/// </summary>
public class NodePath {
    public NodePath(Guid nodeId) {
        NodeKey = new(nodeId); Path = [];
    }
    public NodePath(int nodeId) {
        NodeKey = new(nodeId); Path = [];
    }
    public NodePath(IdKey nodeId) {
        NodeKey = nodeId; Path = [];
    }
    public NodePath(Guid nodeId, InnerProperty[] path) {
        NodeKey = new(nodeId); Path = path;
    }
    public NodePath(int nodeId, InnerProperty[] path) {
        NodeKey = new(nodeId); Path = path;
    }
    public NodePath(IdKey nodeId, InnerProperty[] path) {
        NodeKey = nodeId; Path = path;
    }
    public IdKey NodeKey { get; }
    public InnerProperty[] Path { get; } 
}

/// <summary>
/// Reference to the property on a node or an inner node
/// </summary>
public class PropertyPath {
    public PropertyPath(Guid nodeId, Guid propertyId) {
        NodeId = new(nodeId); Path = []; PropertyId = propertyId;
    }
    public PropertyPath(int nodeId, Guid propertyId) {
        NodeId = new(nodeId); Path = []; PropertyId = propertyId;
    }
    public PropertyPath(IdKey nodeId, Guid propertyId) {
        NodeId = nodeId; Path = []; PropertyId = propertyId;
    }
    public PropertyPath(Guid nodeId, InnerProperty[] path, Guid propertyId) {
        NodeId = new(nodeId); Path = path; PropertyId = propertyId;
    }
    public PropertyPath(int nodeId, InnerProperty[] path, Guid propertyId) {
        NodeId = new(nodeId); Path = path; PropertyId = propertyId;
    }
    public PropertyPath(IdKey nodeId, InnerProperty[] path, Guid propertyId) {
        NodeId = nodeId; Path = path; PropertyId = propertyId;
    }
    public NodePath;
    public IdKey NodeId { get; }
    public InnerProperty[] Path { get; }
    public Guid PropertyId { get; }

}
public readonly struct IdKeyWithCultureId {
    public IdKeyWithCultureId(IdKey idKey, Guid cultureId) { IdKey = idKey; CultureId = cultureId; }
    public IdKey IdKey { get; }
    public Guid CultureId { get; }
}
