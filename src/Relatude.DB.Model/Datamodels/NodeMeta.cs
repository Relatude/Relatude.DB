using Microsoft.CodeAnalysis;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.Datamodels;

public enum InnerNodeMetaType : byte {
    Empty = 0,
    Min = 1,
    Full = 2,
}
public struct NodeAndMeta<T> {
    public NodeAndMeta(T node, INodeDataOuter nodeData) {
        Node = node;
        Meta = new(nodeData);
    }
    public T Node { get; }
    public NodeMeta Meta { get; }
}
public class NodeMeta {
    public readonly IInnerNodeMeta InnerMeta;
    NodeMeta() {
        InnerMeta = IInnerNodeMeta.Empty;
        DisplayName = string.Empty;
    }
    public static NodeMeta Empty { get; } = new NodeMeta();
    public NodeMeta(INodeDataOuter node) {
        InnerMeta = node.Meta ?? IInnerNodeMeta.Empty;
        RevisionId = node is NodeDataRevision rev ? rev.RevisionId : Guid.Empty;
        CreatedUtc = node.CreatedUtc;
        ChangedUtc = node.ChangedUtc;
        NodeTypeId = node.NodeType;
        DisplayName = node.ToString()!;
        Id = node.Id;
        InternalId = node.__Id;
    }

    public Guid NodeTypeId { get; }
    public DateTime CreatedUtc { get; }
    public DateTime ChangedUtc { get; }
    public int InternalId { get; }
    public Guid Id { get; }
    public string DisplayName { get; }
    public Guid RevisionId { get; }

    public RevisionType RevisionType => InnerMeta.RevisionType;

    // not exposing revision key as it is an internal implementation detail,
    // it can be the same for different cultures
    // public int RevisionKey => _meta.RevisionKey; 

    public Guid CollectionId => InnerMeta.CollectionId;
    public Guid ReadAccess => InnerMeta.ReadAccess;
    public Guid EditAccess => InnerMeta.EditAccess;
    public Guid EditViewAccess => InnerMeta.EditViewAccess;
    public Guid PublishAccess => InnerMeta.PublishAccess;
    public bool Deleted => InnerMeta.Deleted;
    public bool Hidden => InnerMeta.Hidden;
    public Guid CreatedBy => InnerMeta.CreatedBy;
    public Guid ChangedBy => InnerMeta.ChangedBy;
    public Guid CultureId => InnerMeta.CultureId;
    public DateTime? ReleaseUtc => InnerMeta.ReleaseUtc;
    public DateTime? ExpireUtc => InnerMeta.ExpireUtc;

    public int GetHashCode([DisallowNull] IInnerNodeMeta obj) {
        throw new NotImplementedException();
    }
    public override bool Equals(object? obj) {
        throw new NotImplementedException();
    }
    public override int GetHashCode() {
        return base.GetHashCode();
    }
}
public interface IInnerNodeMeta : IEquatable<IInnerNodeMeta> { // Without revision ID, so it will be similar for different nodes and node revisions
    int RevisionKey { get; }
    RevisionType RevisionType { get; }
    Guid CollectionId { get; } // common for all cultures
    Guid ReadAccess { get; }  // common for all cultures
    Guid EditAccess { get; }  // common for all cultures
    Guid EditViewAccess { get; }  // common for all cultures
    Guid PublishAccess { get; }  // common for all cultures
    bool Deleted { get; } // common for all cultures
    bool Hidden { get; } // common for all cultures
    Guid CreatedBy { get; }
    Guid ChangedBy { get; }
    Guid CultureId { get; }
    DateTime? ReleaseUtc { get; }
    DateTime? ExpireUtc { get; }

