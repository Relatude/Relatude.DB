namespace Relatude.DB.DataStores.Indexes;

/// <summary>
/// Index over string-array properties: each node maps to an array of strings and the index answers
/// equality/facet queries per unique string. Implemented by the in-memory
/// <see cref="StringArrayIndex"/> and by the persisted variants handed out by
/// <see cref="IValueIndexEngine.OpenStringArrayIndex"/>.
/// </summary>
public interface IStringArrayIndex : IValueArrayIndex<string> { }
