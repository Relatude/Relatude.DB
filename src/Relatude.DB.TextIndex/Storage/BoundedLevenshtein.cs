namespace Relatude.DB.DataStores.Indexes.TextIndexing;

internal static class BoundedLevenshtein {
    /// <summary>
    /// Levenshtein distance with an upper bound: returns <paramref name="maxDist"/> + 1 as soon as
    /// the distance provably exceeds the bound, so dictionary scans can reject candidates cheaply.
    /// </summary>
    public static int Distance(string a, string b, int maxDist) {
        if (Math.Abs(a.Length - b.Length) > maxDist) return maxDist + 1;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var costs = new int[b.Length];
        for (var i = 0; i < b.Length;) costs[i] = ++i;
        for (var i = 0; i < a.Length; i++) {
            var cost = i;
            var previousCost = i;
            var rowMin = i + 1;
            var c1 = a[i];
            for (var j = 0; j < b.Length; j++) {
                var currentCost = cost;
                cost = costs[j];
                if (c1 != b[j]) {
                    if (previousCost < currentCost) currentCost = previousCost;
                    if (cost < currentCost) currentCost = cost;
                    ++currentCost;
                }
                costs[j] = currentCost;
                previousCost = currentCost;
                if (currentCost < rowMin) rowMin = currentCost;
            }
            if (rowMin > maxDist) return maxDist + 1; // no suffix can bring the distance back down
        }
        return costs[b.Length - 1];
    }
}
