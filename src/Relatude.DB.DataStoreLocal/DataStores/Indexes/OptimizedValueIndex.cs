using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Indexes;
/// <summary>
/// A layer to optimize and eliminate redundant add/remove operations on an underlying IValueIndex.
/// </summary>
/// <typeparam name="T"></typeparam>
/// <param name="index"></param>
public class OptimizedValueIndex<T>(IValueIndex<T> index) : IValueIndex<T> where T : notnull {
    readonly IValueIndex<T> _i = index;
    readonly AddRemoveOptimization _o = new(index);

    public string UniqueKey => _i.UniqueKey;

    public void Add(int id, object value) => _o.Add(id, value);
    public void Remove(int id, object value) => _o.Remove(id, value);
    public void RegisterAddDuringStateLoad(int id, object value) => _o.RegisterAddDuringStateLoad(id, value);
    public void RegisterRemoveDuringStateLoad(int id, object value) => _o.RegisterRemoveDuringStateLoad(id, value);

    public long StateId { get { _o.Dequeue(); return _i.StateId; } }
    public int IdCount { get { _o.Dequeue(); return _i.IdCount; } }
    public IEnumerable<int> Ids { get { _o.Dequeue(); return _i.Ids; } }
    public IEnumerable<T> UniqueValues { get { _o.Dequeue(); return _i.UniqueValues; } }
    public int ValueCount { get { _o.Dequeue(); return _i.ValueCount; } }
    public void ClearCache() { _o.Dequeue(); _i.ClearCache(); }
    public void CompressMemory() { _o.Dequeue(); _i.CompressMemory(); }
    public bool ContainsValue(T value) { _o.Dequeue(); return _i.ContainsValue(value); }
    public int CountEqual(IdSet nodeIds, T value) { _o.Dequeue(); return _i.CountEqual(nodeIds, value); }
    public int CountGreaterThan(T value, bool inclusive) { _o.Dequeue(); return _i.CountGreaterThan(value, inclusive); }
    public int CountInRangeEqual(IdSet nodeIds, T from, T to, bool fromInclusive, bool toInclusive) { _o.Dequeue(); return _i.CountInRangeEqual(nodeIds, from, to, fromInclusive, toInclusive); }
    public int CountLessThan(T value, bool inclusive) { _o.Dequeue(); return _i.CountLessThan(value, inclusive); }
    public void Dispose() { _o.Dequeue(); _i.Dispose(); }

    public IdSet Filter(IdSet nodeIds, IndexOperator op, T v) { _o.Dequeue(); return _i.Filter(nodeIds, op, v); }
    public IdSet FilterInValues(IdSet nodeIds, IEnumerable<T> selectedValues) { _o.Dequeue(); return _i.FilterInValues(nodeIds, selectedValues); }
    public IdSet FilterRanges(IdSet nodeIds, List<Tuple<T, T>> selectedRanges) { _o.Dequeue(); return _i.FilterRanges(nodeIds, selectedRanges); }
    public IdSet FilterRangesObject(IdSet set, object from, object to) { _o.Dequeue(); return _i.FilterRangesObject(set, from, to); }
    public object[] GetCacheKey(T queryValue, QueryType queryType) { _o.Dequeue(); return _i.GetCacheKey(queryValue, queryType); }
    public ICollection<int> GetIds(T value) { _o.Dequeue(); return _i.GetIds(value); }
    public T GetValue(int nodeId) { _o.Dequeue(); return _i.GetValue(nodeId); }
    public IEnumerable<int> GreaterThan(T value, bool inclusive) { _o.Dequeue(); return _i.GreaterThan(value, inclusive); }
    public int InSetRangeCount(IdSet ids, T from, T to, bool fromInclusive, bool toInclusive) { _o.Dequeue(); return _i.InSetRangeCount(ids, from, to, fromInclusive, toInclusive); }
    public IEnumerable<int> LessThan(T value, bool inclusive) { _o.Dequeue(); return _i.LessThan(value, inclusive); }
    public int MaxCount(IndexOperator op, T value) { _o.Dequeue(); return _i.MaxCount(op, value); }
    public T? MaxValue() { _o.Dequeue(); return _i.MaxValue(); }
    public T? MinValue() { _o.Dequeue(); return _i.MinValue(); }
    public IEnumerable<int> RangeSearch(T from, T to, bool fromInclusive, bool toInclusive) { _o.Dequeue(); return _i.RangeSearch(from, to, fromInclusive, toInclusive); }
    public void WriteNewTimestampDueToRewriteHotswap(long newTimestamp, Guid walFileId) { _o.Dequeue(); _i.WriteNewTimestampDueToRewriteHotswap(newTimestamp, walFileId); }
    public void ReadStateForMemoryIndexes(Guid walFileId) { _o.Dequeue(); _i.ReadStateForMemoryIndexes(walFileId); }
    public void SaveStateForMemoryIndexes(long logTimestamp, Guid walFileId) { _o.Dequeue(); _i.SaveStateForMemoryIndexes(logTimestamp, walFileId); }
    public IdSet ReOrder(IdSet unsorted, bool descending) { _o.Dequeue(); return _i.ReOrder(unsorted, descending); }
    public IEnumerable<int> WhereRangeOverlapsRange(IValueIndex<T> indexTo, T queryFrom, T queryTo, bool fromInclusive, bool toInclusive) { _o.Dequeue(); return _i.WhereRangeOverlapsRange(indexTo, queryFrom, queryTo, fromInclusive, toInclusive); }
    public long PersistedTimestamp { get { _o.Dequeue(); return _i.PersistedTimestamp; } set { _o.Dequeue(); _i.PersistedTimestamp = value; } }
    public string FriendlyName => "Optimized " + _i.FriendlyName;
}
