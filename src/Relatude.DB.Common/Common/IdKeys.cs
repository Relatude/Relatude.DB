using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Relatude.DB.Common;

public enum KeyType {
    IdKey,
    NodePath,
    PropertyPath,
}
public static class KeyUtil {
    internal const byte TagIdKeyInt = 0x01; // [tag][int32:4]              = 5 bytes
    internal const byte TagIdKeyGuid = 0x02; // [tag][guid:16]              = 17 bytes
    internal const byte TagIdKeyBoth = 0x03; // [tag][guid:16][int32:4]     = 21 bytes
    internal const byte TagNodePath = 0x10; // [tag][idkey][pathLen:1][InnerProperty*32n]
    internal const byte TagPropertyPath = 0x11; // [tag][idkey][pathLen:1][InnerProperty*32n][guid:16]
    public const int MaxPathDepth = 64;
    public static IKeySerializable FromBytes(byte[] bytes) {
        if (bytes is null || bytes.Length == 0) throw new ArgumentException("Bytes cannot be null or empty.", nameof(bytes));
        return bytes[0] switch {
            TagIdKeyInt => bytes.Length >= 5 ? NodeKey.Deserialize(bytes) : throw new FormatException("IdKey (int) too short."),
            TagIdKeyGuid => bytes.Length >= 17 ? NodeKey.Deserialize(bytes) : throw new FormatException("IdKey (guid) too short."),
            TagIdKeyBoth => bytes.Length >= 21 ? NodeKey.Deserialize(bytes) : throw new FormatException("IdKey (both) too short."),
            TagNodePath => NodePath.Deserialize(bytes),
            TagPropertyPath => PropertyPath.Deserialize(bytes),
            _ => throw new FormatException($"Unknown key type tag: 0x{bytes[0]:X2}")
        };
    }

    // Writes an IdKey (tag + data) into dest, returns bytes written (5, 17, or 21)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int WriteIdKey(Span<byte> dest, NodeKey key) {
        if (key.HasGuid && key.HasInt) { dest[0] = TagIdKeyBoth; MemoryMarshal.Write(dest[1..], key.Guid); MemoryMarshal.Write(dest[17..], key.Int); return 21; }
        if (key.HasGuid) { dest[0] = TagIdKeyGuid; MemoryMarshal.Write(dest[1..], key.Guid); return 17; }
        dest[0] = TagIdKeyInt; MemoryMarshal.Write(dest[1..], key.Int); return 5;
    }

    // Reads an IdKey (tag + data) from src, returns bytes consumed (5, 17, or 21)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ReadIdKey(ReadOnlySpan<byte> src, out NodeKey key) {
        if (src.Length < 1) throw new FormatException("IdKey data too short.");
        switch (src[0]) {
            case TagIdKeyBoth: if (src.Length < 21) throw new FormatException("IdKey (both) data too short."); key = new NodeKey(MemoryMarshal.Read<Guid>(src[1..]), MemoryMarshal.Read<int>(src[17..])); return 21;
            case TagIdKeyGuid: if (src.Length < 17) throw new FormatException("IdKey (guid) data too short."); key = new NodeKey(MemoryMarshal.Read<Guid>(src[1..])); return 17;
            default: if (src.Length < 5) throw new FormatException("IdKey (int) data too short."); key = new NodeKey(MemoryMarshal.Read<int>(src[1..])); return 5;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int IdKeySize(NodeKey key) => key.HasGuid && key.HasInt ? 21 : key.HasGuid ? 17 : 5;

    // Reads [pathLen:1][InnerProperty*32n] from the start of s, returns the path entries
    internal static InnerProperty[] ReadPath(ReadOnlySpan<byte> s, string what) {
        if (s.Length < 1) throw new FormatException($"{what} data too short for path length.");
        int count = s[0];
        if (count > MaxPathDepth) throw new FormatException($"{what} path depth {count} exceeds maximum {MaxPathDepth}.");
        if (s.Length < 1 + count * 32) throw new FormatException($"{what} data too short for path entries.");
        return MemoryMarshal.Cast<byte, InnerProperty>(s.Slice(1, count * 32)).ToArray();
    }

    // Writes [pathLen:1][InnerProperty*32n] at the start of dest
    internal static void WritePath(Span<byte> dest, InnerProperty[] path) {
        dest[0] = (byte)path.Length;
        MemoryMarshal.AsBytes(path.AsSpan()).CopyTo(dest[1..]);
    }
}
public interface IKeySerializable {
    byte[] ToBytes();
}
public interface IKeySerializable<T> : IKeySerializable where T : IKeySerializable<T> {
    static abstract T FromBytes(byte[] bytes);
}

