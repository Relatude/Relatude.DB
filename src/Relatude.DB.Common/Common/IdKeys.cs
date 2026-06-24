using System.Diagnostics.CodeAnalysis;
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
            TagIdKeyInt  => bytes.Length >= 6  ? IdKey.Deserialize(bytes) : throw new FormatException("IdKey (int) too short."),
            TagIdKeyGuid => bytes.Length >= 18 ? IdKey.Deserialize(bytes) : throw new FormatException("IdKey (guid) too short."),
            TagIdKeyBoth => bytes.Length >= 22 ? IdKey.Deserialize(bytes) : throw new FormatException("IdKey (both) too short."),
            TagNodePath     => NodePath.Deserialize(bytes),
            TagPropertyPath => PropertyPath.Deserialize(bytes),
            _ => throw new FormatException($"Unknown key type tag: 0x{bytes[0]:X2}")
        };
    }

    // Writes an IdKey (tag + data) into dest, returns bytes written (5, 17, or 21)
    internal static int WriteIdKey(Span<byte> dest, IdKey key) {
        if (key.HasGuid && key.HasInt) { dest[0] = TagIdKeyBoth; MemoryMarshal.Write(dest[1..], key.Guid); MemoryMarshal.Write(dest[17..], key.Int); return 21; }
        if (key.HasGuid) { dest[0] = TagIdKeyGuid; MemoryMarshal.Write(dest[1..], key.Guid); return 17; }
        dest[0] = TagIdKeyInt; MemoryMarshal.Write(dest[1..], key.Int); return 5;
    }

    // Reads an IdKey (tag + data) from src, returns bytes consumed (5, 17, or 21)
    internal static int ReadIdKey(ReadOnlySpan<byte> src, out IdKey key) {
        if (src.Length < 1) throw new FormatException("IdKey data too short.");
        switch (src[0]) {
            case TagIdKeyBoth: if (src.Length < 21) throw new FormatException("IdKey (both) data too short."); key = new IdKey(MemoryMarshal.Read<Guid>(src[1..]), MemoryMarshal.Read<int>(src[17..])); return 21;
            case TagIdKeyGuid: if (src.Length < 17) throw new FormatException("IdKey (guid) data too short."); key = new IdKey(MemoryMarshal.Read<Guid>(src[1..])); return 17;
            default:           if (src.Length < 5)  throw new FormatException("IdKey (int) data too short.");  key = new IdKey(MemoryMarshal.Read<int>(src[1..])); return 5;
        }
    }

    internal static int IdKeySize(IdKey key) => key.HasGuid && key.HasInt ? 21 : key.HasGuid ? 17 : 5;
    internal static byte Checksum(ReadOnlySpan<byte> data) { byte c = 0; foreach (var b in data) c += b; return c; }
}
public interface IKeySerializable {
    byte[] ToBytes();
}
public interface IKeySerializable<T> : IKeySerializable where T : IKeySerializable<T> {
    static abstract T FromBytes(byte[] bytes);
}

