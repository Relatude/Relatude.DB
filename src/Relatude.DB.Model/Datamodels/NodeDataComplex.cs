using System;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.Datamodels;

public class NodeDataComplex : NodeData {
    public NodeDataComplex(Guid id, int uid, Guid nodeType, DateTime createdUtc, DateTime changedUtc, Properties<object> values, NodeComplexMeta meta)
        : base(id, uid, nodeType, createdUtc, changedUtc, values) {
        Meta = meta;
    }
    public override NodeComplexMeta? Meta { get; }
}
public class NodeDataComplexContainer : INodeData {
    public NodeDataComplexContainer(int nodeId, NodeDataComplex[] versions) {
        _id = nodeId;
        Versions = versions;
    }
    int _id;
    public int __Id { get => _id; set => throw new NotImplementedException(); }
    public NodeDataComplex[] Versions { get; }
    public Guid Id { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public Guid NodeType => throw new NotImplementedException();
    public NodeComplexMeta? Meta => throw new NotImplementedException();
    public DateTime ChangedUtc => throw new NotImplementedException();
    public DateTime CreatedUtc { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
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

public class NodeComplexMeta {
    public int CollectionId { get; }

    public int ReadAccess { get; }
    public int EditViewAccess { get; }
    public int PublishAccess { get; }

    public int CreatedBy { get; }
    public int ChangedBy { get; }
    public int CultureId { get; }
    public DateTime PublishedUtc { get; }
    public DateTime RetainedUtc { get; }
    public DateTime ReleasedUtc { get; }
}
