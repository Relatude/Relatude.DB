using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.Datamodels;

public enum NodeMetaType : byte {
    Empty = 0,
    Min = 1,
    Full = 2,
}
public interface INodeMeta : IEqualityComparer<INodeMeta> { // Without revision ID, so it will be similar for different nodes and node revisions
    Guid CollectionId { get; } // common for all cultures
    Guid ReadAccess { get; }  // common for all cultures
    Guid EditAccess { get; }  // common for all cultures
    Guid EditViewAccess { get; }  // common for all cultures
    Guid PublishAccess { get; }  // common for all cultures
    bool Deleted { get; } // common for all cultures
    bool Hidden { get; } // common for all cultures
    bool AnyPublishedContentAnyDate { get; } // common for all cultures
    Guid CreatedBy { get; } 
    Guid ChangedBy { get; }
    Guid CultureId { get; }
    DateTime? ReleaseUtc { get; } 
    DateTime? ExpireUtc { get; }
    
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
            anyPublishedContentAnyDate: original?.AnyPublishedContentAnyDate ?? Empty.AnyPublishedContentAnyDate,
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
        return meta.CollectionId == Guid.Empty
            && meta.ReadAccess == Guid.Empty
            && meta.EditAccess == Guid.Empty
            && meta.EditViewAccess == Guid.Empty
            && meta.PublishAccess == Guid.Empty
            && !meta.Deleted
            && !meta.Hidden
            && meta.AnyPublishedContentAnyDate
            && meta.CreatedBy == Guid.Empty
            && meta.ChangedBy == Guid.Empty
            && meta.CultureId == Guid.Empty
            && meta.ReleaseUtc == null
            && meta.ExpireUtc == null;
    }
    public static bool CanBeMin(INodeMeta meta) {
        return meta.EditViewAccess == meta.EditAccess
            && meta.PublishAccess == meta.EditAccess
            && !meta.Deleted
            && !meta.Hidden
            && meta.AnyPublishedContentAnyDate
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
            Span<byte> buffer = stackalloc byte[1 + 32 * 3];
            buffer[0] = (byte)NodeMetaType.Min;
            meta.CollectionId.TryWriteBytes(buffer.Slice(1, 16));
            meta.ReadAccess.TryWriteBytes(buffer.Slice(17, 16));
            meta.EditAccess.TryWriteBytes(buffer.Slice(33, 16));
            return buffer.ToArray();
        } else if (meta is NodeMetaFull) {
            Span<byte> buffer = stackalloc byte[1 + 16 * 7 + 1 * 3 + 8 * 2];
            buffer[0] = (byte)NodeMetaType.Full;
            meta.CollectionId.TryWriteBytes(buffer.Slice(1, 16));
            meta.ReadAccess.TryWriteBytes(buffer.Slice(17, 16));
            meta.EditAccess.TryWriteBytes(buffer.Slice(33, 16));
            meta.EditViewAccess.TryWriteBytes(buffer.Slice(49, 16));
            meta.PublishAccess.TryWriteBytes(buffer.Slice(65, 16));
            buffer[81] = (byte)(meta.Deleted ? 1 : 0);
            buffer[82] = (byte)(meta.Hidden ? 1 : 0);
            buffer[83] = (byte)(meta.AnyPublishedContentAnyDate ? 1 : 0);
            meta.CreatedBy.TryWriteBytes(buffer.Slice(84, 16));
            meta.ChangedBy.TryWriteBytes(buffer.Slice(100, 16));
            meta.CultureId.TryWriteBytes(buffer.Slice(116, 16));
            long releaseTicks = meta.ReleaseUtc?.Ticks ?? 0;
            long expireTicks = meta.ExpireUtc?.Ticks ?? 0;
            BitConverter.TryWriteBytes(buffer.Slice(132, 8), releaseTicks);
            BitConverter.TryWriteBytes(buffer.Slice(140, 8), expireTicks);
            return buffer.ToArray();
        } else {
            throw new InvalidOperationException("Unknown INodeMeta implementation.");
        }
    }
    public static INodeMeta? FromBytes(byte[] data) {
        var metaType = (NodeMetaType)data[0];
        if (metaType == NodeMetaType.Empty) {
            return null;
        } else if (metaType == NodeMetaType.Min) {
            Guid collectionId = new Guid(new ReadOnlySpan<byte>(data, 1, 16));
            Guid readAccess = new Guid(new ReadOnlySpan<byte>(data, 17, 16));
            Guid editAccess = new Guid(new ReadOnlySpan<byte>(data, 33, 16));
            return new NodeMetaMin(collectionId, readAccess, editAccess);
        } else if (metaType == NodeMetaType.Full) {
            Guid collectionId = new Guid(new ReadOnlySpan<byte>(data, 1, 16));
            Guid readAccess = new Guid(new ReadOnlySpan<byte>(data, 17, 16));
            Guid editAccess = new Guid(new ReadOnlySpan<byte>(data, 33, 16));
            Guid editViewAccess = new Guid(new ReadOnlySpan<byte>(data, 49, 16));
            Guid publishAccess = new Guid(new ReadOnlySpan<byte>(data, 65, 16));
            bool deleted = data[81] != 0;
            bool hidden = data[82] != 0;
            bool anyPublishedContentAnyDate = data[83] != 0;
            Guid createdBy = new Guid(new ReadOnlySpan<byte>(data, 84, 16));
            Guid changedBy = new Guid(new ReadOnlySpan<byte>(data, 100, 16));
            Guid cultureId = new Guid(new ReadOnlySpan<byte>(data, 116, 16));
            long releaseTicks = BitConverter.ToInt64(data, 132);
            long expireTicks = BitConverter.ToInt64(data, 140);
            DateTime? releaseUtc = releaseTicks == 0 ? null : new DateTime(releaseTicks, DateTimeKind.Utc);
            DateTime? expireUtc = expireTicks == 0 ? null : new DateTime(expireTicks, DateTimeKind.Utc);
            return new NodeMetaFull(collectionId, readAccess, editAccess, editViewAccess, publishAccess, deleted, hidden, anyPublishedContentAnyDate, createdBy, changedBy, cultureId, releaseUtc, expireUtc);
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
        return x.CollectionId == y.CollectionId
            && x.ReadAccess == y.ReadAccess
            && x.EditAccess == y.EditAccess
            && x.EditViewAccess == y.EditViewAccess
            && x.PublishAccess == y.PublishAccess
            && x.Deleted == y.Deleted
            && x.Hidden == y.Hidden
            && x.AnyPublishedContentAnyDate == y.AnyPublishedContentAnyDate
            && x.CreatedBy == y.CreatedBy
            && x.ChangedBy == y.ChangedBy
            && x.CultureId == y.CultureId
            && x.ReleaseUtc == y.ReleaseUtc
            && x.ExpireUtc == y.ExpireUtc;
    }
    internal static int GetIHashCode([DisallowNull] INodeMeta obj) {
        HashCode hash = new();

        // cannot do below line, as object with same values but different types would have different hashcodes:
        // if (obj is NodeMetaEmpty) return 0; 

        hash.Add(obj.CollectionId);
        hash.Add(obj.ReadAccess);
        hash.Add(obj.EditAccess);

        // cannot do below line, as object with same values but different types would have different hashcodes:
        // if (obj is NodeMetaMin) return hash.ToHashCode();

        hash.Add(obj.EditViewAccess);
        hash.Add(obj.PublishAccess);
        hash.Add(obj.Deleted);
        hash.Add(obj.Hidden);
        hash.Add(obj.AnyPublishedContentAnyDate);
        hash.Add(obj.CreatedBy);
        hash.Add(obj.ChangedBy);
        hash.Add(obj.CultureId);
        hash.Add(obj.ReleaseUtc);
        hash.Add(obj.ExpireUtc);
        return hash.ToHashCode();
    }
}
class NodeMetaEmpty : INodeMeta {
    public Guid CollectionId  => Guid.Empty;
    public Guid ReadAccess => Guid.Empty;
    public Guid EditAccess => Guid.Empty;
    public Guid EditViewAccess => Guid.Empty;
    public Guid PublishAccess => Guid.Empty;
    public bool Deleted => false;
    public bool Hidden => false;
    public bool AnyPublishedContentAnyDate => true;
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
    public Guid CollectionId { get; }
    public Guid ReadAccess { get; }
    public Guid EditAccess { get; }
    public Guid EditViewAccess => EditAccess; // common for all revisions and cultures
    public Guid PublishAccess => EditAccess; // common for all revisions and cultures
    public bool Deleted => false; // common for all revisions and cultures
    public bool Hidden => false; // common for all revisions and cultures
    public bool AnyPublishedContentAnyDate => true;

