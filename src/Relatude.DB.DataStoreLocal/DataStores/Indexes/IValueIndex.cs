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
    IEnumerable<int> GreaterThan(T value, bool inclusive);
    int InSetRangeCount(IdSet ids, T from, T to, bool fromInclusive, bool toInclusive);
    IEnumerable<int> LessThan(T value, bool inclusive);
    T? MaxValue();
    T? MinValue();

    void Add(int id, T value);
    void Remove(int id, T value);

    IEnumerable<int> RangeSearch(T from, T to, bool fromInclusive, bool toInclusive);
    IEnumerable<int> WhereRangeOverlapsRange(IValueIndex<T> indexTo, T queryFrom, T queryTo, bool fromInclusive, bool toInclusive);
    int CountEqual(IdSet nodeIds, T value);
    int CountInRangeEqual(IdSet nodeIds, T from, T to, bool fromInclusive, bool toInclusive);
    IdSet Filter(IdSet nodeIds, IndexOperator op, T v, QueryContext ctx);
    IdSet FilterInValues(IdSet nodeIds, IEnumerable<T> selectedValues);
    IdSet FilterRanges(IdSet nodeIds, List<Tuple<T, T>> selectedRanges);
    int MaxCount(IndexOperator op, T value);
    IdSet ReOrder(IdSet unsorted, bool descending);

}
