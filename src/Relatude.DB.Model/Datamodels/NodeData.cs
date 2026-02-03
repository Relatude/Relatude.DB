using Relatude.DB.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using static System.Diagnostics.Activity;
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
public class NA : Exception {
    public NA() : base("Access to property is not relevant in this context. Internal error. ") { }
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
    //INodeData Copy();
    public static int BaseSize = 1000;  // approximate base size of node data without properties for cache size estimation
}
public class NodeData : INodeData {  // permanently readonly once set to readonly, to ensure cached objects are immutable, Relations are alyways empty and can never be set
    readonly static EmptyRelations emptyRelations = new(); // Relations are alyways empty and can never be set
    bool _readOnly;
    int _id;
    Guid _guid;
    Properties<object> _values;
    public NodeData(Guid guid, int id, Guid nodeType,
        DateTime createdUtc, DateTime changedUtc,
        Properties<object> values) {
        _guid = guid;
        _id = id;
        NodeType = nodeType;
        CreatedUtc = createdUtc;
        ChangedUtc = changedUtc;
        //_values = new(values);
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
    public virtual INodeMeta? Meta { get; private set; }
    public void _setMeta(INodeMeta meta) => Meta = meta;
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
public class NodeDataOnlyId : INodeData { // readonly node data with possibility to add relations for use in "include" queries
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
    public INodeData Copy() => throw new NA();
    public override string ToString() => $"NodeDataOnlyId: {Id}";
}
public class NodeDataOnlyTypeAndId : INodeData { // readonly node data with possibility to add relations for use in "include" queries
    public NodeDataOnlyTypeAndId(int id, Guid typeId) {
        _id = id;
        _nodeType = typeId;
    }
    int _id;
    public int __Id { get => _id; set => throw new NA(); }
    Guid _nodeType;
    public Guid Id { get => throw new NA(); set => throw new NA(); }
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
    public INodeData Copy() => throw new NA();
    public override string ToString() => $"NodeDataOnlyTypeAndUId: {NodeType} {__Id}";
}
public class NodeDataOnlyTypeAndGuid : INodeData { // readonly node data with possibility to add relations for use in "include" queries
    public NodeDataOnlyTypeAndGuid(Guid id, Guid typeId) {
        _id = id;
        _nodeType = typeId;
    }
    Guid _id;
    public int __Id { get => throw new NA(); set => throw new NA(); }
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
    public INodeData Copy() => throw new NA();
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
    public INodeMeta? Meta => _node.Meta;
    public bool IsComplex => false;
    public NodeData[] Versions => throw new Exception("Node has no versions. ");
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
    public bool TryGetValue<T>(Guid propertyId, [MaybeNullWhen(false)] out T value) => throw new NA();
    public INodeData Copy() => throw new NA();
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
// it is faster and uses less memory than a dictionary, when the number of items less than ~20
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
    public Properties(Properties<T> properties) {
        _size = properties._size;
        _values = new PropertyEntry<T>[_size];
        Array.Copy(properties._values, _values, _size);
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
    public void AddOrUpdate(Guid key, T value) {
        for (int i = 0; i < _size; i++) {
            if (_values[i].PropertyId == key) {
                _values[i] = new(key, value);
                return;
            }
        }
        Add(key, value);
    }
    public void RemoveIfPresent(Guid key) {
        for (int i = 0; i < _size; i++) {
            if (_values[i].PropertyId == key) {
                _values[i] = _values[--_size]; // move last item to this position
                return;
            }
        }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
    public IEnumerable<PropertyEntry<T>> Items {
        get {
            for (int i = 0; i < _size; i++) yield return _values[i];
        }
    }
    public int Count => _size;
    public T this[Guid key] {
        get {
            for (int i = 0; i < _size; i++) if (_values[i].PropertyId == key) return _values[i].Value;
            throw new KeyNotFoundException("Property ID " + key + " was not found. ");
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

//public class Properties<T> {
//    private Guid[] _keys;
//    private T[] _values;
//    private int _size;

//    public Properties(int sizeIndication) {
//        _size = 0;
//        // Optimization: Avoid 0-length arrays to skip initial resize logic
//        int initialCapacity = sizeIndication > 0 ? sizeIndication : 4;
//        _keys = new Guid[initialCapacity];
//        _values = new T[initialCapacity];
//    }

//    public Properties(Properties<T> properties) {
//        _size = properties._size;
//        _keys = new Guid[_size];
//        _values = new T[_size];
//        Array.Copy(properties._keys, _keys, _size);
//        Array.Copy(properties._values, _values, _size);
//    }

//    [MethodImpl(MethodImplOptions.AggressiveInlining)]
//    public bool TryGetValue(Guid key, [MaybeNullWhen(false)] out T value) {
//        // Cache _size in a local variable to help JIT with loop-invariant hoisting
//        int size = _size;
//        ReadOnlySpan<Guid> keys = _keys.AsSpan(0, size);

//        for (int i = 0; i < keys.Length; i++) {
//            if (keys[i] == key) {
//                value = _values[i];
//                return true;
//            }
//        }

//        value = default;
//        return false;
//    }

//    public void Add(Guid key, T v) {
//        if (_size == _keys.Length) {
//            // double the size (or start at 4)
//            int newSize = _keys.Length == 0 ? 4 : _keys.Length * 2;
//            Array.Resize(ref _keys, newSize);
//            Array.Resize(ref _values, newSize);
//        }
//        _keys[_size] = key;
//        _values[_size] = v;
//        _size++;
//    }

//    public bool ContainsKey(Guid key) {
//        ReadOnlySpan<Guid> keys = _keys.AsSpan(0, _size);
//        for (int i = 0; i < keys.Length; i++) {
//            if (keys[i] == key) return true;
//        }
//        return false;
//    }

//    public void AddOrUpdate(Guid key, T value) {
//        Span<Guid> keys = _keys.AsSpan(0, _size);
//        for (int i = 0; i < keys.Length; i++) {
//            if (keys[i] == key) {
//                _values[i] = value;
//                return;
//            }
//        }
//        Add(key, value);
//    }

//    public void RemoveIfPresent(Guid key) {
//        Span<Guid> keys = _keys.AsSpan(0, _size);
//        for (int i = 0; i < keys.Length; i++) {
//            if (keys[i] == key) {
//                int lastIdx = --_size;
//                // Move the last element into the hole (Order is not preserved)
//                _keys[i] = _keys[lastIdx];
//                _values[i] = _values[lastIdx];

//                // We must clear the last slot so the Garbage Collector can reclaim the object.
//                _keys[lastIdx] = default;
//                _values[lastIdx] = default!;
//                return;
//            }
//        }
//    }

//    public int Count => _size;

//    public T this[Guid key] {
//        get {
//            if (TryGetValue(key, out var value)) return value;
//            throw new KeyNotFoundException($"Property ID {key} was not found.");
//        }
//        set => AddOrUpdate(key, value);
//    }

//    // SIGNATURE PRESERVATION: Kept as IEnumerable to match your original API
//    public IEnumerable<PropertyEntry<T>> Items {
//        get {
//            for (int i = 0; i < _size; i++) {
//                yield return new PropertyEntry<T>(_keys[i], _values[i]);
//            }
//        }
//    }
//}





//public class Properties<T> {
//    Dictionary<Guid, T> _values;
//    public Properties(int sizeIndication) {
//        _values = new Dictionary<Guid, T>(sizeIndication);
//    }
//    public Properties(Properties<T> properties) {
//        //_values = new PropertyEntry<T>[_size];
//        //Array.Copy(properties._values, _values, _size);
//        _values = new Dictionary<Guid, T>(properties._values);
//    }
//    public void Add(Guid key, T v) {
//        _values.Add(key, v);
//    }
//    public bool ContainsKey(Guid key) {
//        return _values.ContainsKey(key);
//    }
//    public void AddOrUpdate(Guid key, T value) {
//        _values[key] = value;
//    }
//    public void RemoveIfPresent(Guid key) {
//        _values.Remove(key);
//    }
//    public bool TryGetValue(Guid key, [MaybeNullWhen(false)] out T value) {
//        return _values.TryGetValue(key, out value);
//    }
//    public IEnumerable<PropertyEntry<T>> Items {
//        get {
//            foreach (var kv in _values) yield return new PropertyEntry<T>(kv.Key, kv.Value);
//        }
//    }
//    public int Count => _values.Count;
//    public T this[Guid key] {
//        get {
//            return _values[key];
//        }
//        set {
//            _values[key] = value;
//        }
//    }
//}



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
