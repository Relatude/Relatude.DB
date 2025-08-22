using System.Diagnostics.CodeAnalysis;
using WAF.Common;
namespace WAF.Datamodels;
[Flags]
public enum NodeDataVersionFlag {
    Minimal = 1,
    Access = 2,
    Revisions = 4,
    Cultures = 8,
}
public interface INodeData_NoNodeType {
}
public interface INodeData_OnlyIds {
}
public interface INodeData {
    Guid Id { get; set; }
    int __Id { get; set; }
    IdKey IdKey => new(Id, __Id);
    Guid NodeType { get; }
    //Guid CollectionId { get; }
    //int LCID { get; }
    //int DerivedFromLCID { get; set; }
    ////int Revision { get; } // 0: live, -1: preliminary, 1: 
    //Guid ReadAccess { get; }
    //Guid WriteAccess { get; }
    DateTime ChangedUtc { get; }
    DateTime CreatedUtc { get; set; }
    IEnumerable<PropertyEntry<object>> Values { get; }

    bool ReadOnly { get; }
    //bool IsDerived { get; }
    IRelations Relations { get; }
    int ValueCount { get; }
    void Add(Guid propertyId, object value);
    void AddOrUpdate(Guid propertyId, object value);
    void RemoveIfPresent(Guid propertyId);
    bool Contains(Guid propertyId);
    void EnsureReadOnly();
    bool TryGetValue(Guid propertyId, [MaybeNullWhen(false)] out object value);
    INodeData Copy();

