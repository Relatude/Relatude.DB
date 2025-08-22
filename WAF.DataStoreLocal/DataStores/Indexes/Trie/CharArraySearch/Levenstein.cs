namespace WAF.DataStores.Indexes.Trie.CharArraySearch;
class Levenshtein {
    // Inspired by "Fastenshtein": https://github.com/DanHarltey/Fastenshtein
    // Changed by Ole to fit trie traversal.
    // Result is a much faster calculation by only looking at changes from last calc
    // while traversing the tree ( it also avoiding string copying as it is using char array)
    private readonly char[] storedValue;
    private readonly int[] costs;
    public Levenshtein(char[] value) {
        storedValue = value;
        costs = new int[value.Length];
    }
    public int DistanceFrom(char[] value, int valueLength, int storedValueLength) {
        for (int i = 0; i < storedValueLength;) costs[i] = ++i;
        for (int i = 0; i < valueLength; i++) {
            int cost = i;
            int previousCost = i;
            char c1 = value[i];
            for (int j = 0; j < storedValueLength; j++) {
                int currentCost = cost;
                cost = costs[j];
                if (c1 != storedValue[j]) {
                    if (previousCost < currentCost) currentCost = previousCost;
                    if (cost < currentCost) currentCost = cost;
                    ++currentCost;
                }
                costs[j] = currentCost;
                previousCost = currentCost;
            }
        }
        return costs[storedValueLength - 1];
    }
}
