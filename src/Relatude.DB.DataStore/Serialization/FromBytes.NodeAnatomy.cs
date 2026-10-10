using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
namespace Relatude.DB.Serialization;

/// <summary>What one stretch of a stored node's bytes holds.</summary>
public enum NodeAnatomyPartKind {
    /// <summary>The fixed fields: ids, type, format version, dates, value counts and revision ids.</summary>
    Header,
    DisplayName,
    Address,
    Meta,
    /// <summary>One property value, with the id, type and length that are stored in front of it.</summary>
    Property,
}
/// <summary>
/// The bytes of a stored node under one heading. A property is one part however many revisions
/// carry it: <see cref="Occurrences"/> counts them and the bytes are their sum.
/// </summary>
public sealed class NodeAnatomyPart {
    public NodeAnatomyPartKind Kind { get; init; }
    public Guid PropertyId { get; init; }
    /// <summary>The type the value was stored as, which is not necessarily the model's type today.</summary>
    public PropertyType PropertyType { get; init; }
    /// <summary>Everything the part takes in the node, framing included.</summary>
    public long Bytes { get; set; }
    /// <summary>The value alone, without the property id, type and length stored in front of it.</summary>
    public long ValueBytes { get; set; }
    public int Occurrences { get; set; }
}
/// <summary>A stored node taken apart into the stretches of bytes it is made of.</summary>
public sealed class NodeAnatomy {
    public Guid NodeId { get; init; }
    public int Id { get; init; }
    public Guid NodeTypeId { get; init; }
    public NodeDataStorageVersions Version { get; init; }
    /// <summary>The number of revisions in a revision container, 0 for a node stored without them.</summary>
    public int Revisions { get; init; }
    public int TotalBytes { get; init; }
    public List<NodeAnatomyPart> Parts { get; } = [];
}

public static partial class FromBytes {
    /// <summary>
    /// Walks the bytes of a stored node, the way <see cref="NodeData"/> reads them, and says how many
    /// of them each part takes - the header, the display name, the address, the meta and every
    /// property value. No value is decoded, so a node of any size is measured without being built,
    /// and a property that is no longer in the model is measured all the same: it is still stored.
    /// Kept beside the reader on purpose: a change to the format has to be made in both.
    /// </summary>
    public static NodeAnatomy NodeDataAnatomy(byte[] bytes) {
        var stream = new MemoryStream(bytes, false);
        var guid = stream.ReadGuid();
        var legacyId = (int)stream.ReadUInt();
        var parts = new Dictionary<(NodeAnatomyPartKind, Guid), NodeAnatomyPart>();
        long header = 0;
        void add(NodeAnatomyPartKind kind, long bytes, long valueBytes = 0, Guid propertyId = default, PropertyType propertyType = default) {
            if (kind == NodeAnatomyPartKind.Header) {
                header += bytes;
                return;
            }
            if (!parts.TryGetValue((kind, propertyId), out var part)) {
                part = new NodeAnatomyPart { Kind = kind, PropertyId = propertyId, PropertyType = propertyType };
                parts.Add((kind, propertyId), part);
            }
            part.Bytes += bytes;
            part.ValueBytes += valueBytes;
            part.Occurrences++;
        }
        // a stretch read from the current position: what it took is the distance moved
        long mark = 0;
        long since() {
            var moved = stream.Position - mark;
            mark = stream.Position;
            return moved;
        }
        void properties(int count) {
            if (count < 0 || count > 10000) throw new Exception("Binary data corruption. ");
            for (var i = 0; i < count; i++) {
                var propertyId = stream.ReadGuid();
                var propertyType = (PropertyType)stream.ReadUInt();
                var length = skipByteArray(stream);
                add(NodeAnatomyPartKind.Property, since(), length, propertyId, propertyType);
            }
        }
        // what follows the header in a node stored now, and in each revision of a container
        void body() {
            stream.ReadDateTime(); // created
            stream.ReadDateTime(); // changed
            add(NodeAnatomyPartKind.Header, since());
            skipStringOrNull(stream, out var nameLength);
            add(NodeAnatomyPartKind.DisplayName, since(), nameLength);
            skipStringOrNull(stream, out var addressLength);
            add(NodeAnatomyPartKind.Address, since(), addressLength);
            var metaLength = skipByteArray(stream);
            add(NodeAnatomyPartKind.Meta, since(), metaLength);
            var count = stream.ReadInt();
            add(NodeAnatomyPartKind.Header, since());
            properties(count);
        }
        int id;
        Guid nodeTypeId;
        NodeDataStorageVersions version;
        var revisions = 0;
        if (legacyId != 0) { // the oldest format has no version field: the id is where it would be
            id = legacyId;
            version = NodeDataStorageVersions.Legacy0;
            nodeTypeId = stream.ReadGuid();
            stream.ReadGuid(); // collection
            stream.ReadInt(); // lcid
            stream.ReadInt(); // derived from lcid
            stream.ReadGuid(); // read access
            stream.ReadGuid(); // write access
            stream.ReadDateTime();
            stream.ReadDateTime();
            var count = stream.ReadInt();
            stream.ReadInt(); // the count again, as a check
            add(NodeAnatomyPartKind.Header, since());
            properties(count);
        } else {
            version = (NodeDataStorageVersions)stream.ReadInt();
            id = (int)stream.ReadUInt();
            nodeTypeId = stream.ReadGuid();
            add(NodeAnatomyPartKind.Header, since());
            switch (version) {
                case NodeDataStorageVersions.Legacy1:
                    stream.ReadDateTime();
                    stream.ReadDateTime();
                    var count = stream.ReadInt();
                    add(NodeAnatomyPartKind.Header, since());
                    properties(count);
                    break;
                case NodeDataStorageVersions.NodeData:
                    body();
                    break;
                case NodeDataStorageVersions.RevisionContainer:
                    revisions = stream.ReadInt();
                    if (revisions < 0 || revisions > 10000) throw new Exception("Binary data corruption. ");
                    add(NodeAnatomyPartKind.Header, since());
                    for (var r = 0; r < revisions; r++) {
                        stream.ReadGuid(); // the revision's own id
                        add(NodeAnatomyPartKind.Header, since());
                        body();
                    }
                    break;
                default: throw new NotSupportedException("NodeData version " + version + " is not supported. ");
            }
        }
        var anatomy = new NodeAnatomy {
            NodeId = guid,
            Id = id,
            NodeTypeId = nodeTypeId,
            Version = version,
            Revisions = revisions,
            TotalBytes = bytes.Length,
        };
        anatomy.Parts.Add(new NodeAnatomyPart { Kind = NodeAnatomyPartKind.Header, Bytes = header, ValueBytes = header, Occurrences = 1 });
        anatomy.Parts.AddRange(parts.Values);
        return anatomy;
    }
    // the length is read and the bytes stepped over, so a value of any size costs nothing to measure
    static int skipByteArray(Stream stream) {
        var length = stream.ReadInt();
        if (length < 0 || stream.Position + length > stream.Length) throw new Exception("Binary data corruption. ");
        stream.Seek(length, SeekOrigin.Current);
        return length;
    }
    static void skipStringOrNull(Stream stream, out int length) {
        length = 0;
        if (stream.ReadBool()) length = skipByteArray(stream);
    }
}
