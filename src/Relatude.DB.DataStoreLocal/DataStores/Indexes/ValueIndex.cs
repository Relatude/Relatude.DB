using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Indexes;

public enum QueryType {
    Equal,
    NotEqual,
    Greater,
    GreaterOrEqual,
    Less,
    LessOrEqual,
}
/// <summary>
/// A general index for values of a specific type T
/// It keeps track of the values of every id (node) in the index
/// and every id that has a specific value.
/// It is sorted by value, and can be used for range queries.
/// It returns IdSets and can be used in combination with the SetRegister for set operations
/// Like Union, Intersection, Difference, etc. used to evaluate boolean query expressions.
/// The SetRegister, has built in caches for these operations.
/// The index stateId is used by the cache determine if the cache is still valid.
/// The cache key is a combination a series of stateId and values. 
/// Depending on the source and operation.
/// </summary>
/// <typeparam name="T"></typeparam>
public sealed class ValueIndex<T> : IIndex, IRangeIndex, IValueIndex<T> where T : notnull {
    Action<T, IAppendStream> _writeValue;
    Func<IReadStream, T> _readValue;
    T? _min;
    T? _max;
    // for value types "_min == null" is always false and "_min = default" is a real value (0, DateTime.MinValue...),
    // so validity of _min/_max is tracked explicitly:
    bool _hasMin;
    bool _hasMax;
    readonly object _sortLock = new();
    (List<(int id, T from, T to)> list, long fromStateId, long toStateId)? _last;
    readonly SetRegister _sets;
    readonly IdByValue<T> _idByValue;
    readonly Dictionary<int, T> _valueById = [];
    readonly StateIdValueTracker<T> _stateId;
    readonly IIOProvider _io;
    readonly FileKeyUtility _fileKeys;
    public ValueIndex(SetRegister register, string uniqueKey, string friendlyName, IIOProvider io, FileKeyUtility fileKey, Action<T, IAppendStream> writeValue, Func<IReadStream, T> readValue) {
        _writeValue = writeValue;
        _readValue = readValue;
        _io = io;
        _fileKeys = fileKey;
        _sets = register;
        _stateId = new();
        _idByValue = new(register);
        UniqueKey = uniqueKey;
        FriendlyName = friendlyName;
    }
    public string UniqueKey { get; }

