namespace Relatude.DB.DataStores.Indexes.Trie.CharArraySearch;
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
    // Same as above, but also returns the minimum of the last cost row.
    // No word starting with "value" can have a distance lower than rowMin,
    // so it is a safe bound for pruning trie branches (unlike the final distance, which can decrease again as chars are appended).
    public int DistanceFrom(char[] value, int valueLength, int storedValueLength, out int rowMin) {
        for (int i = 0; i < storedValueLength;) costs[i] = ++i;
        rowMin = 0;
        for (int i = 0; i < valueLength; i++) {
            int cost = i;
            int previousCost = i;
            int min = i + 1; // cost of deleting all chars of value[0..i]
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
                if (currentCost < min) min = currentCost;
            }
            rowMin = min;
        }
        return costs[storedValueLength - 1];
    }
}
