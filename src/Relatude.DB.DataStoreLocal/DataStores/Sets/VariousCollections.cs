using System.Collections;
namespace Relatude.DB.DataStores.Sets;
internal class EmptySet : ICollection<int> {
    public static EmptySet Instance = new();
    public int Count => 0;
    public bool IsReadOnly => true;
    public bool Contains(int item) => false;
    public bool IsProperSubsetOf(IEnumerable<int> other) => throw new NotImplementedException();
    public bool IsProperSupersetOf(IEnumerable<int> other) => throw new NotImplementedException();
    public bool IsSubsetOf(IEnumerable<int> other) => throw new NotImplementedException();
    public bool IsSupersetOf(IEnumerable<int> other) => throw new NotImplementedException();
    public bool Overlaps(IEnumerable<int> other) => throw new NotImplementedException();
    public bool SetEquals(IEnumerable<int> other) => throw new NotImplementedException();
    public IEnumerator<int> GetEnumerator() { yield break; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public override string ToString() => "EmptySet";
    public void CopyTo(int[] array, int arrayIndex) { }
    public void Add(int item) => throw new Exception("Illegal operation on read-only collection. ");
    public void Clear() => throw new Exception("Illegal operation on read-only collection. ");
    public bool Remove(int item) => throw new Exception("Illegal operation on read-only collection. ");
}
internal class SingleValueSet : ICollection<int> {
    private readonly int _value;
    public SingleValueSet(int value) => _value = value;
    public int Count => 1;
    public bool IsReadOnly => true;
    public bool Contains(int item) => item == _value;
    public bool IsProperSubsetOf(IEnumerable<int> other) => throw new NotImplementedException();
    public bool IsProperSupersetOf(IEnumerable<int> other) => throw new NotImplementedException();
    public bool IsSubsetOf(IEnumerable<int> other) => throw new NotImplementedException();
    public bool IsSupersetOf(IEnumerable<int> other) => throw new NotImplementedException();
    public bool Overlaps(IEnumerable<int> other) => throw new NotImplementedException();
    public bool SetEquals(IEnumerable<int> other) => throw new NotImplementedException();
    public IEnumerator<int> GetEnumerator() {
        yield return _value;
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public override string ToString() => "SingleValueSet (" + _value.ToString() + ")";
    public void CopyTo(int[] array, int arrayIndex) => array[arrayIndex] = _value;
    public void Add(int item) => throw new Exception("Illegal operation on read-only collection. ");
    public void Clear() => throw new Exception("Illegal operation on read-only collection. ");
    public bool Remove(int item) => throw new Exception("Illegal operation on read-only collection. ");
}
internal class FixedOrderedSet : ICollection<int> {
    private readonly List<int> _list;
    private HashSet<int>? _set;
    public FixedOrderedSet(IEnumerable<int> uniqueCollection, int count) {
        _list = new List<int>(count);
        _list.AddRange(uniqueCollection);
    }
    public int Count => _list.Count;
    public bool IsReadOnly => true;
    public bool Contains(int item) {
        if (_set == null) _set = new(_list);
        return _set.Contains(item);
    }
    public IEnumerator<int> GetEnumerator() => _list.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public override string ToString() => nameof(FixedOrderedSet) + " (" + Count.ToString() + ")";
    public bool IsProperSubsetOf(IEnumerable<int> other) => throw new NotImplementedException();
    public bool IsProperSupersetOf(IEnumerable<int> other) => throw new NotImplementedException();
    public bool IsSubsetOf(IEnumerable<int> other) => throw new NotImplementedException();
    public bool IsSupersetOf(IEnumerable<int> other) => throw new NotImplementedException();
    public bool Overlaps(IEnumerable<int> other) => throw new NotImplementedException();
    public bool SetEquals(IEnumerable<int> other) => throw new NotImplementedException();
    public void CopyTo(int[] array, int arrayIndex) => _list.CopyTo(array, arrayIndex);
    public void Add(int item) => throw new Exception("Illegal operation on read-only collection. ");
    public void Clear() => throw new Exception("Illegal operation on read-only collection. ");
    public bool Remove(int item) => throw new Exception("Illegal operation on read-only collection. ");
}
