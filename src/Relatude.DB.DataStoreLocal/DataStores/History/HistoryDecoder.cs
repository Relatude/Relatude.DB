using System.Buffers.Binary;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;

namespace Relatude.DB.DataStores.History;

/// <summary>A cursor over bytes of the log, reading the way <see cref="Serialization.FromBytes"/> does but taking nothing it does not need.</summary>
internal ref struct SpanReader(ReadOnlySpan<byte> span) {
    readonly ReadOnlySpan<byte> _span = span;
    public int Position;
    public readonly int Length => _span.Length;
    void need(int n) {
        if (n < 0 || Position + n > _span.Length) throw new InvalidDataException("Node data ends before it should. ");
    }
    public byte ReadByte() { need(1); return _span[Position++]; }
    public int ReadInt() { need(4); var v = BinaryPrimitives.ReadInt32LittleEndian(_span[Position..]); Position += 4; return v; }
    public uint ReadUInt() { need(4); var v = BinaryPrimitives.ReadUInt32LittleEndian(_span[Position..]); Position += 4; return v; }
    public long ReadLong() { need(8); var v = BinaryPrimitives.ReadInt64LittleEndian(_span[Position..]); Position += 8; return v; }
    public Guid ReadGuid() { need(16); var v = new Guid(_span.Slice(Position, 16)); Position += 16; return v; }
    public ReadOnlySpan<byte> Take(int n) { need(n); var v = _span.Slice(Position, n); Position += n; return v; }
    public void Skip(int n) { need(n); Position += n; }
    /// <summary>A string written by WriteStringOrNull, as its bytes; empty and false when there is none.</summary>
    public bool TakeStringOrNull(out ReadOnlySpan<byte> bytes) {
        bytes = default;
        if (ReadByte() != 1) return false;
        bytes = Take(ReadInt());
        return true;
    }
}

/// <summary>One version of a node as the history reader needs it: its id, meta and hashes of what it holds, no values decoded.</summary>
internal sealed class RevisionHead {
    public Guid RevisionId;
    public IInnerNodeMeta? Meta;
    public ulong AddressHash;
    public ulong ValuesHash;
    /// <summary>Where the version's properties start within the node's bytes, and how many there are: for the details of a page.</summary>
    public int PropertiesAt;
    public int PropertyCount;
    public RevisionType Type => Meta?.RevisionType ?? RevisionType.Published;
    public Guid Culture => Meta?.CultureId ?? Guid.Empty;
    public Guid Collection => Meta?.CollectionId ?? Guid.Empty;
    public bool Deleted => Meta?.Deleted ?? false;
    public Guid ChangedBy => Meta?.ChangedBy ?? Guid.Empty;
}

/// <summary>A node as one action of the log holds it, taken apart only as far as the history needs.</summary>
internal sealed class NodeHead {
    public Guid Id;
    public int IntId;
    public Guid Type;
    public bool Container;
    public readonly List<RevisionHead> Revisions = new(1);
}

/// <summary>
/// The history reader's light decoder: a node's head, the meta and every revision's header are read; values are
/// skipped by their lengths, hashed rather than decoded, so a node of any size costs a pass over its bytes and
/// nothing else. Kept beside <see cref="Serialization.FromBytes.NodeDataAnatomy"/>: a change to the node format
/// has to be made in all three.
/// </summary>
internal static class HistoryDecoder {
    const ulong fnvOffset = 14695981039346656037UL;
    const ulong fnvPrime = 1099511628211UL;
    static ulong fnv(ulong h, ReadOnlySpan<byte> bytes) {
        foreach (var b in bytes) h = (h ^ b) * fnvPrime;
        return h;
    }
    /// <summary>The hash of one property: its id and its bytes.</summary>
    static ulong propertyHash(Guid id, ReadOnlySpan<byte> value) {
        Span<byte> g = stackalloc byte[16];
        id.TryWriteBytes(g);
        return fnv(fnv(fnvOffset, g), value);
    }

