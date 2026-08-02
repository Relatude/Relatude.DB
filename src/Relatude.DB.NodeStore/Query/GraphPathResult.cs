namespace Relatude.DB.Query;

/// <summary>
/// Result of a ShortestPath query: one shortest path between two nodes over a relation.
/// </summary>
public sealed class GraphPathResult<T> {
    public GraphPathResult(bool found, Guid[] nodeIds, T[] nodes, double durationMs) {
        Found = found;
        NodeIds = nodeIds;
        Nodes = nodes;
        DurationMs = durationMs;
    }
    /// <summary>True when a path was found within the given maxLevel. </summary>
    public bool Found { get; }
    /// <summary>Node ids along the path, from -> to, both inclusive. Empty when not found. </summary>
    public Guid[] NodeIds { get; }
    /// <summary>Materialized nodes along the path, from -> to, both inclusive. Empty when not found. </summary>
    public T[] Nodes { get; }
    /// <summary>Number of edges in the path. 0 when not found or when from == to. </summary>
    public int Length => Found ? NodeIds.Length - 1 : 0;
    public double DurationMs { get; }
}
