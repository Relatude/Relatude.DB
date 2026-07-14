using KvStore;
using Relatude.DB.Common;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Sets;

namespace Relatude.DB.DataStores.Indexes.KvStore;

internal class NativeKvValueIndex<T> : IValueIndex<T> where T : notnull {
    static readonly Comparer<T> _comparer = Comparer<T>.Default;
    KeyValueStore<int, T> _valuesById;
    KeyValueStore<T, IdSet> _idsByValue;
    readonly StateIdValueTracker<T> _stateId;
    readonly SetRegister _sets;
    public NativeKvValueIndex(string uniqueKey, DatabaseFile store, SetRegister sets, string friendlyName) {
        UniqueKey = uniqueKey;
        _sets = sets;
        _valuesById = store.GetStore<int, T>(uniqueKey + "_valuesById");
        _idsByValue = store.GetStore<T, IdSet>(uniqueKey + "_idsByValue");
        _stateId = new();
        FriendlyName = friendlyName;
    }
    public long StateId => _stateId.Current;

    public int IdCount => (int)_valuesById.Count;

    public IEnumerable<int> Ids => _valuesById.GetAllKeys();

    public IEnumerable<T> UniqueValues => _idsByValue.GetAllKeys();

    public int ValueCount => (int)_idsByValue.Count;

    public string UniqueKey { get; }

    public string FriendlyName { get; }
    public long PersistedTimestamp { get; set; }

    void add(int id, T value) {
        _valuesById.Put(id, value);
        var ids = _idsByValue.TryGet(value, out var existing) ? new List<int>(existing.Enumerate()) : [];
        if (!ids.Contains(id)) ids.Add(id);
        _idsByValue.Put(value, IdSet.UncachableSet(ids));
        _stateId.RegisterAddition(id, value);
    }
    void remove(int id, T value) {
        _valuesById.Delete(id);
        if (_idsByValue.TryGet(value, out var existing)) {
            var ids = new List<int>(existing.Enumerate());
            ids.Remove(id);
            if (ids.Count == 0) _idsByValue.Delete(value);
            else _idsByValue.Put(value, IdSet.UncachableSet(ids));
        }
        _stateId.RegisterRemoval(id, value);
    }
    public void Add(int id, T value) => add(id, value);
    public void Add(int id, object value) => add(id, (T)value);
    public void Remove(int id, T value) => remove(id, value);
    public void Remove(int id, object value) => remove(id, (T)value);
    public void RegisterAddDuringStateLoad(int id, object value) => add(id, (T)value);
    public void RegisterRemoveDuringStateLoad(int id, object value) => remove(id, (T)value);

    public void ClearCache() {
        _valuesById.ClearCache();
        _idsByValue.ClearCache();
    }
    public void CompressMemory() {

    }

    public bool ContainsValue(T value) {
        return _idsByValue.ContainsKey(value);
    }

    static int countIds(IEnumerable<KeyValuePair<T, IdSet>> entries) {
        var count = 0;
        foreach (var e in entries) count += e.Value.Count;
        return count;
    }
    static IEnumerable<int> flattenIds(IEnumerable<KeyValuePair<T, IdSet>> entries) {
        foreach (var e in entries) foreach (var id in e.Value.Enumerate()) yield return id;
    }

    public int CountEqual(IdSet nodeIds, T value) {
        var ids = GetIds(value);
        var count = 0;
        foreach (var id in ids) if (nodeIds.Has(id)) count++;
        return count;
    }

    public int CountGreaterThan(T value, bool inclusive) {
        return countIds(_idsByValue.RangeFrom(value, inclusive));
    }

    public int CountInRangeEqual(IdSet nodeIds, T from, T to, bool fromInclusive, bool toInclusive) {
        var ids = flattenIds(_idsByValue.Range(from, to, fromInclusive, toInclusive));
        var count = 0;
        foreach (var id in ids) if (nodeIds.Has(id)) count++;
        return count;
    }

    public int CountLessThan(T value, bool inclusive) {
        return countIds(_idsByValue.RangeTo(value, inclusive));
    }

    public void Dispose() {
        // lifetime of underlying stores owned by DatabaseFile
    }

    public IdSet Filter(IdSet nodeIds, IndexOperator op, T v) {
        IEnumerable<int> matches = op switch {
            IndexOperator.Equal => GetIds(v),
            IndexOperator.NotEqual => Ids.Where(id => !_valuesById.TryGet(id, out var val) || !val.Equals(v)),
            IndexOperator.Greater => GreaterThan(v, false),
            IndexOperator.GreaterOrEqual => GreaterThan(v, true),
            IndexOperator.Smaller => LessThan(v, false),
            IndexOperator.SmallerOrEqual => LessThan(v, true),
            _ => throw new Exception("Unknown operator: " + op + ". "),
        };
        var matchSet = new HashSet<int>(matches);
        return IdSet.UncachableSet(nodeIds.Enumerate().Where(matchSet.Contains).ToList());
    }

