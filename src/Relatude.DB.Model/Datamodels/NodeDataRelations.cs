using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.Datamodels;

public class NodeDataWithRelations : INodeDataExternal { // readonly node data with possibility to add relations for use in "include" queries
    INodeDataExternal _node;
    Relations _relations;
    static void throwReadOnlyError() => throw new Exception("Internal error. Should only be created with readonly inner node data. ");
    public NodeDataWithRelations(INodeDataExternal nodeData) {
        if (!nodeData.ReadOnly) throwReadOnlyError();
        _node = nodeData;
        _relations = new();
    }
    public int RevisionKey { get => _node.RevisionKey; }
    public RevisionType RevisionType { get => _node.RevisionType; }

    public void SwapNodeData(Dictionary<int, INodeDataExternal> dic) {
        _node = dic[_node.__Id];
        _relations.SwapNodeData(dic);
    }
    public Guid Id { get => _node.Id; set => throwReadOnlyError(); }
    public int __Id { get => _node.__Id; set => throwReadOnlyError(); }
    public Guid NodeType => _node.NodeType;
    public IInnerNodeMeta? Meta => _node.Meta;
    public bool IsComplex => false;
    public NodeData[] Versions => throw new Exception("Node has no versions. ");
    public DateTime CreatedUtc { get => _node.CreatedUtc; set => throwReadOnlyError(); }
    public DateTime ChangedUtc => _node.ChangedUtc;
    public IEnumerable<PropertyEntry<object>> Values => _node.Values;

