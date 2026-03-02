using Microsoft.CodeAnalysis;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.Datamodels;

public enum NodeMetaType : byte {
    Empty = 0,
    Min = 1,
    Full = 2,
}

public class NodeMeta {
    INodeMeta _meta;
    NodeMeta() {
        _meta = INodeMeta.Empty;
        DisplayName = string.Empty;
    }
    public NodeMeta(INodeDataOuter node) {
        _meta = node.Meta ?? INodeMeta.Empty;
        DisplayName = node.ToString()!;
    }

    public static NodeMeta Empty { get; } = new NodeMeta();
    public string DisplayName { get; }

    public RevisionType RevisionType => _meta.RevisionType;
    public int RevisionId => _meta.RevisionId;

    public Guid CollectionId => _meta.CollectionId;
    public Guid ReadAccess => _meta.ReadAccess;
    public Guid EditAccess => _meta.EditAccess;
    public Guid EditViewAccess => _meta.EditViewAccess;
    public Guid PublishAccess => _meta.PublishAccess;
    public bool Deleted => _meta.Deleted;
    public bool Hidden => _meta.Hidden;
    public Guid CreatedBy => _meta.CreatedBy;
    public Guid ChangedBy => _meta.ChangedBy;
    public Guid CultureId => _meta.CultureId;
    public DateTime? ReleaseUtc => _meta.ReleaseUtc;
    public DateTime? ExpireUtc => _meta.ExpireUtc;

    public int GetHashCode([DisallowNull] INodeMeta obj) {
        throw new NotImplementedException();
    }
    public override bool Equals(object? obj) {
        throw new NotImplementedException();
    }
    public override int GetHashCode() {
        return base.GetHashCode();
    }
}
public interface INodeMeta : IEqualityComparer<INodeMeta> { // Without revision ID, so it will be similar for different nodes and node revisions
    int RevisionId { get; }
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

