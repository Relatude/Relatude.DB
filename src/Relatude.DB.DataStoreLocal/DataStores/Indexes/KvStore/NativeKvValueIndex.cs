using System.Text;
using Relatude.DB.DataStores.Sets;
using SuperFastIndex;

namespace Relatude.DB.DataStores.Indexes.KvStore;

internal class NativeKvValueIndex<T> : IValueIndex<T> where T : notnull {
    static readonly Comparer<T> _comparer = Comparer<T>.Default;
    ISortedIndex<T> _index;
    readonly StateIdValueTracker<T> _stateId;
    readonly SetRegister _sets;
    public NativeKvValueIndex(string uniqueKey, IStorageEngine store, SetRegister sets, string friendlyName) {
        UniqueKey = uniqueKey;
        _sets = sets;
        _index = store.OpenOrCreateIndex<T>(uniqueKey);
        _stateId = new();
        FriendlyName = friendlyName;
    }
    public long StateId => _stateId.Current;
    public int IdCount => _index.Count;
    public IEnumerable<int> Ids => _index.Keys;
    public IEnumerable<T> UniqueValues => _index.DistinctValues;
    public int ValueCount => _index.DistinctValueCount;
    public string UniqueKey { get; }
    public string FriendlyName { get; }
    public long PersistedTimestamp { get; set; }
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
        _gap = null;
    }
    public void CompressMemory() {

    }
    public bool ContainsValue(T value) {
        return _index.ContainsValue(value);
    }
    public int CountEqual(IdSet nodeIds, T value) {
        var ids = GetIds(value);
        var count = 0;
        foreach (var id in ids) if (nodeIds.Has(id)) count++;
        return count;
    }
    public int CountGreaterThan(T value, bool inclusive) {
        return _index.CountIdsGreaterThan(value, inclusive);
    }
    public int CountInRangeEqual(IdSet nodeIds, T from, T to, bool fromInclusive, bool toInclusive) {
        var count = 0;
        var allInRange = _index.GetIdsInRange(from, to, fromInclusive, toInclusive);
        foreach (var id in allInRange) if (nodeIds.Has(id)) count++;
        return count;
    }
    public int CountLessThan(T value, bool inclusive) {
        return _index.CountIdsSmallerThan(value, inclusive);
    }
    public void Dispose() {

    }
    public IdSet Filter(IdSet nodeIds, IndexOperator op, T v) {
        if (op == IndexOperator.NotEqual) {
            List<int> notEqual = [];
            foreach (var id in nodeIds.Enumerate()) {
                if (_index.TryGetValue(id, out var value)) {
                    var comparison = _comparer.Compare(value, v);
                    if (comparison != 0) {
                        // If the value is not equal to v, we include it in the result
                        notEqual.Add(id);
                    }
                    ;
                } else {
                    // if the id does not exist in the index, we consider it as not equal to v
                    notEqual.Add(id);
                }
            }
            return IdSet.UncachableSet(notEqual);
        }
        IEnumerable<int> possibleMatches = op switch {
            IndexOperator.Equal => GetIds(v),
            IndexOperator.Greater => GreaterThan(v, false),
            IndexOperator.GreaterOrEqual => GreaterThan(v, true),
            IndexOperator.Smaller => LessThan(v, false),
            IndexOperator.SmallerOrEqual => LessThan(v, true),
            _ => throw new Exception("Unknown operator: " + op + ". "),
        };
        var matchSet = new HashSet<int>(possibleMatches);
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

    // Returns the same cache key for any query that yields the same result set, so the
    // SetRegister can reuse cached sets across varying query values (see ValueIndex<T>.GetCacheKey).
    // For range queries we key on the number of matching ids instead of the raw value: two query
    // values landing in the same gap between indexed values produce the same count, hence the same
    // (nested and equal-sized, therefore identical) result set. For equality we only need the value
    // when it actually exists; all queries for a non-existent value are equivalent.
    // The bounds and counts of the last such gap are kept in _gap, so repeated queries with varying
    // values in the same gap (typically DateTime.Now on every request) resolve without touching the index.
    public object[] GetCacheKey(T queryValue, QueryType queryType) {
        var gap = _gap;
        if (gap == null || gap.StateId != StateId || !inGap(gap, queryValue)) {
            if (ContainsValue(queryValue)) {
                return queryType switch {
                    QueryType.Equal or QueryType.NotEqual => [queryType, queryValue],
                    QueryType.Greater => [queryType, CountGreaterThan(queryValue, false)],
                    QueryType.GreaterOrEqual => [queryType, CountGreaterThan(queryValue, true)],
                    QueryType.Less => [queryType, CountLessThan(queryValue, false)],
                    QueryType.LessOrEqual => [queryType, CountLessThan(queryValue, true)],
                    _ => throw new Exception("Unknown query type: " + queryType + ". "),
                };
            }
            gap = buildGap(queryValue);
            _gap = gap;
        }
        // queryValue lies strictly inside the gap: no indexed value equals it,
        // and the inclusive/exclusive distinction cannot matter
        return queryType switch {
            QueryType.Equal or QueryType.NotEqual => [queryType],
            QueryType.Greater or QueryType.GreaterOrEqual => [queryType, gap.CountAbove],
            QueryType.Less or QueryType.LessOrEqual => [queryType, gap.CountBelow],
            _ => throw new Exception("Unknown query type: " + queryType + ". "),
        };
    }
    // the open interval between the two indexed values that surrounded the last non-indexed query value,
    // with the id counts on either side; only valid for the exact index state it was built at
    GapCache? _gap;
    sealed class GapCache(long stateId, int countBelow, int countAbove) {
        public readonly long StateId = stateId;
        public readonly int CountBelow = countBelow; // ids with a value below the gap
        public readonly int CountAbove = countAbove; // ids with a value above the gap
        public bool HasLower; public T Lower = default!; // greatest indexed value below the gap, if any
        public bool HasUpper; public T Upper = default!; // smallest indexed value above the gap, if any
    }
    static bool inGap(GapCache gap, T value) {
        if (gap.HasLower && compareIndexOrder(value, gap.Lower) <= 0) return false;
        if (gap.HasUpper && compareIndexOrder(value, gap.Upper) >= 0) return false;
        return true;
    }
    GapCache buildGap(T value) { // value is known not to be in the index
        var gap = new GapCache(StateId, _index.CountIdsSmallerThan(value, false), _index.CountIdsGreaterThan(value, false));
        if (_index.Count > 0) {
            if (compareIndexOrder(value, _index.GetMaxValue()) < 0)
                foreach (var entry in _index.GetEntriesInRange(value, _index.GetMaxValue(), false, true)) { gap.Upper = entry.Value; gap.HasUpper = true; break; }
            if (compareIndexOrder(value, _index.GetMinValue()) > 0)
                foreach (var entry in _index.GetEntriesInRange(_index.GetMinValue(), value, true, false, descending: true)) { gap.Lower = entry.Value; gap.HasLower = true; break; }
        }
        return gap;
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
        return CountInRangeEqual(ids, from, to, fromInclusive, toInclusive);
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
