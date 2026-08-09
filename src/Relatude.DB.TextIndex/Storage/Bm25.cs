namespace Relatude.DB.DataStores.Indexes.TextIndexing;

// Same formula and constants as the in-memory trie's BM25 class (which is internal to
// DataStoreLocal), so both word index implementations rank identically.
internal static class Bm25 {
    const double K1 = 1.2;
    const double B = 0.75;
    public static double Score(float hitsInDoc, double docsWithHit, double docLength, double avgDocLength, double totalDocCount) {
        var idf = Math.Log(1 + (totalDocCount - docsWithHit + 0.5d) / (docsWithHit + 0.5d));
        var nominator = hitsInDoc * (K1 + 1d);
        var denominator = hitsInDoc + K1 * (1d - B + B * docLength / avgDocLength);
        return idf * nominator / denominator;
    }
}