    public static IInnerNodeMeta? ChangeCulture(IInnerNodeMeta? meta, Guid cultureId) {
        if (meta == null) {
            if (cultureId == Guid.Empty) return null; // null and empty are treated as the same, return null to save space
            return new InnerNodeMetaFull(
                revisionKey: 0,
                collectionId: Guid.Empty,
                readAccess: Guid.Empty,
                editAccess: Guid.Empty,
                editViewAccess: Guid.Empty,
                publishAccess: Guid.Empty,
                deleted: false,
                hidden: false,
                createdBy: Guid.Empty,
                changedBy: Guid.Empty,
                cultureId: cultureId, // change culture
                releaseUtc: null,
                expireUtc: null
            );
        }
        if (meta.CultureId == cultureId) return meta; // no change needed
        return MinimizeIfPossible(new InnerNodeMetaFull(
            revisionKey: meta.RevisionKey,
            collectionId: meta.CollectionId,
            readAccess: meta.ReadAccess,
            editAccess: meta.EditAccess,
            editViewAccess: meta.EditViewAccess,
            publishAccess: meta.PublishAccess,
            deleted: meta.Deleted,
            hidden: meta.Hidden,
            createdBy: meta.CreatedBy,
            changedBy: meta.ChangedBy,
            cultureId: cultureId, // change culture
            releaseUtc: meta.ReleaseUtc,
            expireUtc: meta.ExpireUtc
        ));
    }
    public static IInnerNodeMeta? ChangeRevision(IInnerNodeMeta? meta, int revisionKey) {
        if (meta == null) {
            if (revisionKey == 0) return null; // null and empty are treated as the same, return null to save space
            return new InnerNodeMetaFull(
                revisionKey: revisionKey,
                collectionId: Guid.Empty,
                readAccess: Guid.Empty,
                editAccess: Guid.Empty,
                editViewAccess: Guid.Empty,
                publishAccess: Guid.Empty,
                deleted: false,
                hidden: false,
                createdBy: Guid.Empty,
                changedBy: Guid.Empty,
                cultureId: Guid.Empty,
                releaseUtc: null,
                expireUtc: null
            );
        }
        if (meta.RevisionKey == revisionKey) return meta; // no change needed
        return MinimizeIfPossible(new InnerNodeMetaFull(
            revisionKey: revisionKey, // change revision
            collectionId: meta.CollectionId,
            readAccess: meta.ReadAccess,
            editAccess: meta.EditAccess,
            editViewAccess: meta.EditViewAccess,
            publishAccess: meta.PublishAccess,
            deleted: meta.Deleted,
            hidden: meta.Hidden,
            createdBy: meta.CreatedBy,
            changedBy: meta.ChangedBy,
            cultureId: meta.CultureId,
            releaseUtc: meta.ReleaseUtc,
            expireUtc: meta.ExpireUtc
        ));
    }

    public static IInnerNodeMeta? DeriveCombinedMeta(IInnerNodeMeta? original, IInnerNodeMeta? newRev) {
        if (original == null && newRev == null) return null;
        return MinimizeIfPossible(new InnerNodeMetaFull(
            // take common props from newRev
            collectionId: newRev?.CollectionId ?? Empty.CollectionId,
            readAccess: newRev?.ReadAccess ?? Empty.ReadAccess,
            editAccess: newRev?.EditAccess ?? Empty.EditAccess,
            editViewAccess: newRev?.EditViewAccess ?? Empty.EditViewAccess,
            publishAccess: newRev?.PublishAccess ?? Empty.PublishAccess,
            deleted: newRev?.Deleted ?? Empty.Deleted,
            hidden: newRev?.Hidden ?? Empty.Hidden,

            // keep revision specific properties             
            revisionKey: original?.RevisionKey ?? Empty.RevisionKey,
            createdBy: original?.CreatedBy ?? Empty.CreatedBy,
            changedBy: original?.ChangedBy ?? Empty.ChangedBy,
            cultureId: original?.CultureId ?? Empty.CultureId,
            releaseUtc: original?.ReleaseUtc ?? Empty.ReleaseUtc,
            expireUtc: original?.ExpireUtc ?? Empty.ExpireUtc
        ));
    }
    static bool CanBeEmptyOrNull(IInnerNodeMeta meta) {
        return meta.RevisionKey == 0
            && meta.CollectionId == Guid.Empty
            && meta.ReadAccess == Guid.Empty
            && meta.EditAccess == Guid.Empty
            && meta.EditViewAccess == Guid.Empty
            && meta.PublishAccess == Guid.Empty
            && !meta.Deleted
            && !meta.Hidden
            && meta.CreatedBy == Guid.Empty
            && meta.ChangedBy == Guid.Empty
            && meta.CultureId == Guid.Empty
            && meta.ReleaseUtc == null
            && meta.ExpireUtc == null;
    }
    static bool CanBeMin(IInnerNodeMeta meta) {
        return meta.RevisionKey == 0
            && meta.EditViewAccess == meta.EditAccess
            && meta.PublishAccess == meta.EditAccess
            && !meta.Deleted
            && !meta.Hidden
            && meta.CreatedBy == Guid.Empty
            && meta.ChangedBy == Guid.Empty
            && meta.CultureId == Guid.Empty
            && meta.ReleaseUtc == null
            && meta.ExpireUtc == null;
    }

