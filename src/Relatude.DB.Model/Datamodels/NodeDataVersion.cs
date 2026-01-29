using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.Datamodels;

public class NodeDataVersion : NodeData {
    public NodeDataVersion(
        Guid id, int uid, Guid nodeType, DateTime createdUtc, DateTime changedUtc, Properties<object> values, NodeMetaInternal meta)
        : base(id, uid, nodeType, createdUtc, changedUtc, values) {
        _setMeta(meta);
    }    
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
    public NodeMetaInternal? Meta => throw new NA();
    public DateTime ChangedUtc => throw new NA();
    public DateTime CreatedUtc { get => throw new NA(); set => throw new NA(); }

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
    public bool TryGetValue<T>(Guid propertyId, [MaybeNullWhen(false)] out T value) => throw new NA();
}
