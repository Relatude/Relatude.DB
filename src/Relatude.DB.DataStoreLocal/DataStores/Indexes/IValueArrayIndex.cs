using Relatude.DB.DataStores.Sets;
namespace Relatude.DB.DataStores.Indexes;

/// <summary>
/// Index over array properties: each node maps to an array of elements and the index answers
/// equality/facet queries per unique element. Implemented by the in-memory
/// <see cref="ValueArrayIndexBase{TElement}"/> subclasses and by the persisted variants handed out
/// by <see cref="IPersistedIndexStore"/>.
/// </summary>
public interface IValueArrayIndex<TElement> : IIndex where TElement : notnull {
    IdSet Filter(IdSet set, IndexOperator op, TElement value);
    int CountEqual(IdSet set, TElement value);
    bool ContainsValue(TElement value);
    IEnumerable<TElement> GetUniqueValues();
    int MaxCount(IndexOperator op, TElement value);
    IdSet FilterInValues(IdSet set, List<TElement> values);
}