    // this is a measure to enable better reuse of cached sets
    // as an example, lets say you query for DateTime.Now on every page request
    // since the query value will vary on every query, a cache key based on
    // the query value will be unique for every query, and the cached sets will not be reused
    // if however, the cache key is based on the closest matching values in the index, we know that result for
    // any datetime in the same range will be the same, and we can reuse the cached set
    // this is particularly useful for range queries, where the query value is not an exact match
    public object[] GetCacheKey(T queryValue, QueryType queryType) {
        if (ValueCount == 0) return [queryType];
        var exists = _idByValue.ContainsValue(queryValue);
        if (exists) {
            switch (queryType) {
                case QueryType.Equal:
                case QueryType.NotEqual: return [queryType, queryValue];
                case QueryType.GreaterOrEqual:
                case QueryType.LessOrEqual: return [queryType, exists, queryValue];
            }
        } else {
            switch (queryType) {
                case QueryType.Equal:
                case QueryType.NotEqual: return [queryType];
            }
        }
        var values = GetSortedValues(); // potentially expensive, but only done once per query as sort is cached...
        var idx = values.BinarySearch(queryValue, comparer); // must use the same comparer as the sort (see IdByValue)
        if (idx >= 0) {
            switch (queryType) {
                case QueryType.Greater: return [queryType, idx + 1];
                case QueryType.Less: return [queryType, idx - 1];
            }
        } else {
            idx = ~idx;
            switch (queryType) {
                case QueryType.Greater: return [queryType, idx];
                case QueryType.GreaterOrEqual: return [queryType, idx];
                case QueryType.Less: return [queryType, idx - 1];
                case QueryType.LessOrEqual: return [queryType, idx - 1];
            }
        }
        throw new Exception("Internal error. "); // should not occur due to the above check
    }
    public long StateId { get => _stateId.Current; }
    void add(int id, T value) {
        _valueById.Add(id, value);
        _idByValue.Index(value, id);
        _stateId.RegisterAddition(id, value);
        if (!_hasMin || comparer.Compare(value, _min!) < 0) { _min = value; _hasMin = true; }
        if (!_hasMax || comparer.Compare(value, _max!) > 0) { _max = value; _hasMax = true; }
    }
    void remove(int id, T value) {
        _valueById.Remove(id);
        _idByValue.DeIndex(value, id);
        _stateId.RegisterRemoval(id, value);
        // only invalidate min/max when the last id with the extreme value is removed, lazily re-evaluated in MinValue()/MaxValue():
        if (_hasMin && value.Equals(_min) && !_idByValue.ContainsValue(value)) { _min = default; _hasMin = false; }
        if (_hasMax && value.Equals(_max) && !_idByValue.ContainsValue(value)) { _max = default; _hasMax = false; }
    }
    public void Add(int id, object value) => add(id, (T)value);
    public void Remove(int id, object value) => remove(id, (T)value);
    public void Add(int id, T value) => add(id, value);
    public void Remove(int id, T value) => remove(id, value);
    public void RegisterAddDuringStateLoad(int nodeId, object value) => Add(nodeId, value);
    public void RegisterRemoveDuringStateLoad(int nodeId, object value) => Remove(nodeId, value);
    public T? MinValue() {
        if (_hasMin) return _min;
        if (ValueCount > 0) { _min = _idByValue.Values.Min(comparer); _hasMin = true; }
        return _min;
    }
    public T? MaxValue() {
        if (_hasMax) return _max;
        if (ValueCount > 0) { _max = _idByValue.Values.Max(comparer); _hasMax = true; }
        return _max;
    }
    public ICollection<int> GetIds(T value) {
        if (_idByValue.TryGetValue(value, out var ids)) return ids;
        return EmptySet.Instance;
    }
    public int CountGreaterThan(T value, bool inclusive) {
        return _idByValue.CountGreaterThan(value, inclusive);
    }
    public IEnumerable<int> GreaterThan(T value, bool inclusive) {
        return _idByValue.GreaterThan(value, inclusive);
    }
    public int CountLessThan(T value, bool inclusive) {
        return _idByValue.CountLessThan(value, inclusive);
    }
    public IEnumerable<int> LessThan(T value, bool inclusive) {
        return _idByValue.LessThan(value, inclusive);
    }
    public IEnumerable<int> RangeSearch(T from, T to, bool fromInclusive, bool toInclusive) {
        return _idByValue.RangeSearch(from, to, fromInclusive, toInclusive);
    }
    public int InSetRangeCount(IdSet ids, T from, T to, bool fromInclusive, bool toInclusive) {
        return _idByValue.InSetRangeCount(ids, from, to, fromInclusive, toInclusive);
    }
    public IEnumerable<T> UniqueValues => _idByValue.Values;
    public int ValueCount => _idByValue.ValueCount;
    public IEnumerable<int> Ids => _valueById.Keys;
    public int IdCount => _valueById.Count;
    List<T> GetSortedValues() => _idByValue.GetSortedValues();
    List<int> GetIdsSortedByValue() => _idByValue.GetIdsSortedByValue();
    List<(int id, T from, T to)> getRangesSortedByFrom(ValueIndex<T> indexTo) {
        lock (_sortLock) {
            if (_last != null && _last.Value.fromStateId == StateId && _last.Value.toStateId == indexTo.StateId) return _last.Value.list;
            List<(int id, T from, T to)> rangesSortedByFrom = new(IdCount);
            foreach (var id in GetIdsSortedByValue()) {
                if (!_valueById.TryGetValue(id, out var from) || !indexTo._valueById.TryGetValue(id, out var to)) throw new Exception("Integrity problems with index. ");
                rangesSortedByFrom.Add((id, from, to));
            }
            _last = (rangesSortedByFrom, StateId, indexTo.StateId);
            return rangesSortedByFrom;
        }
    }
    public IEnumerable<int> WhereRangeOverlapsRange(IValueIndex<T> indexTo, T queryFrom, T queryTo, bool fromInclusive, bool toInclusive) {
        if (ValueCount == 0 || indexTo.ValueCount == 0) return [];
        if (indexTo is not ValueIndex<T> valueIndex) throw new NotSupportedException("Index is not a ValueIndex. ");
        var rangesSortedByFrom = getRangesSortedByFrom(valueIndex);
        return whereRangeOverlapsRange(rangesSortedByFrom, queryFrom, queryTo, fromInclusive, toInclusive);
    }
    // Full overlap semantics, matching NativeKvValueIndex.WhereRangeOverlapsRange:
    // the stored range must START before the query end (governed by toInclusive) and
    // END after the query start (governed by fromInclusive). The scan starts from the
    // beginning to catch ranges that span the query start, but can stop as soon as a
    // range starts past the query end since the list is sorted by "from".
    static IEnumerable<int> whereRangeOverlapsRange(List<(int id, T from, T to)> rangesSortedByFrom, T queryFrom, T queryTo, bool fromInclusive, bool toInclusive) {
        foreach (var range in rangesSortedByFrom) {
            var startsBeforeQueryEnd = toInclusive ? comparer.Compare(range.from, queryTo) <= 0 : comparer.Compare(range.from, queryTo) < 0;
            if (!startsBeforeQueryEnd) break; // sorted by "from", so every later range starts even later
            var endsAfterQueryStart = fromInclusive ? comparer.Compare(range.to, queryFrom) >= 0 : comparer.Compare(range.to, queryFrom) > 0;
            if (endsAfterQueryStart) yield return range.id;
        }
    }
    public T GetValue(int nodeId) => _valueById[nodeId];
    public bool TryGetValue(int nodeId, out T value) => _valueById.TryGetValue(nodeId, out value!);
    public bool ContainsValue(T value) => _idByValue.ContainsValue(value);
    public void WriteNewTimestampDueToRewriteHotswap(long newTimestamp, Guid walFileId) {
        var fileName = _fileKeys.Index_GetFileKey(UniqueKey);
        using var stream = _io.OpenAppend(fileName);
        stream.WriteVerifiedLong(newTimestamp);
        stream.WriteGuid(walFileId);
        PersistedTimestamp = newTimestamp;
    }
    public void SaveStateForMemoryIndexes(long logTimestamp, Guid walFileId) {
        var fileName = _fileKeys.Index_GetFileKey(UniqueKey);
        _io.DeleteFileIfItExists(fileName); // could be optimized to keep old file
        using var stream = _io.OpenAppend(fileName);
        stream.WriteVerifiedInt(_valueById.Count);
        foreach (var (id, value) in _valueById) {
            stream.WriteUInt((uint)id);
            _writeValue(value, stream);
        }
        stream.WriteVerifiedLong(logTimestamp);
        stream.WriteGuid(walFileId);
        PersistedTimestamp = logTimestamp;
    }
    public void ReadStateForMemoryIndexes(Guid walFileId) {
        PersistedTimestamp = 0;
        var fileName = _fileKeys.Index_GetFileKey(UniqueKey);
        if (_io.DoesNotExistsOrIsEmpty(fileName)) return;
        using var stream = _io.OpenRead(fileName, 0);
        var noIds = stream.ReadVerifiedInt();
        for (var i = 0; i < noIds; i++) {
            var id = stream.ReadInt();
            var value = _readValue(stream);
            add(id, value);
        }
        Guid walId = Guid.Empty;
        while (stream.More()) {
            PersistedTimestamp = stream.ReadVerifiedLong();
            walId = stream.ReadGuid();
        }
        if (walId != walFileId) throw new Exception("WAL file ID mismatch when reading index state. ");
    }
    public void CompressMemory() {

    }
    public void ClearCache() {

    }
    public void Dispose() {

    }
    int countEqual(T v) => _idByValue.CountEqual(v);
    public IdSet ReOrder(IdSet unsorted, bool descending) => _sets.OrderBy(this, unsorted, descending);
    public int CountEqual(IdSet nodeIds, T v) {
        return _sets.CountEqual(this, nodeIds, v);
        //// optimize later, count in index directly
        //var matches = Register.WhereEqual(this, v);
        //return Register.Intersection(nodeIds, matches).Count;
    }
    public int CountInRangeEqual(IdSet nodeIds, T from, T to, bool fromInclusive, bool toInclusive) {
        return _sets.CountInRange(this, nodeIds, from, to, fromInclusive, toInclusive);
        //// optimize later, count in index directly
        //var matches = Register.WhereValueInRange(this, from, to, true, true);
        //return Register.Intersection(nodeIds, matches).Count;
    }
    public IdSet Filter(IdSet nodeIds, IndexOperator op, T v) => _sets.Filter(this, nodeIds, op, v);
    public IdSet FilterInValues(IdSet nodeIds, IEnumerable<T> selectedValues) => _sets.FilterInValues(this, nodeIds, selectedValues);
    public IdSet FilterRanges(IdSet nodeIds, List<Tuple<T, T>> selectedRanges) => _sets.FilterRanges(this, nodeIds, selectedRanges);
    public IdSet FilterRangesObject(IdSet set, object from, object to) => _sets.FilterRangesObject(this, set, from, to);
    // strings are compared ordinally, matching the ordinal hashing of the dictionaries; culture based
    // comparison is not stable (it may vary per thread) and can order distinct keys as equal.
    // must be used by every sort, binary search and min/max over the values of this index (see IdByValue).
    internal static readonly IComparer<T> comparer = typeof(T) == typeof(string) ? (IComparer<T>)(object)StringComparer.Ordinal : Comparer<T>.Default;
    public int MaxCount(IndexOperator op, T value) {
        // optimized for fastest speed, not accuracy, important for performance of query engine
        if (IdCount == 0) return 0;
        if (comparer.Compare(value, MaxValue()!) > 0) return 0; // value is larger than max value in index
        if (comparer.Compare(value, MinValue()!) < 0) return 0; // value is smaller than min value in index
        return op switch {
            IndexOperator.Equal => countEqual(value),
            IndexOperator.NotEqual => IdCount - countEqual(value),
            _ => IdCount,
        };
    }
    public long PersistedTimestamp { get; private set; }
    public void FlagFirstCommit() { }
    public string FriendlyName { get; }
}