    public static int BaseSize = 1000;
}
public class NodeData : INodeData {  // permanently readonly once set to readonly, to ensure cached objects are immutable, Relations are alyways empty and can never be set
    readonly static EmptyRelations emptyRelations = new(); // Relations are alyways empty and can never be set
    bool _readOnly;
    int _uid;
    Guid _gid;
    Properties<object> _values; // experiment to see if this is faster than Dictionary
    //Dictionary<Guid, object> _values;
    static public NodeData CreateEmptyDerivedNode(Guid id, int lcid) {
        throw new NotImplementedException();
        //if (lcid == 0) throw new Exception("Culture ID cannot be 0. ");
        //// updating with a derived node will cause removal of culture
        //var e = Guid.Empty;
        //var now = DateTime.UtcNow;
        //return new NodeData(id, 0, e, e, lcid, lcid, e, e, now, now, new(0));
    }
    public NodeData(Guid id, int uid, Guid nodeType,
        //Guid collectionId, int lcid, int derivedFromLcidId, Guid readAccess, Guid writeAccess, 
        DateTime createdUtc, DateTime changedUtc,
        Properties<object> values) {
        _gid = id;
        _uid = uid;
        NodeType = nodeType;
        //CollectionId = collectionId;
        //LCID = lcid;
        //DerivedFromLCID = derivedFromLcidId;
        //ReadAccess = readAccess;
        //WriteAccess = writeAccess;
        CreatedUtc = createdUtc;
        ChangedUtc = changedUtc;
        //_values = new(values);
        _values = values;
    }
    public int __Id {
        get => _uid;
        set {
            if (_uid != 0) throw new Exception("ID can only be initialized once. ");
            _uid = value;
        }
    }
    public Guid Id {
        get => _gid;
        set {
            if (_gid != Guid.Empty) throw new Exception("ID can only be initialized once. ");
            _gid = value;
        }
    }
    //public Guid CollectionId { get; set; }
    //public int LCID { get; }
    // public bool IsDerived { get => LCID != 0; }
    //public int DerivedFromLCID { get; set; }
    //public Guid ReadAccess { get; }
    //public Guid WriteAccess { get; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ChangedUtc { get; }
    public Guid NodeType { get; }
    public IEnumerable<PropertyEntry<object>> Values => _values.Items;
    //public IEnumerable<KeyValuePair<Guid, object>> Values {
    //    get {
    //        foreach (var v in _values) yield return v;
    //    }
    //}
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
        _values.Remove(propertyId);
    }
    public bool TryGetValue(Guid propertyId, [MaybeNullWhen(false)] out object value) {
        return _values.TryGetValue(propertyId, out value);
    }
    public bool Contains(Guid propertyId) => _values.ContainsKey(propertyId);

    public void EnsureReadOnly() {
        if (!_readOnly) _readOnly = true;
    }
    public IRelations Relations => emptyRelations;
    public INodeData Copy() {
        return new NodeData(Id, __Id, NodeType,
            //CollectionId, LCID, DerivedFromLCID, ReadAccess, WriteAccess,
            CreatedUtc, ChangedUtc, new(_values));
    }
    public INodeData CopyWithNewNodeType(Guid nodeTypeId) {
        return new NodeData(Id, __Id, nodeTypeId,
            //CollectionId, LCID, DerivedFromLCID, ReadAccess, WriteAccess,
            CreatedUtc, ChangedUtc, new(_values));
    }
    public override string ToString() {
        return $"NodeData: {Id} {NodeType} {CreatedUtc} {ChangedUtc} {ValueCount}";
    }
}
public class NodeDataOnlyId : INodeData, INodeData_NoNodeType, INodeData_OnlyIds { // readonly node data with possibility to add relations for use in "include" queries
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
    public int __Id { get => _id; set => throw new NotImplementedException(); }
    public Guid NodeType => throw new NotImplementedException();
    public Guid CollectionId { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public int LCID { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public int DerivedFromLCID { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public Guid ReadAccess => throw new NotImplementedException();
    public Guid WriteAccess => throw new NotImplementedException();
    public DateTime CreatedUtc { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public DateTime ChangedUtc => throw new NotImplementedException();
    public IEnumerable<PropertyEntry<object>> Values => throw new NotImplementedException();

    public int ValueCount => throw new NotImplementedException();
    public bool ReadOnly => throw new NotImplementedException();
    public bool IsDerived => throw new NotImplementedException();
    public IRelations Relations => throw new NotImplementedException();

    public void Add(Guid propertyId, object value) => throw new NotImplementedException();
    public void AddOrUpdate(Guid propertyId, object value) => throw new NotImplementedException();
    public void RemoveIfPresent(Guid propertyId) => throw new NotImplementedException();
    public bool Contains(Guid propertyId) => throw new NotImplementedException();
    public void EnsureReadOnly() => throw new NotImplementedException();
    public bool TryGetValue(Guid propertyId, [MaybeNullWhen(false)] out object value) => throw new NotImplementedException();
    public INodeData Copy() => throw new NotImplementedException();
    public override string ToString() => $"NodeDataOnlyId: {Id}";
}
public class NodeDataOnlyTypeAndUId : INodeData, INodeData_OnlyIds { // readonly node data with possibility to add relations for use in "include" queries
    public NodeDataOnlyTypeAndUId(int id, Guid typeId) {
        _id = id;
        _nodeType = typeId;
    }
    int _id;
    public int __Id { get => _id; set => throw new NotImplementedException(); }
    Guid _nodeType;
    public Guid Id { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public Guid NodeType { get => _nodeType; set => throw new NotImplementedException(); }
    public Guid CollectionId { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public int LCID { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public int DerivedFromLCID { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public Guid ReadAccess => throw new NotImplementedException();
    public Guid WriteAccess => throw new NotImplementedException();
    public DateTime CreatedUtc { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public DateTime ChangedUtc => throw new NotImplementedException();
    public IEnumerable<PropertyEntry<object>> Values => throw new NotImplementedException();

    public int ValueCount => throw new NotImplementedException();
    public bool ReadOnly => true;
    public bool IsDerived => throw new NotImplementedException();
    public IRelations Relations => throw new NotImplementedException();

    public void Add(Guid propertyId, object value) => throw new NotImplementedException();
    public void AddOrUpdate(Guid propertyId, object value) => throw new NotImplementedException();
    public void RemoveIfPresent(Guid propertyId) => throw new NotImplementedException();
    public bool Contains(Guid propertyId) => throw new NotImplementedException();
    public void EnsureReadOnly() => throw new NotImplementedException();
    public bool TryGetValue(Guid propertyId, [MaybeNullWhen(false)] out object value) => throw new NotImplementedException();
    public INodeData Copy() => throw new NotImplementedException();
    public override string ToString() => $"NodeDataOnlyTypeAndUId: {NodeType} {__Id}";
}
public class NodeDataOnlyTypeAndId : INodeData, INodeData_OnlyIds { // readonly node data with possibility to add relations for use in "include" queries
    public NodeDataOnlyTypeAndId(Guid id, Guid typeId) {
        _id = id;
        _nodeType = typeId;
    }
    Guid _id;
    public int __Id { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    Guid _nodeType;
    public Guid Id { get => _id; set => throw new NotImplementedException(); }
    public Guid NodeType { get => _nodeType; set => throw new NotImplementedException(); }
    public Guid CollectionId { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public int LCID { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public int DerivedFromLCID { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public Guid ReadAccess => throw new NotImplementedException();
    public Guid WriteAccess => throw new NotImplementedException();
    public DateTime CreatedUtc { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public DateTime ChangedUtc => throw new NotImplementedException();
    public IEnumerable<PropertyEntry<object>> Values => throw new NotImplementedException();

    public int ValueCount => throw new NotImplementedException();
    public bool ReadOnly => true;
    public bool IsDerived => throw new NotImplementedException();
    public IRelations Relations => throw new NotImplementedException();

    public void Add(Guid propertyId, object value) => throw new NotImplementedException();
    public void AddOrUpdate(Guid propertyId, object value) => throw new NotImplementedException();
    public void RemoveIfPresent(Guid propertyId) => throw new NotImplementedException();
    public bool Contains(Guid propertyId) => throw new NotImplementedException();
    public void EnsureReadOnly() => throw new NotImplementedException();
    public bool TryGetValue(Guid propertyId, [MaybeNullWhen(false)] out object value) => throw new NotImplementedException();
    public INodeData Copy() => throw new NotImplementedException();
    public override string ToString() => $"NodeDataOnlyTypeAndUId: {NodeType} {_id}";
}
public class NodeDataWithRelations : INodeData { // readonly node data with possibility to add relations for use in "include" queries
    INodeData _node;
    Relations _relations;
    static void throwReadOnlyError() => throw new Exception("Internal error. Should only be created with readonly inner node data. ");
    public NodeDataWithRelations(INodeData nodeData) {
        if (!nodeData.ReadOnly) throwReadOnlyError();
        _node = nodeData;
        _relations = new();
    }
    public void SwapNodeData(Dictionary<int, INodeData> dic) {
        _node = dic[_node.__Id];
        _relations.SwapNodeData(dic);
    }
    public Guid Id { get => _node.Id; set => throwReadOnlyError(); }
    public int __Id { get => _node.__Id; set => throwReadOnlyError(); }
    public Guid NodeType => _node.NodeType;
    //public Guid CollectionId { get => _node.CollectionId; set => throwReadOnlyError(); }
    //public int LCID { get => _node.LCID; set => throwReadOnlyError(); }
    //public int DerivedFromLCID { get => _node.DerivedFromLCID; set => throwReadOnlyError(); }
    //public Guid ReadAccess => _node.ReadAccess;
    //public Guid WriteAccess => _node.WriteAccess;
    public DateTime CreatedUtc { get => _node.CreatedUtc; set => throwReadOnlyError(); }
    public DateTime ChangedUtc => _node.ChangedUtc;
    public IEnumerable<PropertyEntry<object>> Values => _node.Values;

    public int ValueCount => _node.ValueCount;
    public bool ReadOnly => _node.ReadOnly;
    //public bool IsDerived => _node.IsDerived;
    public IRelations Relations => _relations;

    public void Add(Guid propertyId, object value) => throwReadOnlyError();
    public void AddOrUpdate(Guid propertyId, object value) => throwReadOnlyError();
    public void RemoveIfPresent(Guid propertyId) => throwReadOnlyError();
    public bool Contains(Guid propertyId) => _node.Contains(propertyId);
    public void EnsureReadOnly() => _node.EnsureReadOnly();
    public bool TryGetValue(Guid propertyId, [MaybeNullWhen(false)] out object value) => _node.TryGetValue(propertyId, out value);
    public INodeData Copy() => throw new NotImplementedException();
    public override string ToString() => $"NodeDataWithRelations: {Id} {NodeType} {CreatedUtc} {ChangedUtc} {ValueCount}";
}
public interface IRelations {
    void AddManyRelation(Guid propertyId, NodeDataWithRelations[] manyRelation);
    void AddOneRelation(Guid propertyId, NodeDataWithRelations oneRelation);
    void SetNoRelation(Guid propertyId);
    void LookUpOneRelation(Guid propertyId, out bool included, ref NodeDataWithRelations? value, ref bool? isSet);
    bool TryGetManyRelation(Guid propertyId, [MaybeNullWhen(false)] out NodeDataWithRelations[] value);
    bool TryGetOneRelation(Guid propertyId, out NodeDataWithRelations? value);
    bool ContainsRelation(Guid propertyId);
}
public class EmptyRelations : IRelations { // Always empty relations ( for cache )
    public void AddManyRelation(Guid propertyId, NodeDataWithRelations[] manyRelation) => throw new Exception("Read only. ");
    public void AddOneRelation(Guid propertyId, NodeDataWithRelations oneRelation) => throw new Exception("Read only. ");
    public void SetNoRelation(Guid propertyId) => throw new Exception("Read only. ");
    public void LookUpOneRelation(Guid propertyId, out bool isIncluded, ref NodeDataWithRelations? value, ref bool? isSet) { isIncluded = false; }
    public bool ContainsRelation(Guid propertyId) => false;
    public bool TryGetOneRelation(Guid propertyId, out NodeDataWithRelations? value) { value = null; return false; }
    public bool TryGetManyRelation(Guid propertyId, [MaybeNullWhen(false)] out NodeDataWithRelations[] value) { value = null; return false; }
}
// number of properties will be relatively small, so we can use a simple array
// it is faster and uses less memory than a dictionary
public struct PropertyEntry<T>(Guid key, T value) {
    public Guid PropertyId = key;
    public T Value = value;
}
public class Properties<T> {
    PropertyEntry<T>[] _values;
    int _size;
    public Properties(int sizeIndication) {
        _size = 0;
        _values = new PropertyEntry<T>[sizeIndication];
    }
    public Properties(Properties<T> tinyDictionary) {
        _size = tinyDictionary._size;
        _values = new PropertyEntry<T>[_size];
        Array.Copy(tinyDictionary._values, _values, _size);
    }
    public void Add(Guid key, T v) {
        if (_size == _values.Length) {
            var increase = _values.Length == 0 ? 1 : _values.Length;
            Array.Resize(ref _values, _values.Length + increase); // double the size ( like lists )
        }
        _values[_size++] = new(key, v);
    }
    public bool ContainsKey(Guid key) {
        for (int i = 0; i < _size; i++) if (_values[i].PropertyId == key) return true;
        return false;
    }
    public bool TryGetValue(Guid key, [MaybeNullWhen(false)] out T value) {
        for (int i = 0; i < _size; i++) {
            if (_values[i].PropertyId == key) {
                value = _values[i].Value;
                return true;
            }
        }
        value = default;
        return false;
    }
    internal void Remove(Guid propertyId) {
        // we do not care about the order of the items
        for (int i = 0; i < _size; i++) {
            if (_values[i].PropertyId == propertyId) {
                _values[i] = _values[--_size]; // move last item to this position
                return;
            }
        }
    }
    public IEnumerable<PropertyEntry<T>> Items {
        get {
            for (int i = 0; i < _size; i++) yield return _values[i];
        }
    }
    public int Count => _size;
    public T this[Guid key] {
        get {
            for (int i = 0; i < _size; i++) if (_values[i].PropertyId == key) return _values[i].Value;
            throw new KeyNotFoundException();
        }
        set {
            for (int i = 0; i < _size; i++) {
                if (_values[i].PropertyId == key) {
                    _values[i] = new(key, value);
                    return;
                }
            }
            Add(key, value);
        }
    }
}
public class Relations : IRelations {
    readonly Properties<NodeDataWithRelations[]> _manyRelations = new(0);
    readonly Properties<NodeDataWithRelations?> _oneRelations = new(0);
    public bool ContainsRelation(Guid propertyId) => _oneRelations.ContainsKey(propertyId) || _manyRelations.ContainsKey(propertyId);
    public void AddManyRelation(Guid propertyId, NodeDataWithRelations[] manyRelation) => _manyRelations.Add(propertyId, manyRelation);
    public void AddOneRelation(Guid propertyId, NodeDataWithRelations oneRelation) => _oneRelations.Add(propertyId, oneRelation);
    public void SetNoRelation(Guid propertyId) => _oneRelations.Add(propertyId, null);
    public void LookUpOneRelation(Guid propertyId, out bool isIncluded, ref NodeDataWithRelations? value, ref bool? isSet) {
        if (_oneRelations.TryGetValue(propertyId, out var value1)) {
            value = value1;
            isIncluded = true;
            isSet = value1 != null;
        } else {
            isIncluded = false;
        }
    }
    public bool TryGetOneRelation(Guid propertyId, out NodeDataWithRelations? value) => _oneRelations.TryGetValue(propertyId, out value);
    public bool TryGetManyRelation(Guid propertyId, [MaybeNullWhen(false)] out NodeDataWithRelations[] value) => _manyRelations.TryGetValue(propertyId, out value);
    internal void SwapNodeData(Dictionary<int, INodeData> dic) {
        foreach (var kv in _oneRelations.Items) kv.Value?.SwapNodeData(dic);
        foreach (var kv in _manyRelations.Items) {
            foreach (var v in kv.Value) v.SwapNodeData(dic);
        }
    }
}
