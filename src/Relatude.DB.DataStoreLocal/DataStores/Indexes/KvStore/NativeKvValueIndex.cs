using System.Text;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.Datastores.Indexes.BTreeIndex;

namespace Relatude.DB.DataStores.Indexes.KvStore;

internal class NativeKvValueIndex<T> : PersistedIndexBase, IValueIndex<T>, GapCacheKeyBuilder<T>.IGapSource where T : notnull {
    static readonly Comparer<T> _comparer = Comparer<T>.Default;
    readonly ISortedIndex<T> _index;
    readonly StateIdValueTracker<T> _stateId;
    readonly SetRegister _sets;
    readonly GapCacheKeyBuilder<T> _keyBuilder;
    public NativeKvValueIndex(string uniqueKey, NativeKvIndexStore store, IStorageEngine engine, SetRegister sets, string friendlyName)
        : base(store, engine.OpenOrCreateIndex<T>(uniqueKey).GetTimestamp() == 0) {
        UniqueKey = uniqueKey;
        _sets = sets;
        _index = engine.OpenOrCreateIndex<T>(uniqueKey); // idempotent: returns the same open index as the base check above
        _stateId = new();
        FriendlyName = friendlyName;
        _keyBuilder = new GapCacheKeyBuilder<T>(this);
    }
    public long StateId => _stateId.Current;
    public int IdCount => _index.Count;
    public IEnumerable<int> Ids => _index.Keys;
    public IEnumerable<T> UniqueValues => _index.DistinctValues;
    public int ValueCount => _index.DistinctValueCount;
    public string UniqueKey { get; }
    public string FriendlyName { get; }
    void add(int id, T value) {
        _index.Set(id, value);
        _stateId.RegisterAddition(id, value);
    }
    void remove(int id, T value) {
        _index.Remove(id);
        _stateId.RegisterRemoval(id, value);
    }
    public void Add(int id, T value) => add(id, value);
    public void Add(int id, object value) => add(id, (T)value);
    public void Remove(int id, T value) => remove(id, value);
    public void Remove(int id, object value) => remove(id, (T)value);
    public void RegisterAddDuringStateLoad(int id, object value) => add(id, (T)value);
    public void RegisterRemoveDuringStateLoad(int id, object value) => remove(id, (T)value);
    public void ClearCache() {
        _keyBuilder.Clear();
    }
    public void CompressMemory() {

    }
    public bool ContainsValue(T value) {
        return _index.ContainsValue(value);
    }
    public int CountEqual(IdSet nodeIds, T value) => _sets.CountEqual(this, nodeIds, value);
    public int CountGreaterThan(T value, bool inclusive) {
        return _index.CountIdsGreaterThan(value, inclusive);
    }
    public int CountInRangeEqual(IdSet nodeIds, T from, T to, bool fromInclusive, bool toInclusive) => _sets.CountInRange(this, nodeIds, from, to, fromInclusive, toInclusive);
    public int CountLessThan(T value, bool inclusive) {
        return _index.CountIdsSmallerThan(value, inclusive);
    }
    public void Dispose() {

    }
    public IdSet Filter(IdSet nodeIds, IndexOperator op, T v) => _sets.Filter(this, nodeIds, op, v);
    public IdSet FilterInValues(IdSet nodeIds, IEnumerable<T> selectedValues) => _sets.FilterInValues(this, nodeIds, selectedValues);
    public IdSet FilterRanges(IdSet nodeIds, List<Tuple<T, T>> selectedRanges) {
        var matchSet = new HashSet<int>();
        foreach (var range in selectedRanges) foreach (var id in RangeSearch(range.Item1, range.Item2, true, true)) matchSet.Add(id);
        return IdSet.UncachableSet(nodeIds.Enumerate().Where(matchSet.Contains).ToList());
    }
    public IdSet FilterRangesObject(IdSet set, object from, object to) {
        return FilterRanges(set, [new Tuple<T, T>((T)from, (T)to)]);
    }
    // Cache-key logic lives in the shared GapCacheKeyBuilder; the ordering-sensitive pieces are
    // provided through IGapSource below. See GapCacheKeyBuilder for the full explanation.
    public object[] GetCacheKey(T queryValue, QueryType queryType) => _keyBuilder.GetCacheKey(queryValue, queryType);

