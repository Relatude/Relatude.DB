using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.Common;

public class InnerNodesMap<TKey, TValue> : IDictionary<TKey, TValue> {

    public void Move(TKey key, int newIndex) => throw new NotImplementedException();
    public void MoveRelative(TKey key, int offset) => throw new NotImplementedException();
    public int IndexOf(TKey key) => throw new NotImplementedException();

    public TValue this[TKey key] { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public ICollection<TKey> Keys => throw new NotImplementedException();
    public ICollection<TValue> Values => throw new NotImplementedException();
    public int Count => throw new NotImplementedException();
    public bool IsReadOnly => throw new NotImplementedException();
    public void Add(TKey key, TValue value) => throw new NotImplementedException();
    public void Add(KeyValuePair<TKey, TValue> item) => throw new NotImplementedException();
    public void Clear() => throw new NotImplementedException();
    public bool Contains(KeyValuePair<TKey, TValue> item) => throw new NotImplementedException();
    public bool ContainsKey(TKey key) => throw new NotImplementedException();
    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) => throw new NotImplementedException();
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => throw new NotImplementedException();
    public bool Remove(TKey key) => throw new NotImplementedException();
    public bool Remove(KeyValuePair<TKey, TValue> item) => throw new NotImplementedException();
    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) => throw new NotImplementedException();
    IEnumerator IEnumerable.GetEnumerator() => throw new NotImplementedException();
}

