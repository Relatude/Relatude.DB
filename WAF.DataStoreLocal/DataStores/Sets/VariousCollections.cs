using System.Collections;
namespace WAF.DataStores.Sets;
internal class MutableSet(int item1, int item2) : ICollection<int> {
    IdSet? _lastSet;
    readonly StateIdTracker _state = new();
    ICollection<int> _items = new int[] { item1, item2 }; // array, list, hashSet - depending on size
    readonly static int limArr = 10; // upper threshold for using array data structure
    readonly static int limList = 1000; // upper threshold for using list data structure ( when doing lookups ), after which it uses HashSet
    public void Add(int item) {
        _lastSet = null;
        _state.RegisterAddition(item);
        if (_items is int[] arr) {
            if (arr.Length >= limArr) {
                _items = new List<int>(arr) { item };
            } else {
                Array.Resize(ref arr, arr.Length + 1);
                arr[^1] = item;
                _items = arr; // value is boxed, so must update reference
            }
        } else if (_items is List<int> list) {
            if (list.Count >= limList) {
                _items = new HashSet<int>(list) { item };
            } else {
                list.Add(item);
            }
        } else if (_items is HashSet<int> hashSet) {
            hashSet.Add(item);
        }
    }
    public bool Contains(int item) {
        if (_items is HashSet<int> hashSet) return hashSet.Contains(item);
        if (_items is int[] arr) {
            for (int i = 0; i < arr.Length; i++) if (arr[i] == item) return true;
            return false;
        }
        return _items.Contains(item);
    }
    public int Count => _items.Count;
    public bool IsReadOnly => false;
    public bool Remove(int item) {
        _lastSet = null;
        _state.RegisterRemoval(item);
        if (_items is int[] arr) {
            int index;
            for (index = 0; index < arr.Length; index++) if (arr[index] == item) break;
            if (index == arr.Length) throw new Exception(item + " not found. ");
            int[] newArr = new int[arr.Length - 1];
            if (index > 0) Array.Copy(arr, 0, newArr, 0, index);
            if (index < arr.Length - 1) Array.Copy(arr, index + 1, newArr, index, arr.Length - index - 1);
            _items = newArr;
        } else if (_items is List<int> list) {
            int index;
            for (index = 0; index < list.Count; index++) if (list[index] == item) break;
            if (index == list.Count) throw new Exception(item + " not found. ");
            list.RemoveAt(index);
        } else if (_items is HashSet<int> hashSet) {
            if (!hashSet.Remove(item)) throw new Exception(item + " not found. ");
        }
        return true;
    }
    public IdSet AsUnmutableIdSet() {
        if (_lastSet != null) return _lastSet;
        return _lastSet ??= new(this, _state.Current);
    }
    public IEnumerator<int> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public override string ToString() => _items.GetType().Name + " (" + Count.ToString() + ")";
    public void Clear() {
        _lastSet = null;
        _state.Reset();
        _items = Array.Empty<int>();
        }
    public void CopyTo(int[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
    bool ICollection<int>.Remove(int item) => Remove(item);
    public bool IsProperSubsetOf(IEnumerable<int> other) => throw new NotImplementedException();
    public bool IsProperSupersetOf(IEnumerable<int> other) => throw new NotImplementedException();
    public bool IsSubsetOf(IEnumerable<int> other) => throw new NotImplementedException();
    public bool IsSupersetOf(IEnumerable<int> other) => throw new NotImplementedException();
    public bool Overlaps(IEnumerable<int> other) => throw new NotImplementedException();
    public bool SetEquals(IEnumerable<int> other) => throw new NotImplementedException();
}
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
