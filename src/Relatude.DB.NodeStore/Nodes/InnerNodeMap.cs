using Relatude.DB.Datamodels;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.Nodes;

interface IInnerNodes { }
interface IInnerNodesMap { }
public class InnerNodes<TValue> : InnerNodes<Guid, TValue>, IInnerNodes where TValue : notnull {
    public InnerNodes() { }
    public InnerNodes(Guid keyPropertyId, InnerNodeDataMap<Guid> nodeDataMap, NodeMapper mapper) : base(keyPropertyId, nodeDataMap, mapper) {
    }
}
public class InnerNodes<TKey, TValue> : IInnerNodesMap, IDictionary<TKey, TValue> where TKey : notnull
where TValue : notnull {
    InnerNodeDataMap<TKey>? _nodeDataMap;
    NodeMapper? _mapper;
    Dictionary<TKey, TValue>? _raw;
    ICollection<TKey>? _keys;
    ICollection<TValue>? _values;
    public InnerNodes() {
        _raw = [];
    }
    public InnerNodes(Guid keyPropertyId, InnerNodeDataMap<TKey> nodeDataMap, NodeMapper mapper) {
        _nodeDataMap = nodeDataMap;
        _mapper = mapper;
    }
    public TValue this[TKey key] {
        get {
            if (_nodeDataMap != null && _mapper != null) {
                var nodeData = _nodeDataMap[key];
                return (TValue)_mapper.CreateObjectFromNodeData(nodeData);
            } else if (_raw != null) {
                return _raw[key];
            } else {
                throw new InvalidOperationException("InnerNodes is not properly initialized. ");
            }
        }
        set {
            if (_nodeDataMap != null && _mapper != null) {
                var nodeData = _mapper.CreateNodeDataFromObject(value, null);
                if (nodeData is not NodeData nd) throw new ArgumentException("The value must be of type NodeData.");
                if (!EqualityComparer<TKey>.Default.Equals(_nodeDataMap.EvalKey(nd), key)) throw new ArgumentException("The provided key does not match the value key.", nameof(key));
                _nodeDataMap[key] = nd;
            } else if (_raw != null) {
                _raw[key] = value;
            } else {
                throw new InvalidOperationException("InnerNodes is not properly initialized. ");
            }
        }
    }
    public ICollection<TKey> Keys => _keys ??= new KeyCollection(this);
    public ICollection<TValue> Values => _values ??= new ValueCollection(this);
    public int Count => _raw?.Count ?? _nodeDataMap?.Count ?? 0;
    public bool IsReadOnly => false;
    public void Add(TKey key, TValue value) {
        if (_raw != null) {
            _raw.Add(key, value);
            return;
        } else if (_nodeDataMap != null && _mapper != null) {
            var data = _mapper.CreateNodeDataFromObject(value, null);
            if (data is not NodeData nodeData) throw new ArgumentException("The value must be of type NodeData.");
            if (!EqualityComparer<TKey>.Default.Equals(_nodeDataMap.EvalKey(nodeData), key)) throw new ArgumentException("The provided key does not match the value key.", nameof(key));
            _nodeDataMap.Add(nodeData);
        } else {
            throw new InvalidOperationException("InnerNodes is not properly initialized. ");
        }
    }
    public void Add(KeyValuePair<TKey, TValue> item) {
        if (_raw != null) {
            _raw.Add(item.Key, item.Value);
        } else if (_nodeDataMap != null && _mapper != null) {
            var data = _mapper.CreateNodeDataFromObject(item.Value, null);
            if (data is not NodeData nodeData) throw new ArgumentException("The value must be of type NodeData.");
            if (!EqualityComparer<TKey>.Default.Equals(_nodeDataMap.EvalKey(nodeData), item.Key)) throw new ArgumentException("The provided key does not match the value key.", nameof(item));
            _nodeDataMap.Add(nodeData);
        } else {
            throw new InvalidOperationException("InnerNodes is not properly initialized. ");
        }
    }
    public void Clear() {
        if (_raw != null) {
            _raw.Clear();
        } else if (_nodeDataMap != null) {
            _nodeDataMap.Clear();
        } else {
            throw new InvalidOperationException("InnerNodes is not properly initialized. ");
        }
    }
    public bool Contains(KeyValuePair<TKey, TValue> item) {
        return TryGetValue(item.Key, out var value) && EqualityComparer<TValue>.Default.Equals(value, item.Value);
    }
    public bool ContainsKey(TKey key) {
        if (_raw != null) {
            return _raw.ContainsKey(key);
        } else if (_nodeDataMap != null) {
            return _nodeDataMap.ContainsKey(key);
        } else {
            throw new InvalidOperationException("InnerNodes is not properly initialized. ");
        }
    }
    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) {
        if (_raw != null) {
            ((ICollection<KeyValuePair<TKey, TValue>>)_raw).CopyTo(array, arrayIndex);
        } else if (_nodeDataMap != null && _mapper != null) {
            ArgumentNullException.ThrowIfNull(array);
            if ((uint)arrayIndex > (uint)array.Length) throw new ArgumentOutOfRangeException(nameof(arrayIndex));
            if (array.Length - arrayIndex < Count) throw new ArgumentException("Destination array is not long enough.", nameof(array));
            int i = arrayIndex;
            foreach (var node in _nodeDataMap) {
                var key = _nodeDataMap.EvalKey(node);
                var value = (TValue)_mapper.CreateObjectFromNodeData(node);
                array[i++] = new KeyValuePair<TKey, TValue>(key, value);
            }
        } else {
            throw new InvalidOperationException("InnerNodes is not properly initialized. ");
        }
    }
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() {
        if (_raw != null) {
            foreach (var kvp in _raw) yield return kvp;
        } else if (_nodeDataMap != null && _mapper != null) {
            foreach (var node in _nodeDataMap) {
                var key = _nodeDataMap.EvalKey(node);
                var value = (TValue)_mapper.CreateObjectFromNodeData(node);
                yield return new KeyValuePair<TKey, TValue>(key, value);
            }
        } else {
            throw new InvalidOperationException("InnerNodes is not properly initialized. ");
        }
    }
    public bool Remove(TKey key) {
        if (_raw != null) {
            return _raw.Remove(key);
        } else if (_nodeDataMap != null) {
            return _nodeDataMap.Remove(key);
        } else {
            throw new InvalidOperationException("InnerNodes is not properly initialized. ");
        }
    }
    public bool Remove(KeyValuePair<TKey, TValue> item) {
        if (_raw != null) {
            return ((ICollection<KeyValuePair<TKey, TValue>>)_raw).Remove(item);
        } else if (_nodeDataMap != null && _mapper != null) {
            return TryGetValue(item.Key, out var value) && EqualityComparer<TValue>.Default.Equals(value, item.Value) && _nodeDataMap.Remove(item.Key);
        } else {
            throw new InvalidOperationException("InnerNodes is not properly initialized. ");
        }
    }
    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) {
        if (_raw != null) {
            return _raw.TryGetValue(key, out value);
        } else if (_nodeDataMap != null && _mapper != null) {
            if (_nodeDataMap.TryGetValue(key, out var node)) {
                value = (TValue)_mapper.CreateObjectFromNodeData(node);
                return true;
            }
            value = default;
            return false;
        } else {
            throw new InvalidOperationException("InnerNodes is not properly initialized. ");
        }
    }
    IEnumerator IEnumerable.GetEnumerator() {
        return GetEnumerator();
    }
    public InnerNodeDataMap<TKey> GetNodeDataMap(Guid keyPropertyId, NodeMapper mapper) {
        if (_raw != null) {
            var nodeDatas = _raw.Select(r => {
                if (mapper.CreateNodeDataFromObject(r, null) is not NodeData nd)
                    throw new ArgumentException("The Inner nodes must be of type NodeData and not use revisions etc.");
                return nd;
            }).ToList();
            var nodeDataMap = new InnerNodeDataMap<TKey>(keyPropertyId, nodeDatas);
            return nodeDataMap;
        } else if (_nodeDataMap != null) {
            return _nodeDataMap;
        } else {
            throw new InvalidOperationException("InnerNodes is not properly initialized. ");
        }
    }
    sealed class KeyCollection(InnerNodes<TKey, TValue> owner) : ICollection<TKey> {
        public int Count => owner._raw?.Count ?? owner._nodeDataMap?.Count ?? 0;
        public bool IsReadOnly => true;
        public void Add(TKey item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(TKey item) => owner._raw?.ContainsKey(item) ?? owner._nodeDataMap?.ContainsKey(item) ?? false;
        public void CopyTo(TKey[] array, int arrayIndex) {
            ArgumentNullException.ThrowIfNull(array);
            if ((uint)arrayIndex > (uint)array.Length) throw new ArgumentOutOfRangeException(nameof(arrayIndex));
            if (array.Length - arrayIndex < Count) throw new ArgumentException("Destination array is not long enough.", nameof(array));
            int i = arrayIndex;
            if (owner._raw != null) foreach (var key in owner._raw.Keys) array[i++] = key;
            else if (owner._nodeDataMap != null) foreach (var node in owner._nodeDataMap) array[i++] = owner._nodeDataMap.EvalKey(node);
            else throw new InvalidOperationException("InnerNodes is not properly initialized. ");
        }
        public IEnumerator<TKey> GetEnumerator() {
            if (owner._raw != null) return owner._raw.Keys.GetEnumerator();
            if (owner._nodeDataMap != null) return GetFromNodeDataMap().GetEnumerator();
            throw new InvalidOperationException("InnerNodes is not properly initialized. ");
        }
        IEnumerable<TKey> GetFromNodeDataMap() { foreach (var node in owner._nodeDataMap!) yield return owner._nodeDataMap.EvalKey(node); }
        public bool Remove(TKey item) => throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    sealed class ValueCollection(InnerNodes<TKey, TValue> owner) : ICollection<TValue> {
        public int Count => owner._raw?.Count ?? owner._nodeDataMap?.Count ?? 0;
        public bool IsReadOnly => true;
        public void Add(TValue item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(TValue item) {
            if (owner._raw != null) return owner._raw.Values.Contains(item);
            if (owner._nodeDataMap != null && owner._mapper != null) {
                var c = EqualityComparer<TValue>.Default;
                foreach (var node in owner._nodeDataMap) if (c.Equals((TValue)owner._mapper.CreateObjectFromNodeData(node), item)) return true;
            } else if (owner._raw == null && owner._nodeDataMap == null) {
                throw new InvalidOperationException("InnerNodes is not properly initialized. ");
            }
            return false;
        }
        public void CopyTo(TValue[] array, int arrayIndex) {
            ArgumentNullException.ThrowIfNull(array);
            if ((uint)arrayIndex > (uint)array.Length) throw new ArgumentOutOfRangeException(nameof(arrayIndex));
            if (array.Length - arrayIndex < Count) throw new ArgumentException("Destination array is not long enough.", nameof(array));
            int i = arrayIndex;
            if (owner._raw != null) foreach (var value in owner._raw.Values) array[i++] = value;
            else if (owner._nodeDataMap != null && owner._mapper != null) foreach (var node in owner._nodeDataMap) array[i++] = (TValue)owner._mapper.CreateObjectFromNodeData(node);
            else throw new InvalidOperationException("InnerNodes is not properly initialized. ");
        }
        public IEnumerator<TValue> GetEnumerator() {
            if (owner._raw != null) return owner._raw.Values.GetEnumerator();
            if (owner._nodeDataMap != null && owner._mapper != null) return GetFromNodeDataMap().GetEnumerator();
            throw new InvalidOperationException("InnerNodes is not properly initialized. ");
        }
        IEnumerable<TValue> GetFromNodeDataMap() { foreach (var node in owner._nodeDataMap!) yield return (TValue)owner._mapper!.CreateObjectFromNodeData(node); }
        public bool Remove(TValue item) => throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