    public static NodeHead Decode(ReadOnlySpan<byte> node, HashSet<Guid> ignoredProperties) {
        var r = new SpanReader(node);
        var head = new NodeHead { Id = r.ReadGuid() };
        var legacyId = (int)r.ReadUInt();
        if (legacyId != 0) { // the oldest format: no version, no meta
            head.IntId = legacyId;
            head.Type = r.ReadGuid();
            r.ReadGuid(); r.ReadInt(); r.ReadInt(); r.ReadGuid(); r.ReadGuid(); // collection, lcid twice, read and write access
            r.ReadLong(); r.ReadLong(); // created, changed
            var count = r.ReadInt();
            r.ReadInt();
            head.Revisions.Add(properties(ref r, count, new RevisionHead(), ignoredProperties));
            return head;
        }
        var version = (NodeDataStorageVersions)r.ReadInt();
        head.IntId = (int)r.ReadUInt();
        head.Type = r.ReadGuid();
        switch (version) {
            case NodeDataStorageVersions.Legacy1: {
                    r.ReadLong(); r.ReadLong();
                    var count = r.ReadInt();
                    head.Revisions.Add(properties(ref r, count, new RevisionHead(), ignoredProperties));
                    break;
                }
            case NodeDataStorageVersions.NodeData:
                head.Revisions.Add(body(ref r, new RevisionHead(), ignoredProperties));
                break;
            case NodeDataStorageVersions.RevisionContainer: {
                    head.Container = true;
                    var revisions = r.ReadInt();
                    if (revisions < 0 || revisions > 10000) throw new InvalidDataException("Binary data corruption. ");
                    for (var i = 0; i < revisions; i++) head.Revisions.Add(body(ref r, new RevisionHead { RevisionId = r.ReadGuid() }, ignoredProperties));
                    break;
                }
            default: throw new NotSupportedException("NodeData version " + version + " is not supported. ");
        }
        return head;
    }
    static RevisionHead body(ref SpanReader r, RevisionHead rev, HashSet<Guid> ignored) {
        r.ReadLong(); // created
        r.ReadLong(); // changed: not compared, as an index-only write leaves it and publishing may not
        r.TakeStringOrNull(out _); // the display name nodes once had
        rev.AddressHash = r.TakeStringOrNull(out var address) ? fnv(fnvOffset, address) | 1 : 0;
        var metaBytes = r.Take(r.ReadInt());
        rev.Meta = metaBytes.Length == 0 ? null : IInnerNodeMeta.FromBytes(metaBytes.ToArray());
        return properties(ref r, r.ReadInt(), rev, ignored);
    }
    static RevisionHead properties(ref SpanReader r, int count, RevisionHead rev, HashSet<Guid> ignored) {
        if (count < 0 || count > 10000) throw new InvalidDataException("Binary data corruption. ");
        rev.PropertiesAt = r.Position;
        rev.PropertyCount = count;
        ulong hash = 0, counted = 0;
        for (var i = 0; i < count; i++) {
            var id = r.ReadGuid();
            r.ReadUInt(); // the type it was stored as
            var value = r.Take(r.ReadInt());
            // the engine's own properties (the text index's) change with an index-only write, which is no change
            if (ignored.Contains(id)) continue;
            hash += propertyHash(id, value);
            counted++;
        }
        rev.ValuesHash = hash ^ (counted * fnvPrime);
        return rev;
    }

    /// <summary>
    /// The properties of one revision of a stored node (<see cref="Guid.Empty"/> for a node without revisions, or
    /// the first revision when it has none of that id), each with a hash of its value, and the name (IName.Name) when
    /// it has one: what the details of a page of history are made of.
    /// </summary>
    public static (Dictionary<Guid, ulong> properties, string? name) Properties(ReadOnlySpan<byte> node, Guid revisionId, HashSet<Guid> ignored) {
        var head = Decode(node, ignored);
        var rev = head.Revisions.FirstOrDefault(x => x.RevisionId == revisionId) ?? head.Revisions.FirstOrDefault();
        var result = new Dictionary<Guid, ulong>();
        string? name = null;
        if (rev == null) return (result, name);
        var r = new SpanReader(node) { Position = rev.PropertiesAt };
        for (var i = 0; i < rev.PropertyCount; i++) {
            var id = r.ReadGuid();
            r.ReadUInt();
            var value = r.Take(r.ReadInt());
            if (ignored.Contains(id)) continue;
            result[id] = propertyHash(id, value);
            if (id == NodeConstants.NamePropertyId) name = RelatudeDBGlobals.Encoding.GetString(value);
        }
        return (result, name);
    }
}
