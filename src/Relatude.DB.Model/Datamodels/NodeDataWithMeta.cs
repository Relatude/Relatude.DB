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
    public int __Id { get => _id; set => throw new NA(); }
    public NodeDataWithMeta[] Versions { get; }
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
public struct NodeBasicMeta {
    required public int CollectionId { get; set; }
    required public int ReadAccess { get; set; }
    required public int WriteAccess { get; set; }
}
public class NodeMeta {
    required public NodeBasicMeta Basic { get; set; }
    //required public int CollectionId { get; set; }
    //required public int ReadAccess { get; set; }
    //required public int EditViewAccess { get; set; }
    required public int PublishAccess { get; set; }

    required public int CreatedBy { get; set; }
    required public int ChangedBy { get; set; }
    required public int CultureId { get; set; }
    required public DateTime PublishedUtc { get; set; }
    required public DateTime RetainedUtc { get; set; }
    required public DateTime ReleasedUtc { get; set; }

    public static NodeMeta Empty = new() {
        Basic = new NodeBasicMeta {
            CollectionId = 0,
            ReadAccess = 0,
            WriteAccess = 0
        },
        //CollectionId = 0,
        //ReadAccess = 0,
        //EditViewAccess = 0,
        PublishAccess = 0,
        CreatedBy = 0,
        ChangedBy = 0,
        CultureId = 0,
        PublishedUtc = DateTime.MinValue,
        RetainedUtc = DateTime.MinValue,
        ReleasedUtc = DateTime.MinValue
    };
}