    public static readonly IInnerNodeMeta Empty = new InnerNodeMetaEmpty();
    public static byte[] ToBytes(IInnerNodeMeta? meta) {
        if (meta == null || meta is InnerNodeMetaEmpty) {
            return [(byte)InnerNodeMetaType.Empty];
        } else if (meta is InnerNodeMetaMin) {
            // optimized for performance using span<byte> and stackalloc:
            byte[] data = new byte[1 + 16 * 3]; // 1 byte for type + 3 GUIDs
            data[0] = (byte)InnerNodeMetaType.Min;
            // write GUIDs directly to byte array using span and stackalloc
            Span<byte> span = data.AsSpan(1); // skip the first byte for type
            meta.CollectionId.TryWriteBytes(span.Slice(0, 16));
            meta.ReadAccess.TryWriteBytes(span.Slice(16, 16));
            meta.EditAccess.TryWriteBytes(span.Slice(32, 16));
            return data;
        } else if (meta is InnerNodeMetaFull) {
            // optimized for performance using span<byte> and stackalloc:
            byte[] data = new byte[1 + 4 + 16 * 6 + 1 + 1 + 16 * 3 + 8 * 2]; // type + int + 6 GUIDs + 2 bools + 3 GUIDs + 2 DateTimes
            data[0] = (byte)InnerNodeMetaType.Full;
            Span<byte> span = data.AsSpan(1); // skip the first byte for type
            BitConverter.TryWriteBytes(span.Slice(0, 4), meta.RevisionKey);
            meta.CollectionId.TryWriteBytes(span.Slice(4, 16));
            meta.ReadAccess.TryWriteBytes(span.Slice(20, 16));
            meta.EditAccess.TryWriteBytes(span.Slice(36, 16));
            meta.EditViewAccess.TryWriteBytes(span.Slice(52, 16));
            meta.PublishAccess.TryWriteBytes(span.Slice(68, 16));
            span[84] = (byte)(meta.Deleted ? 1 : 0);
            span[85] = (byte)(meta.Hidden ? 1 : 0);
            meta.CreatedBy.TryWriteBytes(span.Slice(86, 16));
            meta.ChangedBy.TryWriteBytes(span.Slice(102, 16));
            meta.CultureId.TryWriteBytes(span.Slice(118, 16));
            BitConverter.TryWriteBytes(span.Slice(134, 8), meta.ReleaseUtc?.ToBinary() ?? 0);
            BitConverter.TryWriteBytes(span.Slice(142, 8), meta.ExpireUtc?.ToBinary() ?? 0);

            // DateTimes (forcing little endian)
            long releaseBinary = meta.ReleaseUtc?.ToBinary() ?? 0;
            long expireBinary = meta.ExpireUtc?.ToBinary() ?? 0;
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(134, 8), releaseBinary);
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(142, 8), expireBinary);

