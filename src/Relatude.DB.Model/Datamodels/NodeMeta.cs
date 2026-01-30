using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.Datamodels;

public interface INodeMeta : IEqualityComparer<INodeMeta> {
    Guid CollectionId { get; }
    Guid ReadAccess { get; }
    Guid EditAccess { get; }
    Guid EditViewAccess { get; }
    Guid PublishAccess { get; }
    bool Deleted { get; }
    bool Hidden { get; }
    bool AnyPublishedContentAnyDate { get; }
    Guid CreatedBy { get; }
    Guid ChangedBy { get; }
    Guid CultureId { get; }
    DateTime? ReleaseUtc { get; }
    DateTime? ExpireUtc { get; }
    public static INodeMeta Empty { get; } = new NodeMetaEmpty();
    byte[] ToBytes();
    public static INodeMeta FromBytes(byte[] data) {
        if (data.Length == 0) return Empty;
        if (data.Length == 48) {
            Span<byte> buffer = data;
            return new NodeMetaMin(
                new(buffer.Slice(0, 16)),
                new(buffer.Slice(16, 16)),
                new(buffer.Slice(32, 16))
            );
        }
        if (data.Length == 147) {
            Span<byte> buffer = data;
            Guid collectionId = new(buffer.Slice(0, 16));
            Guid readAccess = new(buffer.Slice(16, 16));
            Guid editAccess = new(buffer.Slice(32, 16));
            Guid editViewAccess = new(buffer.Slice(48, 16));
            Guid publishAccess = new(buffer.Slice(64, 16));
            bool deleted = buffer[80] != 0;
            bool hidden = buffer[81] != 0;
            bool anyPublishedContentAnyDate = buffer[82] != 0;
            Guid createdBy = new(buffer.Slice(83, 16));
            Guid changedBy = new(buffer.Slice(99, 16));
            Guid cultureId = new(buffer.Slice(115, 16));
            long releaseTicks = BitConverter.ToInt64(buffer.Slice(131, 8));
            long expireTicks = BitConverter.ToInt64(buffer.Slice(139, 8));
            DateTime? releaseUtc = releaseTicks == 0 ? null : new DateTime(releaseTicks, DateTimeKind.Utc);
            DateTime? expireUtc = expireTicks == 0 ? null : new DateTime(expireTicks, DateTimeKind.Utc);
            return new NodeMetaFull(collectionId,
                readAccess,
                editAccess,
                editViewAccess,
                publishAccess,
                deleted,
                hidden,
                anyPublishedContentAnyDate,
                createdBy,
                changedBy,
                cultureId,
                releaseUtc,
                expireUtc);
        }
        throw new ArgumentException("Invalid data length for NodeMeta deserialization.", nameof(data));
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
public class NodeMetaEmpty : INodeMeta {
    public Guid CollectionId { get; } = Guid.Empty;
    public Guid ReadAccess { get; } = Guid.Empty;
    public Guid EditAccess { get; } = Guid.Empty;
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
    public Guid EditViewAccess => EditAccess; // common
    public Guid PublishAccess => EditAccess; // common
    public bool Deleted => false; // common
    public bool Hidden => false; // common
    public bool AnyPublishedContentAnyDate => true;

    public Guid CreatedBy => Guid.Empty;
    public Guid ChangedBy => Guid.Empty;
    public Guid CultureId => Guid.Empty;

    public DateTime? ReleaseUtc => null;
    public DateTime? ExpireUtc => null;
    public bool Equals(INodeMeta? x, INodeMeta? y) => INodeMeta.IEquals(x, y);
    public int GetHashCode([DisallowNull] INodeMeta obj) => INodeMeta.GetIHashCode(obj);
    public byte[] ToBytes() {
        Span<byte> buffer = stackalloc byte[32 * 3];
        CollectionId.TryWriteBytes(buffer.Slice(0, 16));
        ReadAccess.TryWriteBytes(buffer.Slice(16, 16));
        EditAccess.TryWriteBytes(buffer.Slice(32, 16));
        return buffer.ToArray();
    }
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
    public byte[] ToBytes() {
        Span<byte> buffer = stackalloc byte[16 * 7 + 1 * 3 + 8 * 2];
        CollectionId.TryWriteBytes(buffer.Slice(0, 16));
        ReadAccess.TryWriteBytes(buffer.Slice(16, 16));
        EditAccess.TryWriteBytes(buffer.Slice(32, 16));
        EditViewAccess.TryWriteBytes(buffer.Slice(48, 16));
        PublishAccess.TryWriteBytes(buffer.Slice(64, 16));
        buffer[80] = (byte)(Deleted ? 1 : 0);
        buffer[81] = (byte)(Hidden ? 1 : 0);
        buffer[82] = (byte)(AnyPublishedContentAnyDate ? 1 : 0);
        CreatedBy.TryWriteBytes(buffer.Slice(83, 16));
        ChangedBy.TryWriteBytes(buffer.Slice(99, 16));
        CultureId.TryWriteBytes(buffer.Slice(115, 16));
        long releaseTicks = ReleaseUtc?.Ticks ?? 0;
        long expireTicks = ExpireUtc?.Ticks ?? 0;
        BitConverter.TryWriteBytes(buffer.Slice(131, 8), releaseTicks);
        BitConverter.TryWriteBytes(buffer.Slice(139, 8), expireTicks);
        return buffer.ToArray();
    }
}