using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.Datamodels;
public class NodeDataVersion : NodeData {
    public NodeDataVersion(Guid id, int uid, Guid nodeType, DateTime createdUtc, DateTime changedUtc, Properties<object> values, NodeMeta meta)
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
    private NodeMeta() { }
    public NodeMeta(
        Guid collectionId,
        Guid readAccess,
        Guid editViewAccess
    ) {
        CollectionId = collectionId;
        ReadAccess = readAccess;
        EditViewAccess = editViewAccess;
        PublishAccess = editViewAccess;
    }

    public NodeMeta(
        Guid collectionId,
        Guid readAccess,
        Guid editViewAccess,
        Guid publishAccess,
        Guid createdBy,
        Guid changedBy,
        Guid cultureId,
        DateTime? releasedUtc,
        DateTime? retainedUtc
    ) {
        CollectionId = collectionId;
        ReadAccess = readAccess;
        EditViewAccess = editViewAccess;
        PublishAccess = publishAccess;
        CreatedBy = createdBy;
        ChangedBy = changedBy;
        CultureId = cultureId;
        ReleasedUtc = releasedUtc;
        RetainedUtc = retainedUtc;
    }

    public Guid CollectionId { get; }
    public Guid ReadAccess { get; }
    public Guid EditViewAccess { get; }
    public Guid PublishAccess { get; }

    public Guid CreatedBy { get; }
    public Guid ChangedBy { get; }
    public Guid CultureId { get; }

    public DateTime? ReleasedUtc { get; }
    public DateTime? RetainedUtc { get; }

    public override int GetHashCode() {
        var hash = HashCode.Combine(
            CollectionId,
            ReadAccess,
            EditViewAccess,
            PublishAccess,
            CreatedBy,
            ChangedBy,
            CultureId
        );
        if (ReleasedUtc.HasValue) hash = HashCode.Combine(hash, ReleasedUtc.Value);
        if (RetainedUtc.HasValue) hash = HashCode.Combine(hash, RetainedUtc.Value);
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
            && ReleasedUtc == other.ReleasedUtc
            && RetainedUtc == other.RetainedUtc;
    }
    public static NodeMeta Empty = new();
}
public class NodeMetaWithType : NodeMeta, IEquatable<NodeMetaWithType> {
    public NodeMetaWithType(NodeMeta nodeMeta, Guid nodeTypeId) : base(
        nodeMeta.CollectionId,
        nodeMeta.ReadAccess,
        nodeMeta.EditViewAccess,
        nodeMeta.PublishAccess,
        nodeMeta.CreatedBy,
        nodeMeta.ChangedBy,
        nodeMeta.CultureId,
        nodeMeta.ReleasedUtc,
        nodeMeta.RetainedUtc
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
}