using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.Datamodels;

public enum RevisionType {
    Binned = 0,
    Archived = 1,
    Preliminary = 2,
    Published = 3,
    AwaitingPublicationApproval = 4,
    AwaitingArchiveApproval = 5,
    AwaitingBinningApproval = 6,
    PermanentlyDeleted = 99,
}
public class NodeDataRevision : NodeDataAbstract, INodeDataOuter {
    public Guid RevisionId { get; }
    public RevisionType RevisionType { get; }
    public NodeDataRevision(Guid guid, int id, Guid nodeType,
        DateTime createdUtc, DateTime changedUtc,
        Properties<object> values, Guid revisionId, RevisionType revisionType)
        : base(guid, id, nodeType, createdUtc, changedUtc, values) {
        RevisionId = revisionId;
        RevisionType = revisionType;
    }
}
public class NodeDataRevisions : INodeDataInner {
    public NodeDataRevisions(Guid guid, int id, Guid typeId, NodeDataRevision[] revisions) {
        _id = id;
        _guid = guid;
        NodeType = typeId;
        Revisions = revisions;
    }
    int _id;
    public int __Id { get => _id; set => throw new NA(); }
    public NodeDataRevision[] Revisions { get; }
    //public string[]? Log{ get; }
    Guid _guid;
    public Guid Id { get => _guid; set => throw new NA(); }
    public Guid NodeType { get; }
    public INodeMeta? Meta => throw new NA();
    public DateTime ChangedUtc => throw new NA();
    public DateTime CreatedUtc { get => throw new NA(); set => throw new NA(); }
    public INodeData Copy() => throw new NA();

    public IEnumerable<PropertyEntry<object>> Values => throw new NA();
    public bool ReadOnly => true;
    public IRelations Relations => throw new NA();
    public int ValueCount => throw new NA();
    public void Add(Guid propertyId, object value) => throw new NA();
    public void AddOrUpdate(Guid propertyId, object value) => throw new NA();
    public bool Contains(Guid propertyId) => throw new NA();
    public void EnsureReadOnly() => throw new NA();
    public void RemoveIfPresent(Guid propertyId) => throw new NA();
    public bool TryGetValue(Guid propertyId, [MaybeNullWhen(false)] out object value) => throw new NA();
    public bool TryGetValue<T>(Guid propertyId, [MaybeNullWhen(false)] out T value) => throw new NA();
}