            return data;
        } else {
            throw new InvalidOperationException("Unknown INodeMeta implementation.");
        }
    }
    public static IInnerNodeMeta? FromBytes(byte[] data) {
        var metaType = (InnerNodeMetaType)data[0];
        if (metaType == InnerNodeMetaType.Empty) {
            return null;
        } else if (metaType == InnerNodeMetaType.Min) {
            // optimized for performance using span<byte>
            var span = data.AsSpan(1); // skip the first byte for type
            var collectionId = new Guid(span.Slice(0, 16));
            var readAccess = new Guid(span.Slice(16, 16));
            var editAccess = new Guid(span.Slice(32, 16));
            return new InnerNodeMetaMin(collectionId, readAccess, editAccess);
        } else if (metaType == InnerNodeMetaType.Full) {
            // optimized for performance using span<byte>
            var span = data.AsSpan(1);
            var revisionId = BitConverter.ToInt32(span.Slice(0, 4));
            var collectionId = new Guid(span.Slice(4, 16));
            var readAccess = new Guid(span.Slice(20, 16));
            var editAccess = new Guid(span.Slice(36, 16));
            var editViewAccess = new Guid(span.Slice(52, 16));
            var publishAccess = new Guid(span.Slice(68, 16));
            var deleted = span[84] != 0;
            var hidden = span[85] != 0;
            var createdBy = new Guid(span.Slice(86, 16));
            var changedBy = new Guid(span.Slice(102, 16));
            var cultureId = new Guid(span.Slice(118, 16));

            // DateTimes (forcing little endian)
            long releaseBinary = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(134, 8));
            long expireBinary = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(142, 8));
            DateTime? releaseUtc = releaseBinary != 0 ? DateTime.FromBinary(releaseBinary) : null;
            DateTime? expireUtc = expireBinary != 0 ? DateTime.FromBinary(expireBinary) : null;

            return new InnerNodeMetaFull(revisionId, collectionId, readAccess, editAccess, editViewAccess, publishAccess, deleted, hidden, createdBy, changedBy, cultureId, releaseUtc, expireUtc);
        } else {
            throw new InvalidOperationException("Unknown INodeMeta type in byte array.");
        }
    }
    internal static bool IEquals(IInnerNodeMeta? x, IInnerNodeMeta? y) {
        if (x == null && y == null) return true;
        if (x == null || y == null) return false;
        if (x is InnerNodeMetaEmpty && y is InnerNodeMetaEmpty) return true;
        if (x is InnerNodeMetaMin && y is InnerNodeMetaMin) {
            return x.CollectionId == y.CollectionId
                && x.ReadAccess == y.ReadAccess
                && x.EditAccess == y.EditAccess;
        }
        return
            x.RevisionKey == y.RevisionKey
            && x.CollectionId == y.CollectionId
            && x.ReadAccess == y.ReadAccess
            && x.EditAccess == y.EditAccess
            && x.EditViewAccess == y.EditViewAccess
            && x.PublishAccess == y.PublishAccess
            && x.Deleted == y.Deleted
            && x.Hidden == y.Hidden
            && x.CreatedBy == y.CreatedBy
            && x.ChangedBy == y.ChangedBy
            && x.CultureId == y.CultureId
            && x.ReleaseUtc == y.ReleaseUtc
            && x.ExpireUtc == y.ExpireUtc;
    }
    internal static int GetIHashCode([DisallowNull] IInnerNodeMeta obj, ref int lastHash) {
        if (lastHash != 0) return lastHash;
        HashCode hash = new();

        // cannot do below line, as object with same values but different types would have different hashcodes:
        // if (obj is NodeMetaEmpty) return 0; 
        hash.Add(obj.RevisionKey);
        hash.Add(obj.CollectionId);
        hash.Add(obj.ReadAccess);
        hash.Add(obj.EditAccess);

        // cannot do below line, as object with same values but different types would have different hashcodes:
        // if (obj is NodeMetaMin) return hash.ToHashCode();

        hash.Add(obj.EditViewAccess);
        hash.Add(obj.PublishAccess);
        hash.Add(obj.Deleted);
        hash.Add(obj.Hidden);
        hash.Add(obj.CreatedBy);
        hash.Add(obj.ChangedBy);
        hash.Add(obj.CultureId);
        hash.Add(obj.ReleaseUtc);
        hash.Add(obj.ExpireUtc);
        lastHash = hash.ToHashCode();
        return lastHash;
    }
    public static IInnerNodeMeta? MinimizeIfPossible(IInnerNodeMeta? meta) {
        if (meta == null) return null;
        if (CanBeEmptyOrNull(meta)) return null; // null and empty are treated as the same, set to null to save space
        if (CanBeMin(meta)) return new InnerNodeMetaMin(
            collectionId: meta.CollectionId,
            readAccess: meta.ReadAccess,
            editAccess: meta.EditAccess
        );
        return meta;
    }
    public static IInnerNodeMeta? CopyAndSetRevisionTypeAndKey(IInnerNodeMeta meta, RevisionType revisionType, int revisionKey) {
        return MinimizeIfPossible(new InnerNodeMetaFull(
            revisionKey: revisionKey,
            collectionId: meta.CollectionId,
            readAccess: meta.ReadAccess,
            editAccess: meta.EditAccess,
            editViewAccess: meta.EditViewAccess,
            publishAccess: meta.PublishAccess,
            deleted: meta.Deleted,
            hidden: meta.Hidden,
            createdBy: meta.CreatedBy,
            changedBy: meta.ChangedBy,
            cultureId: meta.CultureId,
            releaseUtc: meta.ReleaseUtc,
            expireUtc: meta.ExpireUtc
        ));
    }
}
class InnerNodeMetaEmpty : IInnerNodeMeta {
    public int RevisionKey => 0;
    public RevisionType RevisionType => RevisionType.Published;
    public Guid CollectionId => Guid.Empty;
    public Guid ReadAccess => Guid.Empty;
    public Guid EditAccess => Guid.Empty;
    public Guid EditViewAccess => Guid.Empty;
    public Guid PublishAccess => Guid.Empty;
    public bool Deleted => false;
    public bool Hidden => false;
    public Guid CreatedBy => Guid.Empty;
    public Guid ChangedBy => Guid.Empty;
    public Guid CultureId => Guid.Empty;
    public DateTime? ReleaseUtc => null;
    public DateTime? ExpireUtc => null;
    public override bool Equals(object? obj) {
        if (obj is IInnerNodeMeta meta) return IInnerNodeMeta.IEquals(this, meta);
        return base.Equals(obj);
    }
    static int _hashCode = IInnerNodeMeta.Empty.GetHashCode(); // precompute hashcode for empty
    public override int GetHashCode() => _hashCode;
    public byte[] ToBytes() => [];
    public bool Equals(IInnerNodeMeta? other) => IInnerNodeMeta.IEquals(this, other);
}
public class InnerNodeMetaMin : IInnerNodeMeta {
    public InnerNodeMetaMin(
        Guid collectionId,
        Guid readAccess,
        Guid editAccess
    ) {
        CollectionId = collectionId;
        ReadAccess = readAccess;
        EditAccess = editAccess;
    }
    public int RevisionKey => 0;
    public RevisionType RevisionType => RevisionType.Published;
    public Guid CollectionId { get; }
    public Guid ReadAccess { get; }
    public Guid EditAccess { get; }
    public Guid EditViewAccess => EditAccess; // common for all revisions and cultures
    public Guid PublishAccess => EditAccess; // common for all revisions and cultures
    public bool Deleted => false; // common for all revisions and cultures
    public bool Hidden => false; // common for all revisions and cultures