public readonly struct IdKey : IEquatable<IdKey>, IKeySerializable<IdKey> {
    public IdKey(Guid guid, int integer) { Guid = guid; Int = integer; }
    public IdKey(Guid guid) => Guid = guid;
    public IdKey(int integer) => Int = integer;
    public Guid Guid { get; }
    public int Int { get; }
    public bool HasGuid => Guid != Guid.Empty;
    public bool HasInt => Int != 0;

    static IdKey IKeySerializable<IdKey>.FromBytes(byte[] bytes) => Deserialize(bytes);
    internal static IdKey Deserialize(byte[] bytes) {
        if (bytes.Length < 2) throw new FormatException("IdKey data too short.");
        if (KeyUtil.Checksum(bytes.AsSpan(0, bytes.Length - 1)) != bytes[^1]) throw new FormatException("IdKey checksum mismatch.");
        KeyUtil.ReadIdKey(bytes, out var k); return k;
    }
    public byte[] ToBytes() { var b = new byte[KeyUtil.IdKeySize(this) + 1]; KeyUtil.WriteIdKey(b, this); b[^1] = KeyUtil.Checksum(b.AsSpan(0, b.Length - 1)); return b; }
    public static IdKey FromBytes(byte[] bytes) => Deserialize(bytes);
    public override string ToString() => B64.EncodeForUrl(ToBytes());
    public static bool TryParse(string s, out IdKey result) {
        try { if (B64.TryDecodeFromUrlParameter(s, out var b)) { result = Deserialize(b); return true; } }
        catch (FormatException) { }
        result = default; return false;
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
public class NodePath : IKeySerializable<NodePath> {
    public NodePath(Guid nodeId) {
        NodeKey = new(nodeId); Path = [];
    }
    public NodePath(int nodeId) {
        NodeKey = new(nodeId); Path = [];
    }
    public NodePath(IdKey key) {
        NodeKey = key; Path = [];
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
    static NodePath IKeySerializable<NodePath>.FromBytes(byte[] bytes) => Deserialize(bytes);
    public static NodePath FromBytes(byte[] bytes) => Deserialize(bytes);
    internal static NodePath Deserialize(byte[] bytes) {
        if (bytes.Length < 4) throw new FormatException("NodePath data too short.");
        if (KeyUtil.Checksum(bytes.AsSpan(0, bytes.Length - 1)) != bytes[^1]) throw new FormatException("NodePath checksum mismatch.");
        var s = bytes.AsSpan(1);
        int ks = KeyUtil.ReadIdKey(s, out var key);
        if (s.Length < ks + 2) throw new FormatException("NodePath data too short for path length.");
        var count = s[ks];
        if (count > KeyUtil.MaxPathDepth) throw new FormatException($"NodePath path depth {count} exceeds maximum {KeyUtil.MaxPathDepth}.");
        if (s.Length < ks + 1 + count * 32 + 1) throw new FormatException("NodePath data too short for path entries.");
        var path = new InnerProperty[count];
        for (int i = 0; i < count; i++) { var ps = s[(ks + 1 + i * 32)..]; path[i] = new InnerProperty(MemoryMarshal.Read<Guid>(ps), MemoryMarshal.Read<Guid>(ps[16..])); }
        return new NodePath(key, path);
    }
    public byte[] ToBytes() {
        int ks = KeyUtil.IdKeySize(NodeKey);
        var bytes = new byte[2 + ks + Path.Length * 32 + 1];
        var s = bytes.AsSpan();
        s[0] = KeyUtil.TagNodePath;
        KeyUtil.WriteIdKey(s[1..], NodeKey);
        s[1 + ks] = (byte)Path.Length;
        for (int i = 0; i < Path.Length; i++) { var ps = s[(2 + ks + i * 32)..]; MemoryMarshal.Write(ps, Path[i].ParentPropertyId); MemoryMarshal.Write(ps[16..], Path[i].InnerNodeId); }
        bytes[^1] = KeyUtil.Checksum(s[..^1]);
        return bytes;
    }
    public override string ToString() => B64.EncodeForUrl(ToBytes());
    public static bool TryParse(string s, [MaybeNullWhen(false)] out NodePath result) {
        try { if (B64.TryDecodeFromUrlParameter(s, out var b)) { result = Deserialize(b); return true; } }
        catch (FormatException) { }
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
    internal static PropertyPath Deserialize(byte[] bytes) {
        if (bytes.Length < 4) throw new FormatException("PropertyPath data too short.");
        if (KeyUtil.Checksum(bytes.AsSpan(0, bytes.Length - 1)) != bytes[^1]) throw new FormatException("PropertyPath checksum mismatch.");
        var s = bytes.AsSpan(1);
        int ks = KeyUtil.ReadIdKey(s, out var key);
        if (s.Length < ks + 2) throw new FormatException("PropertyPath data too short for path length.");
        var count = s[ks];
        if (count > KeyUtil.MaxPathDepth) throw new FormatException($"PropertyPath path depth {count} exceeds maximum {KeyUtil.MaxPathDepth}.");
        if (s.Length < ks + 1 + count * 32 + 17) throw new FormatException("PropertyPath data too short for path entries or PropertyId.");
        var path = new InnerProperty[count];
        for (int i = 0; i < count; i++) {
            var ps = s[(ks + 1 + i * 32)..];
            path[i] = new InnerProperty(MemoryMarshal.Read<Guid>(ps), MemoryMarshal.Read<Guid>(ps[16..]));
        }
        return new PropertyPath(key, path, MemoryMarshal.Read<Guid>(s[(ks + 1 + count * 32)..]));
    }
    public byte[] ToBytes() {
        int ks = KeyUtil.IdKeySize(NodePath.NodeKey);
        var bytes = new byte[2 + ks + NodePath.Path.Length * 32 + 17];
        var s = bytes.AsSpan();
        s[0] = KeyUtil.TagPropertyPath;
        KeyUtil.WriteIdKey(s[1..], NodePath.NodeKey);
        s[1 + ks] = (byte)NodePath.Path.Length;
        for (int i = 0; i < NodePath.Path.Length; i++) {
            var ps = s[(2 + ks + i * 32)..];
            MemoryMarshal.Write(ps, NodePath.Path[i].ParentPropertyId); MemoryMarshal.Write(ps[16..], NodePath.Path[i].InnerNodeId);
        }
        MemoryMarshal.Write(s[(2 + ks + NodePath.Path.Length * 32)..], PropertyId);
        bytes[^1] = KeyUtil.Checksum(s[..^1]);
        return bytes;
    }
    public override string ToString() => B64.EncodeForUrl(ToBytes());
    public static bool TryParse(string s, [MaybeNullWhen(false)] out PropertyPath result) {
        try { if (B64.TryDecodeFromUrlParameter(s, out var b)) { result = Deserialize(b); return true; } }
        catch (FormatException) { }
        result = null; return false;
    }
    public static PropertyPath Parse(string s) => TryParse(s, out var r) ? r! : throw new FormatException($"Invalid PropertyPath: {s}");
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
