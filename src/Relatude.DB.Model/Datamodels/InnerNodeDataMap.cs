using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.DB.Datamodels;

public class InnerNodeDataMap<TKey> where TKey : notnull {
    List<NodeData?> _nodes; // null means the node has been removed, but we keep the slot to avoid shifting indices of subsequent items
    Dictionary<TKey, int> _indexByKey;
    Guid _keyPropertyId;
    public InnerNodeDataMap(Guid keyPropertyId, NodeData[] nodes) {
        if (keyPropertyId == Guid.Empty && typeof(TKey) != typeof(Guid)) {
            throw new ArgumentException("Key property ID cannot be empty when key type is not Guid. ");
        }
        _keyPropertyId = keyPropertyId;
        _nodes = [.. nodes];
        _indexByKey = new(nodes.Length);
        for (int i = 0; i < nodes.Length; i++) _indexByKey.Add(EvalKey(nodes[i]), i);
    }
    public TKey EvalKey(NodeData node) {
        if (_keyPropertyId == Guid.Empty) {
            return (TKey)(object)node.Id;
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
        if (_keyPropertyId == Guid.Empty) {
            key = (TKey)(object)node.Id;
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
        // Update indices of all existing items
        for (int i = 1; i < _nodes.Count; i++) {
            if (_nodes[i] is NodeData shifted) _indexByKey[EvalKey(shifted)] = i;
        }
        _indexByKey[key] = 0;
    }
    public void Add(NodeData node) {
        TKey key;
        if (_keyPropertyId == Guid.Empty) {
            key = (TKey)(object)node.Id;
        } else if (node.TryGetValue(_keyPropertyId, out var oKey)) {
            if (oKey is TKey tKey) {
                key = tKey;
            } else {
                throw new ArgumentException($"Node with ID {node.Id} has a value for the key property with ID {_keyPropertyId}, but it is not of type {typeof(TKey).FullName}. ");
            }
        } else {
            throw new ArgumentException($"Node with ID {node.Id} does not have a value for the key property with ID {_keyPropertyId}. ");
        }
        _nodes.Add(node);
        _indexByKey[key] = _nodes.Count - 1;
    }
    public bool TryAdd(NodeData node) {
        if (TryEvalKey(node, out var key)) {
            if (_indexByKey.ContainsKey(key)) return false;
            _nodes.Add(node);
            _indexByKey[key] = _nodes.Count - 1;
            return true;
        }
        return false;
    }
    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out NodeData node) {
        if (_indexByKey.TryGetValue(key, out var index)) {
            node = _nodes[index];
            if (node == null) throw new InvalidOperationException("Internal error: Node is null. This should not happen. ");
            return true;
        }
        node = null;
        return false;
    }
    public void Remove(TKey key) {
        if (_indexByKey.TryGetValue(key, out var index)) {
            _nodes[index] = null;
            _indexByKey.Remove(key);
        } else {
            throw new KeyNotFoundException($"The given key '{key}' was not present in the map. ");
        }
    }
    public void Move(TKey key, int newIndex) {
        if (!_indexByKey.TryGetValue(key, out int oldIndex)) throw new KeyNotFoundException($"The given key '{key}' was not present in the map. ");
        if ((uint)newIndex >= (uint)_nodes.Count) throw new ArgumentOutOfRangeException(nameof(newIndex));
        if (oldIndex == newIndex) return;
        var node = _nodes[oldIndex] ?? throw new InvalidOperationException("Internal error: Node is null. This should not happen. ");
        if (oldIndex < newIndex) {
            for (int i = oldIndex; i < newIndex; i++) {
                _nodes[i] = _nodes[i + 1];
                if (_nodes[i] is NodeData shifted) _indexByKey[EvalKey(shifted)] = i;
            }
        } else {
            for (int i = oldIndex; i > newIndex; i--) {
                _nodes[i] = _nodes[i - 1];
                if (_nodes[i] is NodeData shifted) _indexByKey[EvalKey(shifted)] = i;
            }
        }
        _nodes[newIndex] = node;
        _indexByKey[key] = newIndex;
    }
    public void MoveRelative(TKey key, int offset) {
        if (!_indexByKey.TryGetValue(key, out int index)) throw new KeyNotFoundException($"The given key '{key}' was not present in the map. ");
        long target = (long)index + offset;
        if (target < 0) target = 0;
        else if (target >= _nodes.Count) target = _nodes.Count - 1;
        Move(key, (int)target);
    }
    public int Count => _indexByKey.Count;
    public void Clear() {
        _nodes.Clear();
        _indexByKey.Clear();
    }
    public bool ContainsKey(TKey key) => _indexByKey.ContainsKey(key);
}