    public Guid CreatedBy => Guid.Empty;
    public Guid ChangedBy => Guid.Empty;
    public Guid CultureId => Guid.Empty;

    public DateTime? ReleaseUtc => null;
    public DateTime? ExpireUtc => null;
    public override bool Equals(object? obj) {
        if (obj is IInnerNodeMeta meta) return IInnerNodeMeta.IEquals(this, meta);
        return base.Equals(obj);
    }
    public bool Equals(IInnerNodeMeta? other) => IInnerNodeMeta.IEquals(this, other);
    int _lastHash = 0;
    public override int GetHashCode() {
        return IInnerNodeMeta.GetIHashCode(this, ref _lastHash);
    }
}
public class InnerNodeMetaFull : IInnerNodeMeta {
    public int RevisionKey { get; }
    public RevisionType RevisionType { get; }
    public Guid CollectionId { get; }
    public Guid ReadAccess { get; }
    public Guid EditAccess { get; }
    public Guid EditViewAccess { get; }
    public Guid PublishAccess { get; }
    public bool Deleted { get; }
    public bool Hidden { get; }

    public Guid CreatedBy { get; } // specific
    public Guid ChangedBy { get; } // specific
    public Guid CultureId { get; } // specific

    public DateTime? ReleaseUtc { get; } // specific
    public DateTime? ExpireUtc { get; } // specific

    public InnerNodeMetaFull(int revisionKey, Guid collectionId, Guid readAccess, Guid editAccess, Guid editViewAccess, Guid publishAccess, bool deleted, bool hidden, Guid createdBy, Guid changedBy, Guid cultureId, DateTime? releaseUtc, DateTime? expireUtc) {
        RevisionKey = revisionKey;
        RevisionType = RevisionUtil.GetRevisionTypeFromKey(revisionKey);
        CollectionId = collectionId;
        ReadAccess = readAccess;
        EditAccess = editAccess;
        EditViewAccess = editViewAccess;
        PublishAccess = publishAccess;
        Deleted = deleted;
        Hidden = hidden;
        CreatedBy = createdBy;
        ChangedBy = changedBy;
        CultureId = cultureId;
        ReleaseUtc = releaseUtc;
        ExpireUtc = expireUtc;
    }

    public override bool Equals(object? obj) {
        if (obj is IInnerNodeMeta meta) return IInnerNodeMeta.IEquals(this, meta);
        return base.Equals(obj);
    }
    public bool Equals(IInnerNodeMeta? other) => IInnerNodeMeta.IEquals(this, other);
    int _lastHash = 0;
    public override int GetHashCode() {
        return IInnerNodeMeta.GetIHashCode(this, ref _lastHash);
    }
}