    public static INodeMeta? ChangeCulture(INodeMeta? meta, Guid cultureId) {
        if (meta == null) {
            if (cultureId == Guid.Empty) return null; // null and empty are treated as the same, return null to save space
            return new NodeMetaFull(
                revisionId: 0,
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
        var metaWithNewCulture = new NodeMetaFull(
            revisionId: meta.RevisionId,
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
        );
        if (CanBeMin(metaWithNewCulture)) return new NodeMetaMin(
            collectionId: metaWithNewCulture.CollectionId,
            readAccess: metaWithNewCulture.ReadAccess,
            editAccess: metaWithNewCulture.EditAccess
        );
        if (CanBeEmptyOrNull(metaWithNewCulture)) return null; // null and empty are treated as the same, set to null to save space
        return metaWithNewCulture;
    }

    public static INodeMeta? DeriveCombinedMeta(INodeMeta? original, INodeMeta? newRev) {
        if (original == null && newRev == null) return null;
        var meta = new NodeMetaFull(

            // take common props from newRev
            collectionId: newRev?.CollectionId ?? Empty.CollectionId,
            readAccess: newRev?.ReadAccess ?? Empty.ReadAccess,
            editAccess: newRev?.EditAccess ?? Empty.EditAccess,
            editViewAccess: newRev?.EditViewAccess ?? Empty.EditViewAccess,
            publishAccess: newRev?.PublishAccess ?? Empty.PublishAccess,
            deleted: newRev?.Deleted ?? Empty.Deleted,
            hidden: newRev?.Hidden ?? Empty.Hidden,

            // keep revision specific properties             
            revisionId: original?.RevisionId ?? Empty.RevisionId,
            createdBy: original?.CreatedBy ?? Empty.CreatedBy,
            changedBy: original?.ChangedBy ?? Empty.ChangedBy,
            cultureId: original?.CultureId ?? Empty.CultureId,
            releaseUtc: original?.ReleaseUtc ?? Empty.ReleaseUtc,
            expireUtc: original?.ExpireUtc ?? Empty.ExpireUtc

        );
        if (CanBeMin(meta)) return new NodeMetaMin(
            collectionId: meta.CollectionId,
            readAccess: meta.ReadAccess,
            editAccess: meta.EditAccess
        );
        if (CanBeEmptyOrNull(meta)) return null; // null and empty are treated as the same, set to null to save space
        return meta;
    }
    public static bool CanBeEmptyOrNull(INodeMeta meta) {
        return meta.RevisionId == 0
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
    public static bool CanBeMin(INodeMeta meta) {
        return meta.RevisionId == 0
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

    public static readonly INodeMeta Empty = new NodeMetaEmpty();
    public static byte[] ToBytes(INodeMeta? meta) {
        if (meta == null || meta is NodeMetaEmpty) {
            return [(byte)NodeMetaType.Empty];
        } else if (meta is NodeMetaMin) {
            // optimized for performance using span<byte> and stackalloc:
            byte[] data = new byte[1 + 16 * 3]; // 1 byte for type + 3 GUIDs
            data[0] = (byte)NodeMetaType.Min;
            // write GUIDs directly to byte array using span and stackalloc
            Span<byte> span = data.AsSpan(1); // skip the first byte for type
            meta.CollectionId.TryWriteBytes(span.Slice(0, 16));
            meta.ReadAccess.TryWriteBytes(span.Slice(16, 16));
            meta.EditAccess.TryWriteBytes(span.Slice(32, 16));
            return data;
        } else if (meta is NodeMetaFull) {
            // optimized for performance using span<byte> and stackalloc:
            byte[] data = new byte[1 + 4 + 16 * 6 + 1 + 1 + 16 * 3 + 8 * 2]; // type + int + 6 GUIDs + 2 bools + 3 GUIDs + 2 DateTimes
            data[0] = (byte)NodeMetaType.Full;
            Span<byte> span = data.AsSpan(1); // skip the first byte for type
            BitConverter.TryWriteBytes(span.Slice(0, 4), meta.RevisionId);
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
    public static INodeMeta? FromBytes(byte[] data) {
        var metaType = (NodeMetaType)data[0];
        if (metaType == NodeMetaType.Empty) {
            return null;
        } else if (metaType == NodeMetaType.Min) {
            // optimized for performance using span<byte>
            var span = data.AsSpan(1); // skip the first byte for type
            var collectionId = new Guid(span.Slice(0, 16));
            var readAccess = new Guid(span.Slice(16, 16));
            var editAccess = new Guid(span.Slice(32, 16));
            return new NodeMetaMin(collectionId, readAccess, editAccess);
        } else if (metaType == NodeMetaType.Full) {
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

            return new NodeMetaFull(revisionId, collectionId, readAccess, editAccess, editViewAccess, publishAccess, deleted, hidden, createdBy, changedBy, cultureId, releaseUtc, expireUtc);
        } else {
            throw new InvalidOperationException("Unknown INodeMeta type in byte array.");
        }
    }
    internal static bool IEquals(INodeMeta? x, INodeMeta? y) {
        if (x == null && y == null) return true;
        if (x == null || y == null) return false;
        if (x is NodeMetaEmpty && y is NodeMetaEmpty) return true;
        if (x is NodeMetaMin && y is NodeMetaMin) {
            return x.CollectionId == y.CollectionId
                && x.ReadAccess == y.ReadAccess
                && x.EditAccess == y.EditAccess;
        }
        return
            x.RevisionId == y.RevisionId
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
    internal static int GetIHashCode([DisallowNull] INodeMeta obj, ref int lastHash) {
        if (lastHash != 0) return lastHash;
        HashCode hash = new();

        // cannot do below line, as object with same values but different types would have different hashcodes:
        // if (obj is NodeMetaEmpty) return 0; 
        hash.Add(obj.RevisionId);
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
}
class NodeMetaEmpty : INodeMeta {
    public int RevisionId => 0;
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
    public bool Equals(INodeMeta? x, INodeMeta? y) => INodeMeta.IEquals(x, y);
    static int _hashCode = INodeMeta.Empty.GetHashCode(); // precompute hashcode for empty
    public int GetHashCode([DisallowNull] INodeMeta obj) => _hashCode;
    public byte[] ToBytes() => [];
}
public class NodeMetaMin : INodeMeta {
    public NodeMetaMin(
        Guid collectionId,
        Guid readAccess,
        Guid editAccess
    ) {
        CollectionId = collectionId;
        ReadAccess = readAccess;
        EditAccess = editAccess;
    }
    public int RevisionId => 0;
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
    public bool Equals(INodeMeta? x, INodeMeta? y) => INodeMeta.IEquals(x, y);
    int _lastHash = 0;
    public int GetHashCode([DisallowNull] INodeMeta obj) => INodeMeta.GetIHashCode(obj, ref _lastHash);
}
public class NodeMetaFull : INodeMeta {
    public int RevisionId { get; }
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

    public NodeMetaFull(int revisionId, Guid collectionId, Guid readAccess, Guid editAccess, Guid editViewAccess, Guid publishAccess, bool deleted, bool hidden, Guid createdBy, Guid changedBy, Guid cultureId, DateTime? releaseUtc, DateTime? expireUtc) {
        RevisionId = revisionId;
        RevisionType = RevisionUtil.GetRevisionType(revisionId);
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

    public bool Equals(INodeMeta? x, INodeMeta? y) => INodeMeta.IEquals(x, y);
    int _lastHash = 0;
    public int GetHashCode([DisallowNull] INodeMeta obj) => INodeMeta.GetIHashCode(obj, ref _lastHash);
}