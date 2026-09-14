using System.Collections;
using System.Runtime.CompilerServices;
using Relatude.DB.DataStores.Sets;

namespace Relatude.DB.DataStores.Indexes;

/// <summary>
/// id → value map that switches between a dictionary and a dense array indexed by id, exploiting
/// that node ids are dense small integers: a lookup is then a bit test and one array read (a few
/// ns), which makes one-pass facet counting over a result set cheap at any scale. The decision is
/// revisited as the map grows and reversed when an id lands far outside the array, so the map never
/// costs more than the dictionary it replaces.
/// </summary>
public sealed class ValueByIdMap<T> : IEnumerable<KeyValuePair<int, T>> where T : notnull {
    const int limDictionary = 10_000;
    const int dictionaryEntryOverhead = 28; // hash, next, bucket and load slack around a value in Dictionary<int, T>
    static readonly int _valueSize = Unsafe.SizeOf<T>();
    int _nextUpgradeCheck = limDictionary; // the density check repeats at every doubling: a map judged sparse early can fill in later
    readonly int _expectedCount;
    Dictionary<int, T>? _dic = [];
    T[] _values = [];
    DenseBitSet? _has; // non-null in dense mode; tracks which slots hold a value
    public ValueByIdMap() { }
    /// <summary>Starts in dense mode sized for <paramref name="expectedCount"/> ids, so a bulk load does not rehash or resize.</summary>
    public ValueByIdMap(int expectedCount) {
        _expectedCount = expectedCount;
        if (expectedCount < limDictionary) return;
        _dic = null;
        _values = new T[expectedCount + 1];
        _has = new DenseBitSet(0, expectedCount);
    }
    public int Count => _dic != null ? _dic.Count : _has!.Count;
    internal bool IsDense => _dic == null;
    // an array up to maxId costs more than a dictionary of count entries
    static bool tooSparse(long maxId, long count) => (maxId + 1) * _valueSize > count * (_valueSize + dictionaryEntryOverhead);
    public void Add(int id, T value) {
        if (_dic != null) {
            _dic.Add(id, value);
            if (_dic.Count >= _nextUpgradeCheck) tryUpgrade();
            return;
        }
        if (Contains(id)) throw new ArgumentException("An item with the same key has already been added. Key: " + id);
        set(id, value);
    }
    public void Set(int id, T value) {
        if (_dic != null) {
            _dic[id] = value;
            if (_dic.Count >= _nextUpgradeCheck) tryUpgrade();
            return;
        }
        set(id, value);
    }
    void set(int id, T value) {
        if (id >= _values.Length || id < 0) {
            if (id < 0 || tooSparse(id, Math.Max(Count + 1, _expectedCount))) {
                downgrade();
                _dic![id] = value;
                return;
            }
            Array.Resize(ref _values, Math.Max(id + 1, _values.Length + (_values.Length >> 1))); // NB: mutations run under the store's write lock (readers never see a torn resize)
        }
        _values[id] = value;
        _has!.Add(id);
    }
    public void Remove(int id) {
        if (_dic != null) {
            _dic.Remove(id);
            return;
        }
        if ((uint)id < (uint)_values.Length && _has!.Remove(id)) _values[id] = default!; // clear so removed reference values can be collected
    }
    public void Clear() {
        if (_dic != null) {
            _dic.Clear();
            return;
        }
        _has!.Clear();
        Array.Clear(_values);
    }
    public bool Contains(int id) {
        if (_dic != null) return _dic.ContainsKey(id);
        return (uint)id < (uint)_values.Length && _has!.Contains(id);
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
        if (maxId < 0 || _dic.Keys.Any(id => id < 0) || tooSparse(maxId, _dic.Count)) {
            _nextUpgradeCheck = _dic.Count * 2;
            return;
        }
        _values = new T[maxId + 1];
        _has = new DenseBitSet(0, maxId);
        foreach (var (id, value) in _dic) {
            _values[id] = value;
            _has.Add(id);
        }
        _dic = null;
    }
    void downgrade() {
        var dic = new Dictionary<int, T>(Math.Max(Count, _expectedCount));
        foreach (var id in _has!) dic[id] = _values[id];
        _dic = dic;
        _values = [];
        _has = null;
        _nextUpgradeCheck = Math.Max(limDictionary, dic.Count * 2);
    }
}
