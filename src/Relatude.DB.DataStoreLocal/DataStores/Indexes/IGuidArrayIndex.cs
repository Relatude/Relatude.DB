namespace Relatude.DB.DataStores.Indexes;

/// <summary>
/// Index over guid-array properties: each node maps to an array of guids and the index answers
/// equality/facet queries per unique guid. Implemented by the in-memory <see cref="GuidArrayIndex"/>
/// and by the persisted variants handed out by <see cref="IPersistedIndexStore.GuidArrayIndex"/>.
/// </summary>
public interface IGuidArrayIndex : IValueArrayIndex<Guid> { }
