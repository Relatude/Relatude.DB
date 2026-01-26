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

    private NodeMeta() { }
    public NodeMeta(
        Guid collectionId,
        Guid readAccess,
        Guid editViewAccess,
        bool hidden
    ) {
        CollectionId = collectionId;
        ReadAccess = readAccess;
        EditViewAccess = editViewAccess;
        PublishAccess = editViewAccess;
        Hidden = hidden;
    }
    public NodeMeta(
        Guid collectionId,
        Guid readAccess,
        Guid editViewAccess,
        Guid publishAccess,
        bool hidden,
        bool deleted,

        Guid createdBy,
        Guid changedBy,
        Guid cultureId,
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
public class NodeMetaWithType : NodeMeta, IEquatable<NodeMetaWithType> {
    public NodeMetaWithType(NodeMeta nodeMeta, Guid nodeTypeId) : base(
        nodeMeta.CollectionId,
        nodeMeta.ReadAccess,
        nodeMeta.EditViewAccess,
        nodeMeta.PublishAccess,
        nodeMeta.Hidden,
        nodeMeta.Deleted,
        nodeMeta.CreatedBy,
        nodeMeta.ChangedBy,
        nodeMeta.CultureId,
        nodeMeta.ReleaseUtc,
        nodeMeta.ExpireUtc
    ) {
        NodeTypeId = nodeTypeId;
    }
    public Guid NodeTypeId { get; set; }
    public override bool Equals(object? obj) {
        return (obj is NodeMetaWithType other) && Equals(other);
    }
    public bool Equals(NodeMetaWithType? other) {
        if (other is null) return false;
        return NodeTypeId == other.NodeTypeId && base.Equals(other);
    }
    override public int GetHashCode() {
        return HashCode.Combine(base.GetHashCode(), NodeTypeId);
    }
    public byte[] ToBytes() {
        // all properties:
        var buffer = new System.IO.MemoryStream();
        buffer.Write(CollectionId.ToByteArray());
        buffer.Write(ReadAccess.ToByteArray());
        buffer.Write(EditViewAccess.ToByteArray());
        buffer.Write(PublishAccess.ToByteArray());
        buffer.Write(BitConverter.GetBytes(Deleted));
        buffer.Write(BitConverter.GetBytes(Hidden));
        buffer.Write(CreatedBy.ToByteArray());
        buffer.Write(ChangedBy.ToByteArray());
        buffer.Write(CultureId.ToByteArray());
        if (ReleaseUtc.HasValue) {
            buffer.Write(BitConverter.GetBytes(true));
            buffer.Write(BitConverter.GetBytes(ReleaseUtc.Value.ToBinary()));
        } else {
            buffer.Write(BitConverter.GetBytes(false));
        }
        if (ExpireUtc.HasValue) {
            buffer.Write(BitConverter.GetBytes(true));
            buffer.Write(BitConverter.GetBytes(ExpireUtc.Value.ToBinary()));
        } else {
            buffer.Write(BitConverter.GetBytes(false));
        }
        buffer.Write(NodeTypeId.ToByteArray());
        return buffer.ToArray();
    }
    public static NodeMetaWithType FromBytes(byte[] bytes) { 
        var buffer = new System.IO.MemoryStream(bytes);
        Span<byte> guidBuffer = stackalloc byte[16];
        buffer.Read(guidBuffer);
        var collectionId = new Guid(guidBuffer);
        buffer.Read(guidBuffer);
        var readAccess = new Guid(guidBuffer);
        buffer.Read(guidBuffer);
        var editViewAccess = new Guid(guidBuffer);
        buffer.Read(guidBuffer);
        var publishAccess = new Guid(guidBuffer);
        Span<byte> boolBuffer = stackalloc byte[1];
        buffer.Read(boolBuffer);
        var deleted = BitConverter.ToBoolean(boolBuffer);
        buffer.Read(boolBuffer);
        var hidden = BitConverter.ToBoolean(boolBuffer);
        buffer.Read(guidBuffer);
        var createdBy = new Guid(guidBuffer);
        buffer.Read(guidBuffer);
        var changedBy = new Guid(guidBuffer);
        buffer.Read(guidBuffer);
        var cultureId = new Guid(guidBuffer);
        buffer.Read(boolBuffer);
        var hasReleaseUtc = BitConverter.ToBoolean(boolBuffer);
        DateTime? releaseUtc = null;
        if (hasReleaseUtc) {
            Span<byte> dateTimeBuffer = stackalloc byte[8];
            buffer.Read(dateTimeBuffer);
            releaseUtc = DateTime.FromBinary(BitConverter.ToInt64(dateTimeBuffer));
        }
        buffer.Read(boolBuffer);
        var hasExpireUtc = BitConverter.ToBoolean(boolBuffer);
        DateTime? expireUtc = null;
        if (hasExpireUtc) {
            Span<byte> dateTimeBuffer = stackalloc byte[8];
            buffer.Read(dateTimeBuffer);
            expireUtc = DateTime.FromBinary(BitConverter.ToInt64(dateTimeBuffer));
        }
        buffer.Read(guidBuffer);
        var nodeTypeId = new Guid(guidBuffer);
        return new NodeMetaWithType(
            new NodeMeta(
                collectionId,
                readAccess,
                editViewAccess,
                publishAccess,
                hidden,
                deleted,
                createdBy,
                changedBy,
                cultureId,
                releaseUtc,
                expireUtc
            ),
            nodeTypeId
        );
    }


}