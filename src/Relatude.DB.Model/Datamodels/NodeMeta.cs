namespace Relatude.DB.Datamodels;

public interface INodeMeta {
    Guid CollectionId { get; }
    Guid ReadAccess { get; }
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
}
public interface INodeMetaInternal {
    int CollectionId { get; }
    int ReadAccess { get; }
    int EditViewAccess { get; }
    int PublishAccess { get; }
    bool Deleted { get; }
    bool Hidden { get; }
    bool AnyPublishedContentAnyDate { get; }
    int CreatedBy { get; }
    int ChangedBy { get; }
    int CultureId { get; }
    DateTime? ReleaseUtc { get; }
    DateTime? ExpireUtc { get; }
}
public class NodeMeta : INodeMeta {
    public Guid CollectionId { get; } // common
    public Guid ReadAccess { get; } // common

    public Guid EditViewAccess { get; } // common
    public Guid PublishAccess { get; } // common
    public bool Deleted { get; } // common
    public bool Hidden { get; } // common
    public bool AnyPublishedContentAnyDate { get; } // common

    public Guid CreatedBy { get; } // specific
    public Guid ChangedBy { get; } // specific
    public Guid CultureId { get; } // specific

    public DateTime? ReleaseUtc { get; } // specific
    public DateTime? ExpireUtc { get; } // specific
}
public class NodeMetaInternal : INodeMetaInternal, IEquatable<INodeMetaInternal> {
    public int CollectionId { get; } // common
    public int ReadAccess { get; } // common

    public int EditViewAccess { get; } // common
    public int PublishAccess { get; } // common
    public bool Deleted { get; } // common
    public bool Hidden { get; } // common
    public bool AnyPublishedContentAnyDate { get; } // common

    public int CreatedBy { get; } // specific
    public int ChangedBy { get; } // specific
    public int CultureId { get; } // specific

    public DateTime? ReleaseUtc { get; } // specific
    public DateTime? ExpireUtc { get; } // specific

    private NodeMetaInternal() { }
    public NodeMetaInternal(
        int collectionId,
        int readAccess,
        int editViewAccess,
        bool hidden
    ) {
        CollectionId = collectionId;
        ReadAccess = readAccess;
        EditViewAccess = editViewAccess;
        PublishAccess = editViewAccess;
        Hidden = hidden;
    }
    public NodeMetaInternal(
        int collectionId,
        int readAccess,
        int editViewAccess,
        int publishAccess,
        bool hidden,
        bool deleted,

        int createdBy,
        int changedBy,
        int cultureId,
        DateTime? releasedUtc,
        DateTime? retainedUtc
    ) {
        CollectionId = collectionId;
        ReadAccess = readAccess;
        Deleted = deleted;
        Hidden = hidden;
        EditViewAccess = editViewAccess;
        PublishAccess = publishAccess;
        CreatedBy = createdBy;
        ChangedBy = changedBy;
        CultureId = cultureId;
        ReleaseUtc = releasedUtc;
        ExpireUtc = retainedUtc;
    }
    public byte[] ToBytes() {
        throw new NotImplementedException();
    }
    public static NodeMetaInternal FromBytes(byte[] bytes) {
        throw new NotImplementedException();
    }
    public static NodeMetaInternal FromStream(Stream stream) {
        throw new NotImplementedException();
    }
    public override int GetHashCode() {
        var hash = HashCode.Combine(
            CollectionId,
            ReadAccess,
            EditViewAccess,
            PublishAccess,
            CreatedBy,
            ChangedBy,
            CultureId,
            Deleted
        );
        if (ReleaseUtc.HasValue) hash = HashCode.Combine(hash, ReleaseUtc.Value);
        if (ExpireUtc.HasValue) hash = HashCode.Combine(hash, ExpireUtc.Value);
        return hash;
    }
    public override bool Equals(object? obj) {
        return (obj is NodeMetaInternal other) && Equals(other);
    }
    public bool Equals(INodeMetaInternal? other) {
        if (other is null) return false;
        return CollectionId == other.CollectionId
            && ReadAccess == other.ReadAccess
            && EditViewAccess == other.EditViewAccess
            && PublishAccess == other.PublishAccess
            && CreatedBy == other.CreatedBy
            && ChangedBy == other.ChangedBy
            && CultureId == other.CultureId
            && ReleaseUtc == other.ReleaseUtc
            && ExpireUtc == other.ExpireUtc
            && Deleted == other.Deleted;
    }

    public void ToStream(Stream stream) {
        throw new NotImplementedException();
    }

    public static NodeMetaInternal Empty = new();
}
