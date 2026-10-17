using System.Runtime.InteropServices;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Sets;
namespace Relatude.DB.DataStores.Indexes;
internal sealed class CaseInsensitiveUniqueIndex(IValueIndex<string> index) : IValueIndex<string>, IValueIndexWrapper<string> {
    readonly IValueIndex<string> _i = index;
    Dictionary<string, int>? _idCountByValueIgnoringCase;

    public bool ContainsValueIgnoringCase(string value) {
        var counts = _idCountByValueIgnoringCase ??= countIdsByValueIgnoringCase();
        return counts.ContainsKey(value);
    }
    Dictionary<string, int> countIdsByValueIgnoringCase() {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _i.Entries) CollectionsMarshal.GetValueRefOrAddDefault(counts, e.Value, out _)++;
        return counts;
    }
    void count(object value, int delta) {
        if (_idCountByValueIgnoringCase == null || value is not string s) return;
        ref var n = ref CollectionsMarshal.GetValueRefOrAddDefault(_idCountByValueIgnoringCase, s, out _);
        n += delta;
        if (n <= 0) _idCountByValueIgnoringCase.Remove(s);
    }
    void forgetCounts() => _idCountByValueIgnoringCase = null;

    public IValueIndex<string> DequeueAndGetInner() => _i is IValueIndexWrapper<string> w ? w.DequeueAndGetInner() : _i;

    public void Add(int id, object value) { _i.Add(id, value); count(value, 1); }
    public void Remove(int id, object value) { _i.Remove(id, value); count(value, -1); }
    public void Add(int id, string value) { _i.Add(id, value); count(value, 1); }
    public void Remove(int id, string value) { _i.Remove(id, value); count(value, -1); }
    public void RegisterAddDuringStateLoad(int id, object value) { forgetCounts(); _i.RegisterAddDuringStateLoad(id, value); }
    public void RegisterRemoveDuringStateLoad(int id, object value) { forgetCounts(); _i.RegisterRemoveDuringStateLoad(id, value); }
    public void CompleteStateLoad() { forgetCounts(); _i.CompleteStateLoad(); }
    public void ReadStateForMemoryIndexes(Guid walFileId) { forgetCounts(); _i.ReadStateForMemoryIndexes(walFileId); }
    public void SaveStateForMemoryIndexes(long logTimestamp, Guid walFileId) => _i.SaveStateForMemoryIndexes(logTimestamp, walFileId);
    public void WriteNewTimestampDueToRewriteHotswap(long newTimestamp, Guid walFileId) => _i.WriteNewTimestampDueToRewriteHotswap(newTimestamp, walFileId);
    public void ClearCache() => _i.ClearCache();
    public void CompressMemory() => _i.CompressMemory();
    public void Dispose() { forgetCounts(); _i.Dispose(); }
    public long PersistedTimestamp => _i.PersistedTimestamp;
    public void FlagFirstCommit() => _i.FlagFirstCommit();
    public string UniqueKey => _i.UniqueKey;
    public string FriendlyName => _i.FriendlyName;

    public long StateId => _i.StateId;
    public int IdCount => _i.IdCount;
    public IEnumerable<int> Ids => _i.Ids;
    public IEnumerable<string> UniqueValues => _i.UniqueValues;
    public int ValueCount => _i.ValueCount;
    public bool ContainsValue(string value) => _i.ContainsValue(value);
    public int CountEqual(IdSet nodeIds, string value) => _i.CountEqual(nodeIds, value);
    public int CountEqual(string value) => _i.CountEqual(value);
    public int CountInRange(string from, string to, bool fromInclusive, bool toInclusive) => _i.CountInRange(from, to, fromInclusive, toInclusive);
    public int CountGreaterThan(string value, bool inclusive) => _i.CountGreaterThan(value, inclusive);
    public int CountInRangeEqual(IdSet nodeIds, string from, string to, bool fromInclusive, bool toInclusive) => _i.CountInRangeEqual(nodeIds, from, to, fromInclusive, toInclusive);
    public int CountLessThan(string value, bool inclusive) => _i.CountLessThan(value, inclusive);
    public IdSet Filter(IdSet nodeIds, IndexOperator op, string v) => _i.Filter(nodeIds, op, v);
    public IdSet FilterInValues(IdSet nodeIds, IEnumerable<string> selectedValues) => _i.FilterInValues(nodeIds, selectedValues);
    public IdSet FilterRanges(IdSet nodeIds, List<Tuple<string, string>> selectedRanges) => _i.FilterRanges(nodeIds, selectedRanges);
    public IdSet FilterRangesObject(IdSet set, object from, object to) => _i.FilterRangesObject(set, from, to);
    public object[] GetCacheKey(string queryValue, QueryType queryType) => _i.GetCacheKey(queryValue, queryType);
    public ICollection<int> GetIds(string value) => _i.GetIds(value);
    public string GetValue(int nodeId) => _i.GetValue(nodeId);
    public bool TryGetValue(int nodeId, out string value) => _i.TryGetValue(nodeId, out value);
    public bool HasFastPointLookup => _i.HasFastPointLookup;
    public IEnumerable<KeyValuePair<int, string>> Entries => _i.Entries;
    public IEnumerable<int> GreaterThan(string value, bool inclusive) => _i.GreaterThan(value, inclusive);
    public int InSetRangeCount(IdSet ids, string from, string to, bool fromInclusive, bool toInclusive) => _i.InSetRangeCount(ids, from, to, fromInclusive, toInclusive);
    public IEnumerable<int> LessThan(string value, bool inclusive) => _i.LessThan(value, inclusive);
    public int MaxCount(IndexOperator op, string value) => _i.MaxCount(op, value);
    public string? MaxValue() => _i.MaxValue();
    public string? MinValue() => _i.MinValue();
    public IEnumerable<int> RangeSearch(string from, string to, bool fromInclusive, bool toInclusive) => _i.RangeSearch(from, to, fromInclusive, toInclusive);
    public ICollection<int> CollectGreaterThan(string value, bool inclusive) => _i.CollectGreaterThan(value, inclusive);
    public ICollection<int> CollectLessThan(string value, bool inclusive) => _i.CollectLessThan(value, inclusive);
    public ICollection<int> CollectRangeSearch(string from, string to, bool fromInclusive, bool toInclusive) => _i.CollectRangeSearch(from, to, fromInclusive, toInclusive);
    public ICollection<int> CollectIn(IEnumerable<string> values) => _i.CollectIn(values);
    public ICollection<int> CollectNotEqual(string value) => _i.CollectNotEqual(value);
    public IdSet ReOrder(IdSet unsorted, bool descending) => _i.ReOrder(unsorted, descending);
    public IEnumerable<int> WhereRangeOverlapsRange(IValueIndex<string> indexTo, string queryFrom, string queryTo, bool fromInclusive, bool toInclusive)
        => _i.WhereRangeOverlapsRange(indexTo is CaseInsensitiveUniqueIndex c ? c._i : indexTo, queryFrom, queryTo, fromInclusive, toInclusive);
}