    GapCacheKeyBuilder<T>.Gap GapCacheKeyBuilder<T>.IGapSource.BuildGap(T value) { // value is known not to be in the index
        object? lower = null, upper = null;
        if (_index.Count > 0) {
            if (compareIndexOrder(value, _index.GetMaxValue()) < 0)
                foreach (var entry in _index.GetEntriesInRange(value, _index.GetMaxValue(), false, true)) { upper = entry.Value; break; }
            if (compareIndexOrder(value, _index.GetMinValue()) > 0)
                foreach (var entry in _index.GetEntriesInRange(_index.GetMinValue(), value, true, false, descending: true)) { lower = entry.Value; break; }
        }
        return new GapCacheKeyBuilder<T>.Gap {
            StateId = StateId,
            CountBelow = _index.CountIdsSmallerThan(value, false),
            CountAbove = _index.CountIdsGreaterThan(value, false),
            Lower = lower,
            Upper = upper,
        };
    }
    // bounds are compared in the index's own byte ordering (see compareIndexOrder)
    bool GapCacheKeyBuilder<T>.IGapSource.InGap(GapCacheKeyBuilder<T>.Gap gap, T value) {
        if (gap.Lower != null && compareIndexOrder(value, (T)gap.Lower) <= 0) return false;
        if (gap.Upper != null && compareIndexOrder(value, (T)gap.Upper) >= 0) return false;
        return true;
    }
    // must match the index's own ordering (its order-preserving byte encoding): strings are ordered
    // by their UTF-8 bytes and Guids by their big-endian RFC 4122 bytes, neither of which agrees
    // with Comparer<T>.Default; all other supported types match it
    static int compareIndexOrder(T a, T b) {
        if (a is string sa && b is string sb) return Encoding.UTF8.GetBytes(sa).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(sb));
        if (a is Guid ga && b is Guid gb) return compareGuidBigEndian(ga, gb);
        return _comparer.Compare(a, b);
    }
    static int compareGuidBigEndian(Guid a, Guid b) {
        Span<byte> ba = stackalloc byte[16], bb = stackalloc byte[16];
        a.TryWriteBytes(ba, bigEndian: true, out _);
        b.TryWriteBytes(bb, bigEndian: true, out _);
        return ba.SequenceCompareTo(bb);
    }
    public ICollection<int> GetIds(T value) {
        return _index.GetIds(value).ToList();
    }
    public T GetValue(int nodeId) {
        return _index.GetValue(nodeId);
    }
    public IEnumerable<int> GreaterThan(T value, bool inclusive) {
        return _index.GetIdsGreaterThan(value, inclusive);
    }
    public int InSetRangeCount(IdSet ids, T from, T to, bool fromInclusive, bool toInclusive) {
        var count = 0;
        foreach (var id in _index.GetIdsInRange(from, to, fromInclusive, toInclusive)) if (ids.Has(id)) count++;
        return count;
    }
    public IEnumerable<int> LessThan(T value, bool inclusive) {
        return _index.GetIdsSmallerThan(value, inclusive);
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
        return _index.GetMaxValue();
    }
    public T? MinValue() {
        return _index.GetMinValue();
    }
    public IEnumerable<int> RangeSearch(T from, T to, bool fromInclusive, bool toInclusive) {
        return _index.GetIdsInRange(from, to, fromInclusive, toInclusive);
    }
    public IdSet ReOrder(IdSet unsorted, bool descending) => _sets.OrderBy(this, unsorted, descending);
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
}
