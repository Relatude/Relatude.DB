using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.Nodes;

interface IInnerNodesMap {
    PropertyPath? PropertyPath { get; }
}
interface IInnerNodes {
}
public class InnerNodes<TValue> : InnerNodes<Guid, TValue>, IInnerNodes where TValue : notnull {
    public InnerNodes() { }
    public InnerNodes(Guid keyPropertyId, InnerNodeDataMap<Guid> nodeDataMap, NodeMapper mapper) : base(keyPropertyId, nodeDataMap, mapper) {
    }
}
public class InnerNodes<TKey, TValue> : IEnumerable<TValue>, IInnerNodesMap where TKey : notnull
where TValue : notnull {
    InnerNodeDataMap<TKey>? _nodeDataMap;
    NodeMapper? _mapper;
    List<TValue>? _raw;
    public InnerNodes() {
        _raw = [];
    }
    public PropertyPath? PropertyPath => _nodeDataMap?.PropertyPath;
    public InnerNodes(Guid keyPropertyId, InnerNodeDataMap<TKey> nodeDataMap, NodeMapper mapper) {
        _nodeDataMap = nodeDataMap;
        _mapper = mapper;
    }
    public TValue this[TKey key] {
        get {
            if (_nodeDataMap != null && _mapper != null) {
                var nodeData = _nodeDataMap[key];
                return (TValue)_mapper.CreateObjectFromNodeData(nodeData, _nodeDataMap.PropertyPath);
            } else if (_raw != null) {
                throw new InvalidOperationException("Cannot access InnerNodes by key until node is persisted to datastore. ");
            } else {
                throw new InvalidOperationException("InnerNodes is not properly initialized. ");
            }
        }
        set {
            if (_nodeDataMap != null && _mapper != null) {
                var nodeData = _mapper.CreateNodeDataFromObject(value, null, _nodeDataMap.PropertyPath);
                if (nodeData is not NodeData nd) throw new ArgumentException("The value must be of type NodeData.");
                if (!EqualityComparer<TKey>.Default.Equals(_nodeDataMap.EvalKey(nd), key)) throw new ArgumentException("The provided key does not match the value key.", nameof(key));
                _nodeDataMap[key] = nd;
            } else if (_raw != null) {
                throw new InvalidOperationException("Cannot access InnerNodes by key until node is persisted to datastore. ");
            } else {
                throw new InvalidOperationException("InnerNodes is not properly initialized. ");
            }
        }
    }
    public int Count => _raw?.Count ?? _nodeDataMap?.Count ?? 0;
    public bool IsReadOnly => false;
    public void Add(TValue value) {
        if (_raw != null) {
            _raw.Add(value);
            return;
        } else if (_nodeDataMap != null && _mapper != null) {
            var data = _mapper.CreateNodeDataFromObject(value, null, _nodeDataMap.PropertyPath);
            if (data is not NodeData nodeData) throw new ArgumentException("The value must be of type NodeData.");
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
    public bool Contains(TKey key) {
        if (_raw != null) {
            throw new InvalidOperationException("Cannot access InnerNodes by key until node is persisted to datastore. ");
        } else if (_nodeDataMap != null) {
            return _nodeDataMap.ContainsKey(key);
        } else {
            throw new InvalidOperationException("InnerNodes is not properly initialized. ");
        }
    }
    public IEnumerable<KeyValuePair<TKey, TValue>> KeysAndValues() {
        if (_raw != null) {
            throw new InvalidOperationException("Cannot access InnerNodes by key until node is persisted to datastore. ");
        } else if (_nodeDataMap != null && _mapper != null) {
            foreach (var node in _nodeDataMap) {
                var key = _nodeDataMap.EvalKey(node);
                var value = (TValue)_mapper.CreateObjectFromNodeData(node, _nodeDataMap.PropertyPath);
                yield return new KeyValuePair<TKey, TValue>(key, value);
            }
        } else {
            throw new InvalidOperationException("InnerNodes is not properly initialized. ");
        }
    }
    public bool Remove(TKey key) {
        if (_raw != null) {
            throw new InvalidOperationException("Cannot access InnerNodes by key until node is persisted to datastore. ");
        } else if (_nodeDataMap != null) {
            return _nodeDataMap.Remove(key);
        } else {
            throw new InvalidOperationException("InnerNodes is not properly initialized. ");
        }
    }
    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) {
        if (_raw != null) {
            throw new InvalidOperationException("Cannot access InnerNodes by key until node is persisted to datastore. ");
        } else if (_nodeDataMap != null && _mapper != null) {
            if (_nodeDataMap.TryGetValue(key, out var node)) {
                value = (TValue)_mapper.CreateObjectFromNodeData(node, _nodeDataMap.PropertyPath);
                return true;
            }
            value = default;
            return false;
        } else {
            throw new InvalidOperationException("InnerNodes is not properly initialized. ");
        }
    }
    public InnerNodeDataMap<TKey> GetNodeDataMap(PropertyPath propPath, Guid keyPropertyId, NodeMapper mapper) {
        if (_raw != null) {
            var nodeDatas = _raw.Select(r => {
                if (mapper.CreateNodeDataFromObject(r, null, propPath) is not NodeData nd)
                    throw new ArgumentException("The Inner nodes must be of type NodeData and not use revisions etc.");
                return nd;
            }).ToList();
            return new InnerNodeDataMap<TKey>(propPath, keyPropertyId, nodeDatas);
        } else if (_nodeDataMap != null) {
            return _nodeDataMap;
        } else {
            throw new InvalidOperationException("InnerNodes is not properly initialized. ");
        }
    }

    public IEnumerator<TValue> GetEnumerator() {
        if (_raw != null) {
            throw new InvalidOperationException("Cannot access InnerNodes by key until node is persisted to datastore. ");
        } else if (_nodeDataMap != null && _mapper != null) {
            foreach (var node in _nodeDataMap) {
                yield return (TValue)_mapper.CreateObjectFromNodeData(node, _nodeDataMap.PropertyPath);
            }
        } else {
            throw new InvalidOperationException("InnerNodes is not properly initialized. ");
        }
    }

    IEnumerator IEnumerable.GetEnumerator() {
        return GetEnumerator();
    }

}
