using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Sets;
namespace Relatude.DB.DataStores.Indexes;

public interface IValueIndex<T> : IIndex, IRangeIndex where T : notnull {
    long StateId { get; }
    int IdCount { get; }
    IEnumerable<int> Ids { get; }
    IEnumerable<T> UniqueValues { get; }
    int ValueCount { get; }
    bool ContainsValue(T value);
    int CountGreaterThan(T value, bool inclusive);
    int CountLessThan(T value, bool inclusive);
    object[] GetCacheKey(T queryValue, QueryType queryType);

    ICollection<int> GetIds(T value);

    T GetValue(int nodeId);
    bool TryGetValue(int nodeId, out T value);
    bool HasFastPointLookup { get; } // true when TryGetValue is a memory read rather than a tree/disk lookup
    int InSetRangeCount(IdSet ids, T from, T to, bool fromInclusive, bool toInclusive);
    T? MaxValue();
    T? MinValue();

    void Add(int id, T value);
    void Remove(int id, T value);

    IEnumerable<int> GreaterThan(T value, bool inclusive);
    IEnumerable<int> LessThan(T value, bool inclusive);
    IEnumerable<int> RangeSearch(T from, T to, bool fromInclusive, bool toInclusive);
    IEnumerable<int> WhereRangeOverlapsRange(IValueIndex<T> indexTo, T queryFrom, T queryTo, bool fromInclusive, bool toInclusive);

    // batch set construction; defaults preserve the enumeration path for indexes without a faster route
    ICollection<int> CollectGreaterThan(T value, bool inclusive) => IdSet.CollectUnique(GreaterThan(value, inclusive));
    ICollection<int> CollectLessThan(T value, bool inclusive) => IdSet.CollectUnique(LessThan(value, inclusive));
    ICollection<int> CollectRangeSearch(T from, T to, bool fromInclusive, bool toInclusive) => IdSet.CollectUnique(RangeSearch(from, to, fromInclusive, toInclusive));
    ICollection<int> CollectIn(IEnumerable<T> values) => IdSet.CollectUnique(values.Distinct().SelectMany(GetIds));
    ICollection<int> CollectNotEqual(T value) {
        var exclude = GetIds(value);
        ICollection<int> excludeFast;
        if (exclude is MutableSet ms && ms.TryGetBits(out var bits)) excludeFast = bits;
        else if (exclude.Count > 16) excludeFast = new HashSet<int>(exclude);
        else excludeFast = exclude;
        return IdSet.CollectUnique(whereNot(Ids, excludeFast));
        static IEnumerable<int> whereNot(IEnumerable<int> ids, ICollection<int> exclude) {
            foreach (var id in ids) if (!exclude.Contains(id)) yield return id;
        }
    }

    int CountEqual(IdSet nodeIds, T value);
    int CountInRangeEqual(IdSet nodeIds, T from, T to, bool fromInclusive, bool toInclusive);

    // whole-index counts (no node set to intersect): served from maintained counts - O(1)/O(log n)
    // for the memory/tree indexes - so they never enumerate ids
    int CountEqual(T value) => GetIds(value).Count;
    int CountInRange(T from, T to, bool fromInclusive, bool toInclusive) => Math.Max(0, CountLessThan(to, toInclusive) - CountLessThan(from, !fromInclusive));
    IdSet Filter(IdSet nodeIds, IndexOperator op, T v);
    IdSet FilterInValues(IdSet nodeIds, IEnumerable<T> selectedValues);
    IdSet FilterRanges(IdSet nodeIds, List<Tuple<T, T>> selectedRanges);
    int MaxCount(IndexOperator op, T value);
    IdSet ReOrder(IdSet unsorted, bool descending);

}
