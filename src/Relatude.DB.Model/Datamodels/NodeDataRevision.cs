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
    public NodeDataRevision(Guid guid, int id, Guid nodeType,
    DateTime createdUtc, DateTime changedUtc,
    Properties<object> values, Guid revisionId, RevisionType revisionType, INodeMeta meta)
    : base(guid, id, nodeType, createdUtc, changedUtc, values, meta) {
        RevisionId = revisionId;
        RevisionType = revisionType;
    }
    public NodeDataRevision CopyAsNodeDataRevision(Guid revisionId, RevisionType revisionType, INodeMeta meta) {
        var rev = new NodeDataRevision(Id, __Id, NodeType, CreatedUtc, ChangedUtc, new(_values), revisionId, revisionType, meta);
        return rev;
    }
    public NodeDataRevision CopyRevision() {
        var rev = new NodeDataRevision(Id, __Id, NodeType, CreatedUtc, ChangedUtc, new(_values), RevisionId, RevisionType, Meta);
        return rev;
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
    public INodeDataInner Copy() => CopyRevisions();
    public NodeDataRevisions CopyRevisions() {
        var revs = new NodeDataRevision[Revisions.Length];
        for (int i = 0; i < Revisions.Length; i++) revs[i] = Revisions[i].CopyRevision();
        var data = new NodeDataRevisions(Id, __Id, NodeType, revs);
        return data;
    }
    public NodeDataRevisions CopyAndChangeMeta(INodeMeta meta, Guid revisionId) {
        var revs = new NodeDataRevision[Revisions.Length];
        for (int i = 0; i < Revisions.Length; i++) {
            INodeMeta rMeta;
            if (Revisions[i].RevisionId == revisionId) {
                rMeta = Revisions[i];
            } else { 
                rMeta = Revisions[i].Meta!;
            }
            revs[i] = Revisions[i].CopyAsNodeDataRevision(revisionId, Revisions[i].RevisionType, rMeta);
        }
        var data = new NodeDataRevisions(Id, __Id, NodeType, revs);
        return data;
    }

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
