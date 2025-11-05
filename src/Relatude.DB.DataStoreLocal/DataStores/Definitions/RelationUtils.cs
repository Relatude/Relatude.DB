using Relatude.DB.DataStores.Relations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.DB.DataStores.Definitions;
public static class RelationUtils {
    /// <summary>
    /// Returns true if adding a relation from -> to would create a cycle.
    /// If a cycle would occur, 'loop' contains a concrete cycle path like [from, to, ..., from].
    /// Assumes Relation is a directed graph:
    /// - GetRelatedTo(id): outgoing neighbors of id (id -> neighbor)
    /// - GetRelatedFrom(id): incoming neighbors of id (neighbor -> id)
    /// </summary>
    internal static bool WillCauseCircularReference(int from, int to, IRelationIndex relation, out IEnumerable<int>? loop) {
        // Trivial self-loop case
        if (from == to) {
            loop = new[] { from, from };
            return true;
        }

        // If there's a path from 'to' back to 'from' (following outgoing edges),
        // adding 'from -> to' will close a cycle.
        // We'll do an iterative DFS from 'to' and track parents to reconstruct a path.

        var stack = new Stack<int>();
        var visited = new HashSet<int>();
        var parent = new Dictionary<int, int?>(); // child -> parent (null for root)

        stack.Push(to);
        visited.Add(to);
        parent[to] = null;

        while (stack.Count > 0) {
            int current = stack.Pop();

            foreach (var next in relation.Get(current, false).Enumerate()) {
                if (next == from) {
                    // Found a path to 'from'. Reconstruct cycle:
                    // path: from  <- ... <- current <- to
                    // cycle: from -> to -> ... -> from
                    var path = ReconstructPath(parent, start: to, endParent: current);

                    // Compose [from, to, ..., from]
                    var cycle = new List<int>(capacity: path.Count + 2)
                    {
                        from
                    };
                    cycle.AddRange(path); // starts with 'to'
                    cycle.Add(from);

                    loop = cycle;
                    return true;
                }

                if (visited.Add(next)) {
                    parent[next] = current;
                    stack.Push(next);
                }
            }
        }

        loop = null;
        return false;
    }

    /// <summary>
    /// Reconstructs a path from 'start' to 'endParent -> ... -> start' using the parent map.
    /// Returns a forward path beginning at 'start' (which is 'to') and ending at 'endParent'.
    /// </summary>
    private static List<int> ReconstructPath(Dictionary<int, int?> parent, int start, int endParent) {
        var reversed = new List<int>();
        int? cur = endParent;

        // Walk parents until we hit the root (which should be 'start')
        while (cur is int v) {
            reversed.Add(v);
            cur = parent.TryGetValue(v, out var p) ? p : null;
        }

        // reversed now holds: endParent, ..., start
        reversed.Reverse();

        // Insert the root 'start' at the beginning to produce [start, ..., endParent]
        if (reversed.Count == 0 || reversed[0] != start)
            reversed.Insert(0, start);

        return reversed;
    }
}