    public IdSet FilterInValues(IdSet nodeIds, IEnumerable<T> selectedValues) {
        var matchSet = new HashSet<int>();
        foreach (var value in selectedValues) foreach (var id in GetIds(value)) matchSet.Add(id);
        return IdSet.UncachableSet(nodeIds.Enumerate().Where(matchSet.Contains).ToList());
    }

    public IdSet FilterRanges(IdSet nodeIds, List<Tuple<T, T>> selectedRanges) {
        var matchSet = new HashSet<int>();
        foreach (var range in selectedRanges) foreach (var id in RangeSearch(range.Item1, range.Item2, true, true)) matchSet.Add(id);
        return IdSet.UncachableSet(nodeIds.Enumerate().Where(matchSet.Contains).ToList());
    }

    public IdSet FilterRangesObject(IdSet set, object from, object to) {
        return FilterRanges(set, [new Tuple<T, T>((T)from, (T)to)]);
    }

    public object[] GetCacheKey(T queryValue, QueryType queryType) {
        return [queryType, queryValue];
    }

    public ICollection<int> GetIds(T value) {
        if (_idsByValue.TryGet(value, out var ids)) return ids.Enumerate().ToList();
        return EmptySet.Instance;
    }

    public T GetValue(int nodeId) {
        return _valuesById[nodeId];
    }

    public IEnumerable<int> GreaterThan(T value, bool inclusive) {
        return flattenIds(_idsByValue.RangeFrom(value, inclusive));
    }

    public int InSetRangeCount(IdSet ids, T from, T to, bool fromInclusive, bool toInclusive) {
        return CountInRangeEqual(ids, from, to, fromInclusive, toInclusive);
    }

    public IEnumerable<int> LessThan(T value, bool inclusive) {
        return flattenIds(_idsByValue.RangeTo(value, inclusive));
    }

    public int MaxCount(IndexOperator op, T value) {
        if (IdCount == 0) return 0;
        var min = MinValue();
        var max = MaxValue();
        if (min == null || max == null) return 0;
        if (_comparer.Compare(value, max) > 0) return 0;
        if (_comparer.Compare(value, min) < 0) return 0;
        return op switch {
            IndexOperator.Equal => GetIds(value).Count,
            IndexOperator.NotEqual => IdCount - GetIds(value).Count,
            _ => IdCount,
        };
    }

    public T? MaxValue() {
        var keys = _idsByValue.GetAllKeys(true);
        return keys.Count > 0 ? keys[0] : default;
    }

    public T? MinValue() {
        var keys = _idsByValue.GetAllKeys(false);
        return keys.Count > 0 ? keys[0] : default;
    }

    public IEnumerable<int> RangeSearch(T from, T to, bool fromInclusive, bool toInclusive) {
        return flattenIds(_idsByValue.Range(from, to, fromInclusive, toInclusive));
    }

    public IdSet ReOrder(IdSet unsorted, bool descending) {
        var withValues = unsorted.Enumerate().Select(id => (id, value: GetValue(id))).ToList();
        withValues.Sort((a, b) => descending ? _comparer.Compare(b.value, a.value) : _comparer.Compare(a.value, b.value));
        return IdSet.UncachableSet(withValues.Select(x => x.id).ToList());
    }

    public void ReadStateForMemoryIndexes(Guid walFileId) { } // not relevant for sqlite indexes  
    public void SaveStateForMemoryIndexes(long logTimestamp, Guid walFileId) { } // not relevant for sqlite indexes  

    public IEnumerable<int> WhereRangeOverlapsRange(IValueIndex<T> indexTo, T queryFrom, T queryTo, bool fromInclusive, bool toInclusive) {
        if (ValueCount == 0 || indexTo.ValueCount == 0) return [];
        Func<T, bool> fromCmp = fromInclusive ? (v => _comparer.Compare(v, queryTo) <= 0) : (v => _comparer.Compare(v, queryTo) < 0);
        Func<T, bool> toCmp = toInclusive ? (v => _comparer.Compare(v, queryFrom) >= 0) : (v => _comparer.Compare(v, queryFrom) > 0);
        var result = new List<int>();
        foreach (var id in Ids) {
            var from = GetValue(id);
            var to = indexTo.GetValue(id);
            if (fromCmp(from) && toCmp(to)) result.Add(id);
        }
        return result;
    }

    public void WriteNewTimestampDueToRewriteHotswap(long newTimestamp, Guid walFileId) {
        PersistedTimestamp = newTimestamp;
    }
}
