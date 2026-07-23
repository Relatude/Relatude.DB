using Relatude.DB.DataStores.Sets;
namespace Relatude.DB.DataStores.Indexes;

/// <summary>
/// Index over string-array properties: each node maps to an array of strings and the index answers
/// equality/facet queries per unique string. Implemented by the in-memory
/// <see cref="StringArrayIndex"/> and by the persisted variants handed out by
/// <see cref="IPersistedIndexStore.StringArrayIndex"/>.
/// </summary>
public interface IStringArrayIndex : IIndex {
    IdSet Filter(IdSet set, IndexOperator op, string value);
    int CountEqual(IdSet set, string value);
    bool ContainsValue(string value);
    IEnumerable<string> GetUniqueValues();
    int MaxCount(IndexOperator op, string value);
    IdSet FilterInValues(IdSet set, List<string> values);
}