    public Guid CreatedBy => Guid.Empty;
    public Guid ChangedBy => Guid.Empty;
    public Guid CultureId => Guid.Empty;

    public DateTime? ReleaseUtc => null;
    public DateTime? ExpireUtc => null;
    public bool Equals(INodeMeta? x, INodeMeta? y) => INodeMeta.IEquals(x, y);
    public int GetHashCode([DisallowNull] INodeMeta obj) => INodeMeta.GetIHashCode(obj);
}
public class NodeMetaFull : INodeMeta {
    public Guid CollectionId { get; }
    public Guid ReadAccess { get; }
    public Guid EditAccess { get; }
    public Guid EditViewAccess { get; }
    public Guid PublishAccess { get; }
    public bool Deleted { get; }
    public bool Hidden { get; }
    public bool AnyPublishedContentAnyDate { get; }

    public Guid CreatedBy { get; } // specific
    public Guid ChangedBy { get; } // specific
    public Guid CultureId { get; } // specific

    public DateTime? ReleaseUtc { get; } // specific
    public DateTime? ExpireUtc { get; } // specific

    public NodeMetaFull(Guid collectionId, Guid readAccess, Guid editAccess, Guid editViewAccess, Guid publishAccess, bool deleted, bool hidden, bool anyPublishedContentAnyDate, Guid createdBy, Guid changedBy, Guid cultureId, DateTime? releaseUtc, DateTime? expireUtc) {
        CollectionId = collectionId;
        ReadAccess = readAccess;
        EditAccess = editAccess;
        EditViewAccess = editViewAccess;
        PublishAccess = publishAccess;
        Deleted = deleted;
        Hidden = hidden;
        AnyPublishedContentAnyDate = anyPublishedContentAnyDate;
        CreatedBy = createdBy;
        ChangedBy = changedBy;
        CultureId = cultureId;
        ReleaseUtc = releaseUtc;
        ExpireUtc = expireUtc;
    }

    public bool Equals(INodeMeta? x, INodeMeta? y) => INodeMeta.IEquals(x, y);
    public int GetHashCode([DisallowNull] INodeMeta obj) => INodeMeta.GetIHashCode(obj);
}