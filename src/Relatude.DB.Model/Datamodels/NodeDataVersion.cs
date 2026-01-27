using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.Datamodels;
public class NodeDataVersion : NodeData {
    public NodeDataVersion(
        Guid id, int uid, Guid nodeType, DateTime createdUtc, DateTime changedUtc, Properties<object> values, NodeMeta meta)
        : base(id, uid, nodeType, createdUtc, changedUtc, values) {
        Meta = meta;
    }
    public override NodeMeta? Meta { get; }
}
public class NodeDataVersionsContainer : INodeData {
    public NodeDataVersionsContainer(int nodeId, NodeDataVersion[] versions) {
        _id = nodeId;
        Versions = versions;
    }
    int _id;
    public int __Id { get => _id; set => throw new NA(); }
    public NodeDataVersion[] Versions { get; }
    public Guid Id { get => throw new NA(); set => throw new NA(); }

    public Guid NodeType => throw new NA();
    public NodeMeta? Meta => throw new NA();
    public DateTime ChangedUtc => throw new NA();
    public DateTime CreatedUtc { get => throw new NA(); set => throw new NA(); }
    public Guid CollectionId { get => throw new NA(); set => throw new NA(); }
    public Guid ReadAccess { get => throw new NA(); set => throw new NA(); }
    public Guid WriteAccess { get => throw new NA(); set => throw new NA(); }

    public IEnumerable<PropertyEntry<object>> Values => throw new NA();
    public bool ReadOnly => true;
    public IRelations Relations => throw new NA();
    public int ValueCount => throw new NA();
    public void Add(Guid propertyId, object value) => throw new NA();
    public void AddOrUpdate(Guid propertyId, object value) => throw new NA();
    public bool Contains(Guid propertyId) => throw new NA();
    public INodeData Copy() => throw new NA();
    public void EnsureReadOnly() => throw new NA();
    public void RemoveIfPresent(Guid propertyId) => throw new NA();
    public bool TryGetValue(Guid propertyId, [MaybeNullWhen(false)] out object value) => throw new NA();
}
public class NodeMeta : IEquatable<NodeMeta> {
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

    private NodeMeta() { }
    public NodeMeta(
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
    public NodeMeta(
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
    public static NodeMeta FromBytes(byte[] bytes) {
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
        return (obj is NodeMeta other) && Equals(other);
    }
    public bool Equals(NodeMeta? other) {
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
    public static NodeMeta Empty = new();
}
