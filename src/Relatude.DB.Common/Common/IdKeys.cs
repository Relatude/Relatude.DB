using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Relatude.DB.Common;

file static class B64 {
    internal static string Encode(Guid g) {
        Span<byte> b = stackalloc byte[16];
        g.TryWriteBytes(b);
        return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
    internal static bool TryDecode(ReadOnlySpan<char> s, out Guid g) {
        if (s.Length != 22) { g = default; return false; }
        Span<char> buf = stackalloc char[24];
        for (int i = 0; i < 22; i++) buf[i] = s[i] == '-' ? '+' : s[i] == '_' ? '/' : s[i];
        buf[22] = buf[23] = '=';
        Span<byte> bytes = stackalloc byte[16];
        if (!Convert.TryFromBase64Chars(buf, bytes, out _)) { g = default; return false; }
        g = new Guid(bytes); return true;
    }
}

public readonly struct IdKey : IEquatable<IdKey> {
    public IdKey(Guid guid, int integer) { Guid = guid; Int = integer; }
    public IdKey(Guid guid) => Guid = guid;
    public IdKey(int integer) => Int = integer;
    public Guid Guid { get; }
    public int Int { get; }
    public bool HasGuid => Guid != Guid.Empty;
    public bool HasInt => Int != 0;

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
    public override string ToString() =>
        HasGuid && HasInt ? $"{B64.Encode(Guid)}.{Int}" :
        HasGuid ? B64.Encode(Guid) :
        HasInt ? Int.ToString() : "0";

    public static bool TryParse(string value, [MaybeNullWhen(false)] out IdKey result) {
        if (value.Length == 22 && B64.TryDecode(value, out var g)) { result = new IdKey(g); return true; }
        if (value.Length > 23 && value[22] == '.' && B64.TryDecode(value.AsSpan(0, 22), out g) && int.TryParse(value.AsSpan(23), out var n)) { result = new IdKey(g, n); return true; }
        if (int.TryParse(value, out n)) { result = new IdKey(n); return true; }
        result = default; return false;
    }
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
    public override string ToString() => $"{B64.Encode(ParentPropertyId)}.{B64.Encode(InnerNodeId)}";

    public static bool TryParse(string value, [MaybeNullWhen(false)] out InnerProperty result) {
        if (value.Length == 45 && value[22] == '.' && B64.TryDecode(value.AsSpan(0, 22), out var p) && B64.TryDecode(value.AsSpan(23), out var n)) { result = new InnerProperty(p, n); return true; }
        result = default; return false;
    }
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
    public override string ToString() =>
        Path.Length == 0 ? NodeKey.ToString() : $"{NodeKey}:{string.Join(',', Path.Select(p => p.ToString()))}";

    public static bool TryParse(string value, [MaybeNullWhen(false)] out NodePath result) {
        var ci = value.IndexOf(':');
        if (!IdKey.TryParse(ci < 0 ? value : value[..ci], out var key)) { result = null; return false; }
        if (ci < 0) { result = new NodePath(key); return true; }
        var parts = value[(ci + 1)..].Split(',');
        var path = new InnerProperty[parts.Length];
        for (int i = 0; i < parts.Length; i++) if (!InnerProperty.TryParse(parts[i], out path[i])) { result = null; return false; }
        result = new NodePath(key, path); return true;
    }
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
    public override string ToString() => $"{NodePath}~{B64.Encode(PropertyId)}";

    public static bool TryParse(string value, [MaybeNullWhen(false)] out PropertyPath result) {
        var ti = value.LastIndexOf('~');
        if (ti < 0 || !NodePath.TryParse(value[..ti], out var np) || !B64.TryDecode(value.AsSpan(ti + 1), out var pid)) { result = null; return false; }
        result = new PropertyPath(np, pid); return true;
    }


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

    public static PropertyPath Parse(string path) {
        if (!TryParse(path, out var result)) throw new FormatException("Invalid property path format");
        return result;
    }
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