    public int ValueCount => _node.ValueCount;
    public bool ReadOnly => _node.ReadOnly;
    public IRelations Relations => _relations;
    public INodeDataInternal Copy() => throw new NA();
    public INodeDataExternal CopyExternal() => throw new NA();
    public void Add(Guid propertyId, object value) => throwReadOnlyError();
    public void AddOrUpdate(Guid propertyId, object value) => throwReadOnlyError();
    public void RemoveIfPresent(Guid propertyId) => throwReadOnlyError();
    public bool Contains(Guid propertyId) => _node.Contains(propertyId);
    public void EnsureReadOnly() => _node.EnsureReadOnly();
    public bool TryGetValue(Guid propertyId, [MaybeNullWhen(false)] out object value) => _node.TryGetValue(propertyId, out value);
    public override string ToString() => $"NodeDataWithRelations: {Id} {NodeType} {CreatedUtc} {ChangedUtc} {ValueCount}";
    public string? Address { get => _node.Address; set => throwReadOnlyError(); }
    public bool AutoAddress { get => _node.AutoAddress; set => throwReadOnlyError(); }
}
public interface IRelations {
    void AddManyRelation(Guid propertyId, NodeDataWithRelations[] manyRelation);
    void AddOneRelation(Guid propertyId, NodeDataWithRelations oneRelation);
    void AddReference(Guid propertyId, NodeDataWithRelations reference);
    void AddReferences(Guid propertyId, NodeDataWithRelations[] references);
    void SetNoRelation(Guid propertyId);
    void LookUpOneRelation(Guid propertyId, out bool included, ref NodeDataWithRelations? value, ref bool? isSet);
    bool TryGetManyRelation(Guid propertyId, [MaybeNullWhen(false)] out NodeDataWithRelations[] value);
    bool TryGetOneRelation(Guid propertyId, out NodeDataWithRelations? value);
    bool TryGetReference(Guid propertyId, [MaybeNullWhen(false)] out NodeDataWithRelations reference);
    bool TryGetReferences(Guid propertyId, [MaybeNullWhen(false)] out NodeDataWithRelations[] references);
    bool ContainsRelation(Guid propertyId);
}
public class EmptyRelations : IRelations { // Always empty relations ( for cache )
    public void AddManyRelation(Guid propertyId, NodeDataWithRelations[] manyRelation) => throw new Exception("Read only. ");
    public void AddOneRelation(Guid propertyId, NodeDataWithRelations oneRelation) => throw new Exception("Read only. ");
    public void AddReference(Guid propertyId, NodeDataWithRelations reference) => throw new Exception("Read only. ");
    public void AddReferences(Guid propertyId, NodeDataWithRelations[] references) => throw new Exception("Read only. ");
    public void SetNoRelation(Guid propertyId) => throw new Exception("Read only. ");
    public void LookUpOneRelation(Guid propertyId, out bool isIncluded, ref NodeDataWithRelations? value, ref bool? isSet) { isIncluded = false; }
    public bool ContainsRelation(Guid propertyId) => false;
    public bool TryGetOneRelation(Guid propertyId, out NodeDataWithRelations? value) { value = null; return false; }
    public bool TryGetManyRelation(Guid propertyId, [MaybeNullWhen(false)] out NodeDataWithRelations[] value) { value = null; return false; }
    public bool TryGetReference(Guid propertyId, [MaybeNullWhen(false)] out NodeDataWithRelations reference) { reference = null; return false; }
    public bool TryGetReferences(Guid propertyId, [MaybeNullWhen(false)] out NodeDataWithRelations[] references) { references = null; return false; }
}
public class Relations : IRelations {
    // made when first added to: every node of an include result has one of these, and most use one of the four or none
    Properties<NodeDataWithRelations[]>? _manyRelations;
    Properties<NodeDataWithRelations?>? _oneRelations;
    Properties<NodeDataWithRelations?>? _references;
    Properties<NodeDataWithRelations[]>? _manyReferences;
    public bool ContainsRelation(Guid propertyId) => (_oneRelations?.ContainsKey(propertyId) ?? false) || (_manyRelations?.ContainsKey(propertyId) ?? false)
        || (_references?.ContainsKey(propertyId) ?? false) || (_manyReferences?.ContainsKey(propertyId) ?? false);
    public void AddManyRelation(Guid propertyId, NodeDataWithRelations[] manyRelation) => (_manyRelations ??= new(1)).Add(propertyId, manyRelation);
    public void AddOneRelation(Guid propertyId, NodeDataWithRelations oneRelation) => (_oneRelations ??= new(1)).Add(propertyId, oneRelation);
    public void AddReference(Guid propertyId, NodeDataWithRelations reference) => (_references ??= new(1)).Add(propertyId, reference);
    public void AddReferences(Guid propertyId, NodeDataWithRelations[] references) => (_manyReferences ??= new(1)).Add(propertyId, references);
    public void SetNoRelation(Guid propertyId) => _oneRelations?.Add(propertyId, null);
    public void LookUpOneRelation(Guid propertyId, out bool isIncluded, ref NodeDataWithRelations? value, ref bool? isSet) {
        if (_oneRelations != null && _oneRelations.TryGetValue(propertyId, out var value1)) {
            value = value1;
            isIncluded = true;
            isSet = value1 != null;
        } else {
            isIncluded = false;
        }
    }
    public bool TryGetOneRelation(Guid propertyId, out NodeDataWithRelations? value) {
        if (_oneRelations != null) return _oneRelations.TryGetValue(propertyId, out value);
        value = null;
        return false;
    }
    public bool TryGetManyRelation(Guid propertyId, [MaybeNullWhen(false)] out NodeDataWithRelations[] value) {
        if (_manyRelations != null) return _manyRelations.TryGetValue(propertyId, out value);
        value = null;
        return false;
    }
    public bool TryGetReference(Guid propertyId, [MaybeNullWhen(false)] out NodeDataWithRelations value) {
        if (_references != null) return _references.TryGetValue(propertyId, out value);
        value = null;
        return false;
    }
    public bool TryGetReferences(Guid propertyId, [MaybeNullWhen(false)] out NodeDataWithRelations[] references) {
        if (_manyReferences != null) return _manyReferences.TryGetValue(propertyId, out references);
        references = null;
        return false;
    }
    internal void SwapNodeData(Dictionary<int, INodeDataExternal> dic) {
        if (_oneRelations != null) foreach (var kv in _oneRelations.Items) kv.Value?.SwapNodeData(dic);
        if (_manyRelations != null) foreach (var kv in _manyRelations.Items) {
            foreach (var v in kv.Value) v.SwapNodeData(dic);
        }
        if (_references != null) foreach (var kv in _references.Items) kv.Value?.SwapNodeData(dic);
        if (_manyReferences != null) foreach (var kv in _manyReferences.Items) {
            foreach (var v in kv.Value) v.SwapNodeData(dic);
        }
    }
}
