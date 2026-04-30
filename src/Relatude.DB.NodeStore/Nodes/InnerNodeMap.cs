using Relatude.DB.Datamodels;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.Nodes;

public class InnerNodes<TValue> : InnerNodes<Guid, TValue> where TValue : notnull {
    public InnerNodes(Guid keyPropertyId, InnerNodeDataMap<Guid> nodeDataMap, NodeMapper mapper)
        : base(keyPropertyId, nodeDataMap, mapper) { }
}
public class InnerNodes<TKey, TValue> : IDictionary<TKey, TValue>
    where TKey : notnull
    where TValue : notnull {
    InnerNodeDataMap<TKey> _nodeDataMap;
    NodeMapper _mapper;
    public InnerNodes(Guid keyPropertyId, InnerNodeDataMap<TKey> nodeDataMap, NodeMapper mapper) {
        _nodeDataMap = nodeDataMap;
        _mapper = mapper;
    }
    public TValue this[TKey key] { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public ICollection<TKey> Keys => throw new NotImplementedException();
    public ICollection<TValue> Values => throw new NotImplementedException();
    public int Count => _nodeDataMap.Count;
    public bool IsReadOnly => throw new NotImplementedException();
    public void Add(TKey key, TValue value) {
        var data = _mapper.CreateNodeDataFromObject(value, null);
        if (data is not NodeData nodeData) throw new ArgumentException("The value must be of type NodeData.");
        _nodeDataMap.Add(nodeData);
    }
    public void Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);
    public void Clear() => _nodeDataMap.Clear();
    public bool Contains(KeyValuePair<TKey, TValue> item) => _nodeDataMap.ContainsKey(item.Key);
    public bool ContainsKey(TKey key) => _nodeDataMap.ContainsKey(key);

    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) {
        throw new NotImplementedException();
    }
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() {
        throw new NotImplementedException();
    }
    public bool Remove(TKey key) {
        throw new NotImplementedException();
    }
    public bool Remove(KeyValuePair<TKey, TValue> item) {
        throw new NotImplementedException();
    }
    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) {
        throw new NotImplementedException();
    }
    IEnumerator IEnumerable.GetEnumerator() {
        return GetEnumerator();
    }
    public static InnerNodes<TKey, TValue> Empty { get; };
}
