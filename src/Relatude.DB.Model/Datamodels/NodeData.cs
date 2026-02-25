using Relatude.DB.Common;
using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.Datamodels;

public enum NodeDataStorageVersions {
    Legacy0 = 0,
    Legacy1 = 1,
    NodeData = 2,
    RevisionContainer = 100,
    //WithMeta = 2, // Access, Revisions, Cultures, Versions etc.
    //WithRelations = 3, // due to serialization for transfer to db clients ( not for disk )
    //WithMinimalMeta = 4, // Access, NOT versions 
}
internal class NA : Exception {
    public NA() : base("Access to property is not relevant in this context. Internal error. ") { }
}
public interface INodeDataInner : INodeData {    
}
public interface INodeDataOuter : INodeData {
    NodeDataRevision CopyAsNodeDataRevision(Guid revisionId, RevisionType revisionType, INodeMeta meta);
}
public interface INodeData {
    Guid Id { get; set; }
    int __Id { get; set; }
    IdKey IdKey => new(Id, __Id);
    Guid NodeType { get; }
    INodeMeta? Meta { get; }
    DateTime ChangedUtc { get; }
    DateTime CreatedUtc { get; set; }
    IEnumerable<PropertyEntry<object>> Values { get; }
    bool ReadOnly { get; }
    IRelations Relations { get; }
    int ValueCount { get; }
    void Add(Guid propertyId, object value);
    void AddOrUpdate(Guid propertyId, object value);
    void RemoveIfPresent(Guid propertyId);
    bool Contains(Guid propertyId);
    void EnsureReadOnly();
    bool TryGetValue(Guid propertyId, [MaybeNullWhen(false)] out object value);
    public static int BaseSize = 1000;  // approximate min size of node data without properties for cache size estimation
}
public abstract class NodeDataAbstract : INodeData {  // permanently readonly once set to readonly, to ensure cached objects are immutable, Relations are alyways empty and can never be set
    readonly static EmptyRelations emptyRelations = new(); // Relations are alyways empty and can never be set
    bool _readOnly;
    int _id;
    Guid _guid;
    public Properties<object> _values;
    public NodeDataAbstract(Guid guid, int id, Guid nodeType,
        DateTime createdUtc, DateTime changedUtc,
        Properties<object> values) {
        _guid = guid;
        _id = id;
        NodeType = nodeType;
        CreatedUtc = createdUtc;
        ChangedUtc = changedUtc;
        _values = values;
    }
    public NodeDataAbstract(Guid guid, int id, Guid nodeType,
        DateTime createdUtc, DateTime changedUtc,
        Properties<object> values, INodeMeta? meta) {
        _guid = guid;
        _id = id;
        NodeType = nodeType;
        CreatedUtc = createdUtc;
        ChangedUtc = changedUtc;
        Meta = meta;
        _values = values;
    }
    public int __Id {
        get => _id;
        set {
            if (_id != 0) throw new Exception("ID can only be initialized once. ");
            _id = value;
        }
    }
    public Guid Id {
        get => _guid;
        set {
            if (_guid != Guid.Empty) throw new Exception("ID can only be initialized once. ");
            _guid = value;
        }
    }
    public Guid NodeType { get; }
    public virtual INodeMeta? Meta { get; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ChangedUtc { get; }
    public IEnumerable<PropertyEntry<object>> Values => _values.Items;
    public int ValueCount => _values.Count;
    public bool ReadOnly => _readOnly;
    public void Add(Guid propertyId, object value) {
        if (_readOnly) throw new Exception("Node data is readonly. ");
        _values.Add(propertyId, value);
    }
    public void AddOrUpdate(Guid propertyId, object value) {
        if (_readOnly) throw new Exception("Node data is readonly. ");
        _values[propertyId] = value;
    }
    public void RemoveIfPresent(Guid propertyId) {
        if (_readOnly) throw new Exception("Node data is readonly. ");
        _values.RemoveIfPresent(propertyId);
    }
    public bool TryGetValue(Guid propertyId, [MaybeNullWhen(false)] out object value) => _values.TryGetValue(propertyId, out value);
    public bool Contains(Guid propertyId) => _values.ContainsKey(propertyId);
    public void EnsureReadOnly() {
        if (!_readOnly) _readOnly = true;
    }
    public IRelations Relations => emptyRelations;
    public INodeDataInner Copy() => copyNodeData();
    public NodeData CopyWithNewNodeType(Guid nodeTypeId) {
        return new NodeData(Id, __Id, nodeTypeId,
            //CollectionId, LCID, DerivedFromLCID, ReadAccess, WriteAccess,
            CreatedUtc, ChangedUtc, new(_values));
    }
    public override string ToString() {
        return $"NodeData: {Id} {NodeType} {CreatedUtc} {ChangedUtc} {ValueCount}";
    }
}
public class NodeData : NodeDataAbstract, INodeDataInner, INodeDataOuter {
    public NodeData(Guid guid, int id, Guid nodeType,
        DateTime createdUtc, DateTime changedUtc,
        Properties<object> values) : base(guid, id, nodeType, createdUtc, changedUtc, values) {
    }
    public NodeDataRevision CopyAsNodeDataRevision(Guid revisionId, RevisionType revisionType, INodeMeta meta) {
        var rev = new NodeDataRevision(Id, __Id, NodeType, CreatedUtc, ChangedUtc, new(_values), revisionId, revisionType, meta);
        return rev;
    }
}
public class NodeDataOnlyId : INodeDataOuter { // readonly node data with possibility to add relations for use in "include" queries
    public NodeDataOnlyId(Guid gid) => _gid = gid;
    public NodeDataOnlyId(int id) => _id = id;
    Guid _gid;
    public Guid Id {
        get => _gid;
        set {
            if (_gid != Guid.Empty) throw new Exception("ID can only be initialized once. ");
            _gid = value;
        }
    }
    int _id;
    public int __Id { get => _id; set => throw new NA(); }
    public Guid NodeType => throw new NA();
    public INodeMeta? Meta => throw new NA();
    public DateTime CreatedUtc { get => throw new NA(); set => throw new NA(); }
    public DateTime ChangedUtc => throw new NA();
    public Guid CollectionId { get => throw new NA(); set => throw new NA(); }
    public Guid ReadAccess { get => throw new NA(); set => throw new NA(); }
    public Guid WriteAccess { get => throw new NA(); set => throw new NA(); }
    public IEnumerable<PropertyEntry<object>> Values => throw new NA();
    public NodeDataRevision CopyAsNodeDataRevision(Guid revisionId, RevisionType revisionType, INodeMeta meta) => throw new NA();
    public int ValueCount => throw new NA();
    public bool ReadOnly => throw new NA();
    public bool IsDerived => throw new NA();
    public bool IsReadOnly => throw new NA();
    public IRelations Relations => throw new NA();
    public void Add(Guid propertyId, object value) => throw new NA();
    public void AddOrUpdate(Guid propertyId, object value) => throw new NA();
    public void RemoveIfPresent(Guid propertyId) => throw new NA();
    public bool Contains(Guid propertyId) => throw new NA();
    public void EnsureReadOnly() => throw new NA();
    public bool TryGetValue(Guid propertyId, [MaybeNullWhen(false)] out object value) => throw new NA();
    public INodeDataInner Copy() => throw new NA();
    public override string ToString() => $"NodeDataOnlyId: {Id}";
    public virtual INodeDataInner CopyAndChangeMeta(INodeMeta? meta) {
        return new NodeData(Id, __Id, NodeType,
            //CollectionId, LCID, DerivedFromLCID, ReadAccess, WriteAccess,
            CreatedUtc, ChangedUtc, new(_values));
    }
    NodeData copyNodeData() {
        return new NodeData(Id, __Id, NodeType,
            //CollectionId, LCID, DerivedFromLCID, ReadAccess, WriteAccess,
            CreatedUtc, ChangedUtc, new(_values));
    }
}
public class NodeDataOnlyTypeAndId : INodeDataOuter { // readonly node data with possibility to add relations for use in "include" queries
    public NodeDataOnlyTypeAndId(int id, Guid typeId) {
        _id = id;
        _nodeType = typeId;
    }
    int _id;
    public int __Id { get => _id; set => throw new NA(); }
    Guid _nodeType;
    public Guid Id { get => throw new NA(); set => throw new NA(); }
    public Guid NodeType { get => _nodeType; set => throw new NA(); }
    public NodeDataRevision CopyAsNodeDataRevision(Guid revisionId, RevisionType revisionType, INodeMeta meta) => throw new NA();
    public INodeMeta? Meta => throw new NA();
    public DateTime CreatedUtc { get => throw new NA(); set => throw new NA(); }
    public DateTime ChangedUtc => throw new NA();
    public Guid CollectionId { get => throw new NA(); set => throw new NA(); }
    public Guid ReadAccess { get => throw new NA(); set => throw new NA(); }
    public Guid WriteAccess { get => throw new NA(); set => throw new NA(); }
    public IEnumerable<PropertyEntry<object>> Values => throw new NA();
    public int ValueCount => throw new NA();
    public bool ReadOnly => true;
    public bool IsDerived => throw new NA();
    public IRelations Relations => throw new NA();
    public void Add(Guid propertyId, object value) => throw new NA();
    public void AddOrUpdate(Guid propertyId, object value) => throw new NA();
    public void RemoveIfPresent(Guid propertyId) => throw new NA();
    public bool Contains(Guid propertyId) => throw new NA();
    public void EnsureReadOnly() => throw new NA();
    public bool TryGetValue(Guid propertyId, [MaybeNullWhen(false)] out object value) => throw new NA();
    public INodeDataInner Copy() => throw new NA();
    public override string ToString() => $"NodeDataOnlyTypeAndUId: {NodeType} {__Id}";
}
public class NodeDataOnlyTypeAndGuid : INodeDataOuter { // readonly node data with possibility to add relations for use in "include" queries
    public NodeDataOnlyTypeAndGuid(Guid id, Guid typeId) {
        _id = id;
        _nodeType = typeId;
    }
    Guid _id;
    public int __Id { get => throw new NA(); set => throw new NA(); }
    public NodeDataRevision CopyAsNodeDataRevision(Guid revisionId, RevisionType revisionType, INodeMeta meta) => throw new NA();
    Guid _nodeType;
    public Guid Id { get => _id; set => throw new NA(); }
    public Guid NodeType { get => _nodeType; set => throw new NA(); }
    public INodeMeta? Meta => throw new NA();
    public DateTime CreatedUtc { get => throw new NA(); set => throw new NA(); }
    public DateTime ChangedUtc => throw new NA();
    public Guid CollectionId { get => throw new NA(); set => throw new NA(); }
    public Guid ReadAccess { get => throw new NA(); set => throw new NA(); }
    public Guid WriteAccess { get => throw new NA(); set => throw new NA(); }
    public IEnumerable<PropertyEntry<object>> Values => throw new NA();
    public int ValueCount => throw new NA();
    public bool ReadOnly => true;
    public bool IsDerived => throw new NA();
    public IRelations Relations => throw new NA();
    public void Add(Guid propertyId, object value) => throw new NA();
    public void AddOrUpdate(Guid propertyId, object value) => throw new NA();
    public void RemoveIfPresent(Guid propertyId) => throw new NA();
    public bool Contains(Guid propertyId) => throw new NA();
    public void EnsureReadOnly() => throw new NA();
    public bool TryGetValue(Guid propertyId, [MaybeNullWhen(false)] out object value) => throw new NA();
    public INodeDataInner Copy() => throw new NA();
    public override string ToString() => $"NodeDataOnlyTypeAndUId: {NodeType} {_id}";
}
