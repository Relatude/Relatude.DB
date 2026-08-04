namespace Relatude.DB.DataStores.Relations;
/// <summary>
/// Pure list ordering algorithms behind the relation move operations. The desired order is computed here,
/// then translated into single item moves (matching RelatedList.MoveTo semantics) so every step can be
/// logged, replayed and rolled back as a primitive action.
/// </summary>
public static class RelationOrderUtils {
    /// <summary>
    /// Moves the selected items by offset places (negative = towards the top). Multi select behaves like
    /// list UIs: the selection keeps its internal order and compacts against the ends of the list.
    /// </summary>
    public static List<int> MoveByOffset(IReadOnlyList<int> current, HashSet<int> selected, int offset) {
        var n = current.Count;
        var selectedInOrder = new List<int>();
        var selectedPositions = new List<int>();
        for (var i = 0; i < n; i++) {
            if (selected.Contains(current[i])) {
                selectedInOrder.Add(current[i]);
                selectedPositions.Add(i);
            }
        }
        var m = selectedInOrder.Count;
        var finalPositions = new int[m];
        for (var k = 0; k < m; k++) {
            // clamp against the end AND against previously placed selected items, both bounds are
            // strictly increasing in k so the final positions are strictly increasing too:
            finalPositions[k] = offset < 0
                ? Math.Max(k, selectedPositions[k] + offset)
                : Math.Min(n - m + k, selectedPositions[k] + offset);
        }
        var desired = new int[n];
        var taken = new bool[n];
        for (var k = 0; k < m; k++) {
            desired[finalPositions[k]] = selectedInOrder[k];
            taken[finalPositions[k]] = true;
        }
        var slot = 0;
        foreach (var id in current) {
            if (selected.Contains(id)) continue;
            while (taken[slot]) slot++;
            desired[slot] = id;
            taken[slot] = true;
        }
        return [.. desired];
    }
    /// <summary>Moves the selected items to the top or bottom, keeping their internal order.</summary>
    public static List<int> MoveToEdge(IReadOnlyList<int> current, HashSet<int> selected, bool top) {
        var selectedInOrder = new List<int>();
        var others = new List<int>();
        foreach (var id in current) (selected.Contains(id) ? selectedInOrder : others).Add(id);
        var desired = new List<int>(current.Count);
        if (top) { desired.AddRange(selectedInOrder); desired.AddRange(others); } else { desired.AddRange(others); desired.AddRange(selectedInOrder); }
        return desired;
    }
    /// <summary>Moves the selected items to a contiguous block just before or after the anchor, keeping their internal order.</summary>
    public static List<int> MoveRelativeToAnchor(IReadOnlyList<int> current, HashSet<int> selected, int anchor, bool after) {
        var selectedInOrder = new List<int>();
        var others = new List<int>();
        foreach (var id in current) (selected.Contains(id) ? selectedInOrder : others).Add(id);
        var anchorIndex = others.IndexOf(anchor);
        var desired = new List<int>(current.Count);
        desired.AddRange(others);
        desired.InsertRange(after ? anchorIndex + 1 : anchorIndex, selectedInOrder);
        return desired;
    }
    /// <summary>
    /// Returns the minimal sequence of single item moves that transforms current into desired
    /// (both must hold the same distinct ids). Each returned toIndex is the item's resulting index at
    /// the time that move is applied, matching RelatedList.MoveTo, so the sequence replays and reverses
    /// exactly. Items on the longest increasing subsequence stay put; everything else moves once.
    /// </summary>
    public static List<(int moved, int fromIndex, int toIndex)> DiffToMoves(IReadOnlyList<int> current, IReadOnlyList<int> desired) {
        var n = current.Count;
        if (n != desired.Count) throw new ArgumentException("Current and desired order must have the same length. ");
        var rank = new Dictionary<int, int>(n);
        for (var i = 0; i < n; i++) rank.Add(desired[i], i);
        var seq = new int[n];
        for (var i = 0; i < n; i++) {
            if (!rank.TryGetValue(current[i], out seq[i])) throw new ArgumentException("Current and desired order must contain the same ids. ");
        }
        var stays = longestIncreasingSubsequence(seq);
        var settled = new HashSet<int>();
        var toMove = new List<int>();
        for (var i = 0; i < n; i++) {
            if (stays[i]) settled.Add(current[i]);
            else toMove.Add(current[i]);
        }
        toMove.Sort((a, b) => rank[a].CompareTo(rank[b]));
        var working = new List<int>(current);
        var moves = new List<(int, int, int)>();
        foreach (var id in toMove) { // in desired order, so all settled items with lower rank are already in place
            var fromIndex = working.IndexOf(id);
            working.RemoveAt(fromIndex);
            var toIndex = 0;
            for (var p = working.Count - 1; p >= 0; p--) {
                if (settled.Contains(working[p]) && rank[working[p]] < rank[id]) { toIndex = p + 1; break; }
            }
            working.Insert(toIndex, id);
            settled.Add(id);
            if (fromIndex != toIndex) moves.Add((id, fromIndex, toIndex));
        }
        return moves;
    }
    static bool[] longestIncreasingSubsequence(int[] seq) {
        var n = seq.Length;
        var result = new bool[n];
        if (n == 0) return result;
        var tails = new List<int>(); // tails[k] = index in seq of the smallest tail of any increasing subsequence of length k+1
        var prev = new int[n];
        for (var i = 0; i < n; i++) {
            var lo = 0;
            var hi = tails.Count;
            while (lo < hi) {
                var mid = (lo + hi) / 2;
                if (seq[tails[mid]] < seq[i]) lo = mid + 1; else hi = mid;
            }
            prev[i] = lo > 0 ? tails[lo - 1] : -1;
            if (lo == tails.Count) tails.Add(i); else tails[lo] = i;
        }
        var k = tails[^1];
        while (k >= 0) { result[k] = true; k = prev[k]; }
        return result;
    }
}
