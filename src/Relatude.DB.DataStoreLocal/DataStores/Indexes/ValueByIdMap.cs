using System.Collections;
using Relatude.DB.DataStores.Sets;

namespace Relatude.DB.DataStores.Indexes;

/// <summary>
/// id → value map that upgrades from a dictionary to a dense array once it is large enough,
/// exploiting that node ids are dense small integers: a lookup then becomes a bit test and one
/// array read (a few ns), which makes one-pass facet counting over a result set cheap at any scale.
/// </summary>
internal sealed class ValueByIdMap<T> : IEnumerable<KeyValuePair<int, T>> where T : notnull {
    const int limDictionary = 10_000;
    Dictionary<int, T>? _dic = [];
    T[] _values = [];
    DenseBitSet? _has; // non-null in dense mode; tracks which slots hold a value
    public int Count => _dic != null ? _dic.Count : _has!.Count;
    public void Add(int id, T value) {
        if (_dic != null) {
            _dic.Add(id, value);
            if (_dic.Count == limDictionary) tryUpgrade(); // attempted once; a map this sparse rarely becomes dense later
            return;
        }
        if (!_has!.Contains(id)) {
            if (id >= _values.Length) Array.Resize(ref _values, Math.Max(id + 1, _values.Length + (_values.Length >> 1))); // NB: mutations run under the store's write lock (readers never see a torn resize)
            _has.Add(id);
            _values[id] = value;
            return;
        }
        throw new ArgumentException("An item with the same key has already been added. Key: " + id);
    }
    public void Remove(int id) {
        if (_dic != null) {
            _dic.Remove(id);
            return;
        }
        if (_has!.Remove(id)) _values[id] = default!; // clear so removed reference values can be collected
    }
    public bool TryGetValue(int id, out T value) {
        if (_dic != null) return _dic.TryGetValue(id, out value!);
        if ((uint)id < (uint)_values.Length && _has!.Contains(id)) {
            value = _values[id];
            return true;
        }
        value = default!;
        return false;
    }
    public T this[int id] => TryGetValue(id, out var v) ? v : throw new KeyNotFoundException(id.ToString());
    public IEnumerable<int> Keys => _dic != null ? _dic.Keys : _has!;
    public IEnumerator<KeyValuePair<int, T>> GetEnumerator() {
        if (_dic != null) {
            foreach (var kv in _dic) yield return kv;
        } else {
            foreach (var id in _has!) yield return new(id, _values[id]);
        }
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    void tryUpgrade() {
        var maxId = 0;
        foreach (var id in _dic!.Keys) if (id > maxId) maxId = id;
        if (maxId < 0 || (long)maxId > (long)_dic.Count * 8) return; // too sparse: the array would waste more than it gains
        _values = new T[maxId + 1];
        _has = new DenseBitSet(0, maxId);
        foreach (var (id, value) in _dic) {
            _values[id] = value;
            _has.Add(id);
        }
        _dic = null;
    }
}
