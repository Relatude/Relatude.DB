using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.Datamodels;

public class NodeDataWithMeta : NodeData {
    public NodeDataWithMeta(Guid id, int uid, Guid nodeType, DateTime createdUtc, DateTime changedUtc, Properties<object> values, NodeMeta meta)
        : base(id, uid, nodeType, createdUtc, changedUtc, values) {
        Meta = meta;
    }
    public override NodeMeta? Meta { get; }
}
public class NodeDataVersionContainer : INodeData {
    public NodeDataVersionContainer(int nodeId, NodeDataWithMeta[] versions) {
        _id = nodeId;
        Versions = versions;
    }
    int _id;
    public int __Id { get => _id; set => throw new NotImplementedException(); }
    public NodeDataWithMeta[] Versions { get; }
    public Guid Id { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public Guid NodeType => throw new NotImplementedException();
    public NodeMeta? Meta => throw new NotImplementedException();
    public DateTime ChangedUtc => throw new NotImplementedException();
    public DateTime CreatedUtc { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public Guid CollectionId { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public Guid ReadAccess { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public Guid WriteAccess { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public IEnumerable<PropertyEntry<object>> Values => throw new NotImplementedException();
    public bool ReadOnly => true;
    public IRelations Relations => throw new NotImplementedException();
    public int ValueCount => throw new NotImplementedException();
    public void Add(Guid propertyId, object value) => throw new NotImplementedException();
    public void AddOrUpdate(Guid propertyId, object value) => throw new NotImplementedException();
    public bool Contains(Guid propertyId) => throw new NotImplementedException();
    public INodeData Copy() => throw new NotImplementedException();
    public void EnsureReadOnly() => throw new NotImplementedException();
    public void RemoveIfPresent(Guid propertyId) => throw new NotImplementedException();
    public bool TryGetValue(Guid propertyId, [MaybeNullWhen(false)] out object value) => throw new NotImplementedException();
}

public class NodeMeta {
    required public int CollectionId { get; set; }

    required public int ReadAccess { get; set; }
    required public int EditViewAccess { get; set; }
    required public int PublishAccess { get; set; }

    required public int CreatedBy { get; set; }
    required public int ChangedBy { get; set; }
    required public int CultureId { get; set; }
    required public DateTime PublishedUtc { get; set; }
    required public DateTime RetainedUtc { get; set; }
    required public DateTime ReleasedUtc { get; set; }

    public static NodeMeta Empty = new() {
        CollectionId = 0,
        ReadAccess = 0,
        EditViewAccess = 0,
        PublishAccess = 0,
        CreatedBy = 0,
        ChangedBy = 0,
        CultureId = 0,
        PublishedUtc = DateTime.MinValue,
        RetainedUtc = DateTime.MinValue,
        ReleasedUtc = DateTime.MinValue
    };
}