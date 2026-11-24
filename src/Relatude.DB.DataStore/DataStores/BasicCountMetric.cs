namespace Relatude.DB.DataStores;
/// <summary>
/// What a store did between <see cref="IDataStore.StartBasicCountMetric"/> and <see cref="IDataStore.EndBasicCountMetric"/>,
/// whoever asked it: the queries run, the nodes read and how many of those were not in the node cache. Zeros when another
/// count overlapped this one (<see cref="Overlapped"/>), as the two cannot be told apart.
/// </summary>
public sealed record BasicCountMetric(long Queries, long NodesRetrieved, long NodesNotCached, bool Overlapped) {
    public static readonly BasicCountMetric None = new(0, 0, 0, false);
}
