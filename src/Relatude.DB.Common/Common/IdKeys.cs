using System.Runtime.InteropServices;

namespace Relatude.DB.Common;

public readonly struct IdKey : IEquatable<IdKey> {
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
    public static IdKey FromBytes(byte[] bytes) {
        var s = bytes.AsSpan();
        return new IdKey(MemoryMarshal.Read<Guid>(s), MemoryMarshal.Read<int>(s[16..]));
    }
    internal byte[] ToBytes() {
        var bytes = new byte[20];
        var s = bytes.AsSpan();
        MemoryMarshal.Write(s, Guid);
        MemoryMarshal.Write(s[16..], Int);
        return bytes;
    }
    public bool Equals(IdKey other) => Guid == other.Guid && Int == other.Int;
    public override bool Equals(object? obj) => obj is IdKey other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Guid, Int);
    public static bool operator ==(IdKey a, IdKey b) => a.Equals(b);
    public static bool operator !=(IdKey a, IdKey b) => !a.Equals(b);
}

public readonly struct InnerProperty(Guid parentPropertyId, Guid innerNodeId) : IEquatable<InnerProperty> {
    public Guid ParentPropertyId { get; } = parentPropertyId;
    public Guid InnerNodeId { get; } = innerNodeId;
    public static InnerProperty FromBytes(byte[] bytes) {
        var s = bytes.AsSpan();
        return new InnerProperty(MemoryMarshal.Read<Guid>(s), MemoryMarshal.Read<Guid>(s[16..]));
    }
    internal byte[] ToBytes() {
        var bytes = new byte[32];
        var s = bytes.AsSpan();
        MemoryMarshal.Write(s, ParentPropertyId);
        MemoryMarshal.Write(s[16..], InnerNodeId);
        return bytes;
    }
    public bool Equals(InnerProperty other) => ParentPropertyId == other.ParentPropertyId && InnerNodeId == other.InnerNodeId;
    public override bool Equals(object? obj) => obj is InnerProperty other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(ParentPropertyId, InnerNodeId);
    public static bool operator ==(InnerProperty a, InnerProperty b) => a.Equals(b);
    public static bool operator !=(InnerProperty a, InnerProperty b) => !a.Equals(b);
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
    public PropertyPath CreatePropertyPath(Guid propertyId) => new(this, propertyId);
    public IdKey NodeKey { get; }
    public InnerProperty[] Path { get; }
    public static NodePath FromBytes(byte[] bytes) {
        var s = bytes.AsSpan();
        var key = new IdKey(MemoryMarshal.Read<Guid>(s), MemoryMarshal.Read<int>(s[16..]));
        var count = MemoryMarshal.Read<int>(s[20..]);
        var path = new InnerProperty[count];
        for (int i = 0; i < count; i++) { var ps = s[(24 + i * 32)..]; path[i] = new InnerProperty(MemoryMarshal.Read<Guid>(ps), MemoryMarshal.Read<Guid>(ps[16..])); }
        return new NodePath(key, path);
    }
    internal byte[] ToBytes() {
        var bytes = new byte[24 + Path.Length * 32];
        var s = bytes.AsSpan();
        MemoryMarshal.Write(s, NodeKey.Guid);
        MemoryMarshal.Write(s[16..], NodeKey.Int);
        MemoryMarshal.Write(s[20..], Path.Length);
        for (int i = 0; i < Path.Length; i++) { 
            var ps = s[(24 + i * 32)..]; 
            MemoryMarshal.Write(ps, Path[i].ParentPropertyId); 
            MemoryMarshal.Write(ps[16..], Path[i].InnerNodeId); 
        }
        return bytes;
    }
    public override bool Equals(object? obj) =>
        obj is NodePath other &&
        NodeKey == other.NodeKey &&
        Path.AsSpan().SequenceEqual(other.Path.AsSpan());
    public override int GetHashCode() => HashCode.Combine(NodeKey);

}

/// <summary>
/// Reference to the property on a node or an inner node
/// </summary>
public class PropertyPath {
    public PropertyPath(NodePath nodePath, Guid propertyId) {
        NodePath = nodePath; PropertyId = propertyId;
    }
    public PropertyPath(Guid nodeId, Guid propertyId) {
        NodePath = new(nodeId); PropertyId = propertyId;
    }
    public PropertyPath(int nodeId, Guid propertyId) {
        NodePath = new(nodeId); PropertyId = propertyId;
    }
    public PropertyPath(IdKey nodeId, Guid propertyId) {
        NodePath = new(nodeId); PropertyId = propertyId;
    }
    public PropertyPath(Guid nodeId, InnerProperty[] path, Guid propertyId) {
        NodePath = new(nodeId, path); PropertyId = propertyId;
    }
    public PropertyPath(int nodeId, InnerProperty[] path, Guid propertyId) {
        NodePath = new(nodeId, path); PropertyId = propertyId;
    }
    public PropertyPath(IdKey nodeId, InnerProperty[] path, Guid propertyId) {
        NodePath = new(nodeId, path); PropertyId = propertyId;
    }
    public NodePath CreateInnerNodePath(Guid innerNodeId) {
        var newPath = new InnerProperty[NodePath.Path.Length + 1];
        NodePath.Path.AsSpan().CopyTo(newPath);
        newPath[^1] = new InnerProperty(PropertyId, innerNodeId);
        return new NodePath(NodePath.NodeKey, newPath);
    }
    public NodePath NodePath { get; set; }
    public Guid PropertyId { get; }
    

    public static PropertyPath FromBytes(byte[] bytes) {
        var s = bytes.AsSpan();
        var key = new IdKey(MemoryMarshal.Read<Guid>(s), MemoryMarshal.Read<int>(s[16..]));
        var count = MemoryMarshal.Read<int>(s[20..]);
        var path = new InnerProperty[count];
        for (int i = 0; i < count; i++) { var ps = s[(24 + i * 32)..]; path[i] = new InnerProperty(MemoryMarshal.Read<Guid>(ps), MemoryMarshal.Read<Guid>(ps[16..])); }
        return new PropertyPath(key, path, MemoryMarshal.Read<Guid>(s[(24 + count * 32)..]));
    }
    internal byte[] ToBytes() {
        var nodePath = NodePath.ToBytes();
        var bytes = new byte[nodePath.Length + 16];
        nodePath.CopyTo(bytes, 0);
        MemoryMarshal.Write(bytes.AsSpan(nodePath.Length), PropertyId);
        return bytes;
    }
    public override bool Equals(object? obj) =>
        obj is PropertyPath other &&
        PropertyId == other.PropertyId &&
        NodePath.Equals(other.NodePath);
    public override int GetHashCode() => HashCode.Combine(NodePath.NodeKey, PropertyId);
}
public readonly struct IdKeyWithCultureId : IEquatable<IdKeyWithCultureId> {
    public IdKeyWithCultureId(IdKey idKey, Guid cultureId) { IdKey = idKey; CultureId = cultureId; }
    public IdKey IdKey { get; }
    public Guid CultureId { get; }
    public bool Equals(IdKeyWithCultureId other) => IdKey == other.IdKey && CultureId == other.CultureId;
    public override bool Equals(object? obj) => obj is IdKeyWithCultureId other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(IdKey, CultureId);
    public static bool operator ==(IdKeyWithCultureId a, IdKeyWithCultureId b) => a.Equals(b);
    public static bool operator !=(IdKeyWithCultureId a, IdKeyWithCultureId b) => !a.Equals(b);
}
