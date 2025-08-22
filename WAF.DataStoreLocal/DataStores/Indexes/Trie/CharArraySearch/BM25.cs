namespace WAF.DataStores.Indexes.Trie.CharArraySearch; 
internal class BM25 {
    static internal double BM25_k1 = 1.2;
    static internal double BM25_b = 0.75;
    static internal double Score(float hitsInDoc, double docsWithHit, double docLength, double avgDocLength, double totalDocCount) {
        var IDF = Math.Log(1 + (totalDocCount - docsWithHit + 0.5d) / (docsWithHit + 0.5d));
        var nominator = hitsInDoc * (BM25_k1 + 1d);
        var denominator = hitsInDoc + BM25_k1 * (1d - BM25_b + BM25_b * docLength / avgDocLength);
        return IDF * nominator / denominator;
    }
}
