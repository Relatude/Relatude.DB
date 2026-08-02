using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Sets;

namespace Relatude.DB.DataStores.Definitions;
/// <summary>
/// BFS based graph traversal over a single relation index.
/// All methods work in internal int id space and assume the caller holds the store read lock.
/// </summary>
internal static class GraphTraversalUtils {
    public const int DefaultMaxVisited = 1_000_000;
    public const int DefaultMaxLevelShortestPath = 1_000;

    static void throwBudgetExceeded(int maxVisited)
        => throw new Exception($"Graph traversal exceeded the budget of {maxVisited} visited nodes. Reduce maxLevel, narrow the seed set or raise maxVisited. ");

    // The effective index direction of the first probe. The property direction is the base;
    // Reverse flips it. For symmetric indexes the flag is ignored by the index itself.
    static bool effectiveFlag(bool fromTargetToSource, GraphDirection direction)
        => direction == GraphDirection.Reverse ? !fromTargetToSource : fromTargetToSource;

    /// <summary>
    /// Multi source BFS. Returns every id whose minimum distance from any seed is within [minLevel, maxLevel].
    /// Seeds are at level 0. Cycle and self-loop safe. Result contains no duplicates,
    /// in BFS discovery order (level ascending).
    /// </summary>
    internal static ICollection<int> CollectWithin(IdSet seeds, Relation relation, bool fromTargetToSource, GraphDirection direction, int minLevel, int maxLevel, int maxVisited) {
        if (minLevel < 0) throw new ArgumentException("minLevel cannot be negative. ");
        if (maxLevel < minLevel) throw new ArgumentException("maxLevel cannot be less than minLevel. ");
        var flag = effectiveFlag(fromTargetToSource, direction);
        var bothWays = direction == GraphDirection.Both && !relation.IsSymmetric;
        var visited = new HashSet<int>();
        var frontier = new List<int>(seeds.Count);
        foreach (var id in seeds.Enumerate()) {
            if (visited.Add(id)) frontier.Add(id);
        }
        var result = new List<int>();
        if (minLevel == 0) result.AddRange(frontier);
        for (var level = 1; level <= maxLevel && frontier.Count > 0; level++) {
            var next = new List<int>();
            foreach (var id in frontier) {
                expand(relation.GetRelated(id, flag));
                if (bothWays) expand(relation.GetRelated(id, !flag));
            }
            frontier = next;
            void expand(IdSet neighbors) {
                foreach (var n in neighbors.Enumerate()) {
                    if (!visited.Add(n)) continue;
                    if (visited.Count > maxVisited) throwBudgetExceeded(maxVisited);
                    next.Add(n);
                    if (level >= minLevel) result.Add(n);
                }
            }
        }
        return IdSet.CollectUnique(result);
    }

    /// <summary>
    /// Bidirectional BFS shortest path. Returns the node ids of one shortest path
    /// [from, ..., to] (inclusive), or null when no path exists within maxLevel edges.
    /// Deterministic: expansion follows IdSet enumeration order, first discovery wins.
    /// </summary>
    internal static int[]? TryShortestPath(int from, int to, Relation relation, bool fromTargetToSource, GraphDirection direction, int maxLevel, int maxVisited) {
        if (maxLevel < 0) throw new ArgumentException("maxLevel cannot be negative. ");
        if (from == to) return [from];
        if (maxLevel == 0) return null;
        var flag = effectiveFlag(fromTargetToSource, direction);
        var bothWays = direction == GraphDirection.Both && !relation.IsSymmetric;
        var parentTowardsFrom = new Dictionary<int, int>();
        var parentTowardsTo = new Dictionary<int, int>();
        var visitedF = new HashSet<int> { from };
        var visitedB = new HashSet<int> { to };
        var frontierF = new List<int> { from };
        var frontierB = new List<int> { to };
        var depthF = 0;
        var depthB = 0;
        while (frontierF.Count > 0 && frontierB.Count > 0) {
            if (depthF + depthB >= maxLevel) return null;
            var forward = frontierF.Count <= frontierB.Count; // expand the smaller frontier
            var frontier = forward ? frontierF : frontierB;
            var visitedOwn = forward ? visitedF : visitedB;
            var visitedOther = forward ? visitedB : visitedF;
            var parentOwn = forward ? parentTowardsFrom : parentTowardsTo;
            var next = new List<int>();
            foreach (var id in frontier) {
                // the backward search must follow edges in reverse:
                var probe = forward ? flag : !flag;
                if (tryExpand(relation.GetRelated(id, probe), id, out var path)) return path;
                if (bothWays && tryExpand(relation.GetRelated(id, !probe), id, out path)) return path;
                bool tryExpand(IdSet neighbors, int current, out int[]? found) {
                    foreach (var n in neighbors.Enumerate()) {
                        if (visitedOwn.Contains(n)) continue;
                        parentOwn[n] = current;
                        if (visitedOther.Contains(n)) { found = buildPath(n, parentTowardsFrom, parentTowardsTo, from, to); return true; }
                        visitedOwn.Add(n);
                        if (visitedF.Count + visitedB.Count > maxVisited) throwBudgetExceeded(maxVisited);
                        next.Add(n);
                    }
                    found = null;
                    return false;
                }
            }
            if (forward) { frontierF = next; depthF++; } else { frontierB = next; depthB++; }
        }
        return null;
    }

    static int[] buildPath(int meet, Dictionary<int, int> parentTowardsFrom, Dictionary<int, int> parentTowardsTo, int from, int to) {
        var path = new List<int>();
        var cur = meet;
        while (cur != from) { path.Add(cur); cur = parentTowardsFrom[cur]; }
        path.Add(from);
        path.Reverse();
        cur = meet;
        while (cur != to) { cur = parentTowardsTo[cur]; path.Add(cur); }
        return [.. path];
    }
}
