using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;

namespace Relatude.DB.Datamodels;

public interface IInnerNodeDataMap : IEnumerable<NodeData> {
    int Count { get; }
}
// Thread-safe for concurrent reads. Not writes.
public class InnerNodeDataMap<TKey> : IInnerNodeDataMap where TKey : notnull {
    public static Guid PropertyIdNodeGuidId = Guid.Empty;
    public static Guid PropertyIdNodeIntId = Guid.Parse("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF");
    List<NodeData?> _nodes; // null means the node has been removed, but we keep the slot to avoid shifting indices of subsequent items
    Dictionary<TKey, int>? __indexByKey;
    Dictionary<TKey, int> getIdx() { // measure to delay or avoid building the index if not needed
        lock (_nodes) {
            if (__indexByKey == null) {
                __indexByKey = new(_nodes.Count);
                for (int i = 0; i < _nodes.Count; i++) if (_nodes[i] is NodeData node) __indexByKey.Add(EvalKey(node), i);
            }
            return __indexByKey;
        }
    }
    Guid _keyPropertyId;
    public InnerNodeDataMap(Guid keyPropertyId, ICollection<NodeData> nodes) {
        //if (keyPropertyId == PropertyIdNodeGuidId && typeof(TKey) != typeof(Guid)) {
        //    throw new ArgumentException("Key property ID cannot be empty when key type is not Guid. ");
        //}
        //if (keyPropertyId == PropertyIdNodeIntId && typeof(TKey) != typeof(int)) {
        //    throw new ArgumentException("Key property ID cannot be empty when key type is not int. ");
        //}
        _keyPropertyId = keyPropertyId;
        _nodes = [.. nodes];
    }
    public TKey EvalKey(NodeData node) {
        if (_keyPropertyId == PropertyIdNodeGuidId) {
            return (TKey)(object)node.Id;
        } else if (_keyPropertyId == PropertyIdNodeIntId) {
            return (TKey)(object)node.__Id;
        } else if (node.TryGetValue(_keyPropertyId, out var oKey)) {
            if (oKey is TKey tKey) {
                return tKey;
            } else {
                throw new ArgumentException($"Node with ID {node.Id} has a value for the key property with ID {_keyPropertyId}, but it is not of type {typeof(TKey).FullName}. ");
            }
        } else {
            throw new ArgumentException($"Node with ID {node.Id} does not have a value for the key property with ID {_keyPropertyId}. ");
        }
    }
    public bool TryEvalKey(NodeData node, [MaybeNullWhen(false)] out TKey key) {
        if (_keyPropertyId == PropertyIdNodeGuidId) {
            key = (TKey)(object)node.Id;
            return true;
        } else if (_keyPropertyId == PropertyIdNodeIntId) {
            key = (TKey)(object)node.__Id;
            return true;
        } else if (node.TryGetValue(_keyPropertyId, out var oKey)) {
            if (oKey is TKey tKey) {
                key = tKey;
                return true;
            }
        }
        key = default;
        return false;
    }
    public void AddToTop(NodeData node) {
        var key = EvalKey(node);
        _nodes.Insert(0, node);
        var indexByKey = getIdx(); // Reduce calls to the property getter
        // Update indices of all existing items
        for (int i = 1; i < _nodes.Count; i++) {
            if (_nodes[i] is NodeData shifted) indexByKey[EvalKey(shifted)] = i;
        }
        indexByKey[key] = 0;
    }
    public void Add(NodeData node) {
        var key = EvalKey(node);
        var indexByKey = getIdx();
        indexByKey.Add(key, _nodes.Count);
        _nodes.Add(node);
    }
    public bool TryAdd(NodeData node) {
        if (TryEvalKey(node, out var key)) {
            var indexByKey = getIdx(); // Reduce calls to the property getter
            if (indexByKey.ContainsKey(key)) return false;
            _nodes.Add(node);
            indexByKey[key] = _nodes.Count - 1;
            return true;
        }
        return false;
    }
    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out NodeData node) {
        if (getIdx().TryGetValue(key, out var index)) {
            node = _nodes[index];
            if (node == null) throw new InvalidOperationException("Internal error: Node is null. This should not happen. ");
            return true;
        }
        node = null;
        return false;
    }
    public bool Remove(TKey key) {
        var indexByKey = getIdx();
        if (indexByKey.TryGetValue(key, out var index)) {
            _nodes[index] = null;
            indexByKey.Remove(key);
            return true;
        } else {
            return false;
        }
    }
    public void Move(TKey key, int newIndex) {
        var indexByKey = getIdx(); // Reduce calls to the property getter
        if (!indexByKey.TryGetValue(key, out int oldIndex)) throw new KeyNotFoundException($"The given key '{key}' was not present in the map. ");
        if ((uint)newIndex >= (uint)_nodes.Count) throw new ArgumentOutOfRangeException(nameof(newIndex));
        if (oldIndex == newIndex) return;
        var node = _nodes[oldIndex] ?? throw new InvalidOperationException("Internal error: Node is null. This should not happen. ");
        if (oldIndex < newIndex) {
            for (int i = oldIndex; i < newIndex; i++) {
                _nodes[i] = _nodes[i + 1];
                if (_nodes[i] is NodeData shifted) indexByKey[EvalKey(shifted)] = i;
            }
        } else {
            for (int i = oldIndex; i > newIndex; i--) {
                _nodes[i] = _nodes[i - 1];
                if (_nodes[i] is NodeData shifted) indexByKey[EvalKey(shifted)] = i;
            }
        }
        _nodes[newIndex] = node;
        indexByKey[key] = newIndex;
    }
    public void MoveRelative(TKey key, int offset) {
        if (!getIdx().TryGetValue(key, out int index)) throw new KeyNotFoundException($"The given key '{key}' was not present in the map. ");
        long target = (long)index + offset;
        if (target < 0) target = 0;
        else if (target >= _nodes.Count) target = _nodes.Count - 1;
        Move(key, (int)target);
    }
    public int Count => getIdx().Count;
    public bool IsReadOnly => false;
    public NodeData this[TKey key] {
        get {
            if (getIdx().TryGetValue(key, out var index)) {
                var node = _nodes[index];
                if (node == null) throw new InvalidOperationException("Internal error: Node is null. This should not happen. ");
                return node;
            } else {
                throw new KeyNotFoundException($"The given key '{key}' was not present in the map. ");
            }
        }
        set {
            var indexByKey = getIdx(); // Reduce calls to the property getter
            if (!EqualityComparer<TKey>.Default.Equals(EvalKey(value), key)) throw new ArgumentException("The provided key does not match the node key.", nameof(key));
            if (indexByKey.TryGetValue(key, out var index)) {
                _nodes[index] = value;
            } else {
                _nodes.Add(value);
                indexByKey[key] = _nodes.Count - 1;
            }
        }
    }
    public void Clear() {
        _nodes.Clear();
        __indexByKey?.Clear();
    }
    public bool ContainsKey(TKey key) => getIdx().ContainsKey(key);
    public InnerNodeDataMap<TKey> Copy() {
        throw new NotSupportedException();
    }
    public IEnumerator<NodeData> GetEnumerator() {
        foreach (var node in _nodes) {
            if (node != null) yield return node;
        }
    }
    IEnumerator IEnumerable.GetEnumerator() {
        return GetEnumerator();

    }
}
