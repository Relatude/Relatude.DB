using WAF.DataStores.Sets;
namespace WAF.DataStores.Indexes;
/// <summary>
/// Interface for indexes that can filter ranges of values.
/// </summary>
public interface IRangeIndex {
    IdSet FilterRangesObject(IdSet set, object from, object to);
}