public readonly struct NodeKey : IEquatable<NodeKey>, IKeySerializable<NodeKey> {
    public NodeKey(Guid guid, int integer) { Guid = guid; Int = integer; }
    public NodeKey(Guid guid) => Guid = guid;
    public NodeKey(int integer) => Int = integer;
    public Guid Guid { get; }
    public int Int { get; }
    public bool HasGuid => Guid != Guid.Empty;
    public bool HasInt => Int != 0;

    static NodeKey IKeySerializable<NodeKey>.FromBytes(byte[] bytes) => Deserialize(bytes);
    internal static NodeKey Deserialize(byte[] bytes) {
        if (bytes is null || bytes.Length == 0) throw new FormatException("IdKey data too short.");
        KeyUtil.ReadIdKey(bytes, out var k); return k;
    }
    public byte[] ToBytes() { var b = new byte[KeyUtil.IdKeySize(this)]; KeyUtil.WriteIdKey(b, this); return b; }
    public static NodeKey FromBytes(byte[] bytes) => Deserialize(bytes);
    public override string ToString() => B64.EncodeForUrl(ToBytes());
    public string ToUrlString() => B64.EncodeForUrl(ToBytes());
    public static bool TryParse(string s, out NodeKey result) {
        try { if (B64.TryDecodeFromUrlParameter(s, out var b)) { result = Deserialize(b); return true; } } catch (FormatException) { }
        result = default; return false;
    }
    public bool Equals(NodeKey other) => Guid == other.Guid && Int == other.Int;
    public override bool Equals(object? obj) => obj is NodeKey other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Guid, Int);
    public static bool operator ==(NodeKey a, NodeKey b) => a.Equals(b);
    public static bool operator !=(NodeKey a, NodeKey b) => !a.Equals(b);
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct InnerProperty(Guid parentPropertyId, Guid innerNodeId) : IEquatable<InnerProperty> {
    public Guid ParentPropertyId { get; } = parentPropertyId;
    public Guid InnerNodeId { get; } = innerNodeId;
    public static InnerProperty FromBytes(byte[] bytes) => MemoryMarshal.Read<InnerProperty>(bytes);
    internal byte[] ToBytes() {
        var bytes = new byte[32];
        MemoryMarshal.Write(bytes, this);
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
public class NodePath : IKeySerializable<NodePath> {
    public NodePath(Guid nodeId) {
        NodeKey = new(nodeId); Path = [];
    }
    public NodePath(int nodeId) {
        NodeKey = new(nodeId); Path = [];
    }
    public NodePath(NodeKey key) {
        NodeKey = key; Path = [];
    }
    internal NodePath(NodeKey nodeId, InnerProperty[] path) {
        NodeKey = nodeId; Path = path;
    }
    public NodePath(NodeKey nodeId, byte[] subPathBytes) {
        NodeKey = nodeId;
        if (subPathBytes.Length < 2) throw new FormatException("NodePath subpath data too short.");
        Path = KeyUtil.ReadPath(subPathBytes.AsSpan(1), "NodePath subpath");
    }
    public PropertyPath CreatePropertyPath(Guid propertyId) => new(this, propertyId);
    public NodeKey NodeKey { get; }
    public InnerProperty[] Path { get; }
    static NodePath IKeySerializable<NodePath>.FromBytes(byte[] bytes) => Deserialize(bytes);
    public static NodePath FromBytes(byte[] bytes) => Deserialize(bytes);
    public static NodePath FromBytesWithGivenNodeKey(NodeKey key, byte[] bytes) => Deserialize(key, bytes);
    public string ToUrlString() => B64.EncodeForUrl(ToBytes());
    internal static NodePath Deserialize(byte[] bytes) {
        if (bytes.Length < 2) throw new FormatException("NodePath data too short.");
        var s = bytes.AsSpan(1);
        int ks = KeyUtil.ReadIdKey(s, out var key);
        return new NodePath(key, KeyUtil.ReadPath(s[ks..], "NodePath"));
    }
    internal static NodePath Deserialize(NodeKey key, byte[] bytes) {
        // same as Deserialize(byte[]) but with the NodeKey given
        if (bytes.Length < 2) throw new FormatException("NodePath subpath data too short.");
        return new NodePath(key, KeyUtil.ReadPath(bytes.AsSpan(1), "NodePath subpath"));
    }
    public byte[] ToBytes() {
        int ks = KeyUtil.IdKeySize(NodeKey);
        var bytes = new byte[2 + ks + Path.Length * 32];
        var s = bytes.AsSpan();
        s[0] = KeyUtil.TagNodePath;
        KeyUtil.WriteIdKey(s[1..], NodeKey);
        KeyUtil.WritePath(s[(1 + ks)..], Path);
        return bytes;
    }
    public byte[] ToBytesWithoutNodeKey() {
        // same as ToBytes but without the NodeKey, for use in PropertyPath
        var bytes = new byte[2 + Path.Length * 32];
        var s = bytes.AsSpan();
        s[0] = KeyUtil.TagNodePath;
        KeyUtil.WritePath(s[1..], Path);
        return bytes;
    }
    public override string ToString() => B64.EncodeForUrl(ToBytes());
    public static bool TryParse(string s, [MaybeNullWhen(false)] out NodePath result) {
        try { if (B64.TryDecodeFromUrlParameter(s, out var b)) { result = Deserialize(b); return true; } } catch (FormatException) { }
        result = null; return false;
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
public class PropertyPath : IKeySerializable<PropertyPath> {
    public PropertyPath(NodePath nodePath, Guid propertyId) {
        NodePath = nodePath; PropertyId = propertyId;
    }
    public PropertyPath(Guid nodeId, Guid propertyId) {
        NodePath = new(nodeId); PropertyId = propertyId;
    }
    public PropertyPath(int nodeId, Guid propertyId) {
        NodePath = new(nodeId); PropertyId = propertyId;
    }
    public PropertyPath(NodeKey nodeId, Guid propertyId) {
        NodePath = new(nodeId); PropertyId = propertyId;
    }
    internal PropertyPath(NodeKey nodeId, InnerProperty[] path, Guid propertyId) {
        NodePath = new(nodeId, path); PropertyId = propertyId;
    }
    public NodePath CreatePathToInnerNode(Guid innerNodeId) {
        var newPath = new InnerProperty[NodePath.Path.Length + 1];
        NodePath.Path.AsSpan().CopyTo(newPath);
        newPath[^1] = new InnerProperty(PropertyId, innerNodeId);
        return new NodePath(NodePath.NodeKey, newPath);
    }
    public NodePath NodePath { get; }
    public Guid PropertyId { get; }
    public override bool Equals(object? obj) =>
        obj is PropertyPath other &&
        PropertyId == other.PropertyId &&
        NodePath.Equals(other.NodePath);
    public override int GetHashCode() => HashCode.Combine(NodePath.NodeKey, PropertyId);
    static PropertyPath IKeySerializable<PropertyPath>.FromBytes(byte[] bytes) => Deserialize(bytes);
    public static PropertyPath FromBytes(byte[] bytes) => Deserialize(bytes);
    public static PropertyPath FromBytesWithGivenNodeKey(NodeKey key, byte[] bytes) => Deserialize(key, bytes);
    internal static PropertyPath Deserialize(byte[] bytes) {
        if (bytes.Length < 2) throw new FormatException("PropertyPath data too short.");
        var s = bytes.AsSpan(1);
        int ks = KeyUtil.ReadIdKey(s, out var key);
        var path = KeyUtil.ReadPath(s[ks..], "PropertyPath");
        int po = ks + 1 + path.Length * 32;
        if (s.Length < po + 16) throw new FormatException("PropertyPath data too short for PropertyId.");
        return new PropertyPath(key, path, MemoryMarshal.Read<Guid>(s[po..]));
    }
    internal static PropertyPath Deserialize(NodeKey key, byte[] bytes) {
        // same as Deserialize(byte[]) but with the NodeKey given
        if (bytes.Length < 2) throw new FormatException("PropertyPath subpath data too short.");
        var s = bytes.AsSpan(1);
        var path = KeyUtil.ReadPath(s, "PropertyPath subpath");
        int po = 1 + path.Length * 32;
        if (s.Length < po + 16) throw new FormatException("PropertyPath subpath data too short for PropertyId.");
        return new PropertyPath(key, path, MemoryMarshal.Read<Guid>(s[po..]));
    }
    public byte[] ToBytes() {
        int ks = KeyUtil.IdKeySize(NodePath.NodeKey);
        var bytes = new byte[2 + ks + NodePath.Path.Length * 32 + 16];
        var s = bytes.AsSpan();
        s[0] = KeyUtil.TagPropertyPath;
        KeyUtil.WriteIdKey(s[1..], NodePath.NodeKey);
        KeyUtil.WritePath(s[(1 + ks)..], NodePath.Path);
        MemoryMarshal.Write(s[(2 + ks + NodePath.Path.Length * 32)..], PropertyId);
        return bytes;
    }
    public override string ToString() => B64.EncodeForUrl(ToBytes());
    public static bool TryParse(string s, [MaybeNullWhen(false)] out PropertyPath result) {
        try { if (B64.TryDecodeFromUrlParameter(s, out var b)) { result = Deserialize(b); return true; } } catch (FormatException) { }
        result = null; return false;
    }
    public static PropertyPath Parse(string s) => TryParse(s, out var r) ? r! : throw new FormatException($"Invalid PropertyPath: {s}");

    public string ToUrlString() => B64.EncodeForUrl(ToBytes());

    public byte[] ToBytesWithoutNodeKey() {
        // same as ToBytes but without the NodeKey, for use in PropertyPath
        var bytes = new byte[2 + NodePath.Path.Length * 32 + 16];
        var s = bytes.AsSpan();
        s[0] = KeyUtil.TagPropertyPath;
        KeyUtil.WritePath(s[1..], NodePath.Path);
        MemoryMarshal.Write(s[(2 + NodePath.Path.Length * 32)..], PropertyId);
        return bytes;
    }
}

/// <summary>
/// A struct that combines a NodeKey with a culture identifier (Guid) for use in references that are culture-specific.
/// </summary>
public readonly struct NodeKeyWithCulture : IEquatable<NodeKeyWithCulture> {
    public NodeKeyWithCulture(NodeKey idKey, Guid cultureId) { IdKey = idKey; CultureId = cultureId; }
    public NodeKey IdKey { get; }
    public Guid CultureId { get; }
    public bool Equals(NodeKeyWithCulture other) => IdKey == other.IdKey && CultureId == other.CultureId;
    public override bool Equals(object? obj) => obj is NodeKeyWithCulture other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(IdKey, CultureId);
    public static bool operator ==(NodeKeyWithCulture a, NodeKeyWithCulture b) => a.Equals(b);
    public static bool operator !=(NodeKeyWithCulture a, NodeKeyWithCulture b) => !a.Equals(b);
}

public readonly struct NodePathWithCulture : IEquatable<NodePathWithCulture> {
    public NodePathWithCulture(NodePath nodePath, Guid cultureId) { NodePath = nodePath; CultureId = cultureId; }
    public NodePath NodePath { get; }
    public Guid CultureId { get; }
    public bool Equals(NodePathWithCulture other) => NodePath.Equals(other.NodePath) && CultureId == other.CultureId;
    public override bool Equals(object? obj) => obj is NodePathWithCulture other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(NodePath, CultureId);
    public static bool operator ==(NodePathWithCulture a, NodePathWithCulture b) => a.Equals(b);
    public static bool operator !=(NodePathWithCulture a, NodePathWithCulture b) => !a.Equals(b);
}

public readonly struct PropertyPathWithCulture : IEquatable<PropertyPathWithCulture> {
    public PropertyPathWithCulture(PropertyPath propertyPath, Guid cultureId) { PropertyPath = propertyPath; CultureId = cultureId; }
    public PropertyPath PropertyPath { get; }
    public Guid CultureId { get; }
    public bool Equals(PropertyPathWithCulture other) => PropertyPath.Equals(other.PropertyPath) && CultureId == other.CultureId;
    public override bool Equals(object? obj) => obj is PropertyPathWithCulture other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(PropertyPath, CultureId);
    public static bool operator ==(PropertyPathWithCulture a, PropertyPathWithCulture b) => a.Equals(b);
    public static bool operator !=(PropertyPathWithCulture a, PropertyPathWithCulture b) => !a.Equals(b);
}
