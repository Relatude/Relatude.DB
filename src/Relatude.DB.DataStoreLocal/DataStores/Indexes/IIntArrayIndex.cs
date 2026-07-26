namespace Relatude.DB.DataStores.Indexes;

/// <summary>
/// Index over int-array properties (e.g. enum arrays): each node maps to an array of ints and the
/// index answers equality/facet queries per unique int. Implemented by the in-memory
/// <see cref="IntArrayIndex"/> and by the persisted variants handed out by
/// <see cref="IPersistedIndexStore.IntArrayIndex"/>.
/// </summary>
public interface IIntArrayIndex : IValueArrayIndex<int> { }
