namespace Relatude.DB.DataStores.Indexes.TextIndexing;

/// <summary>
/// Live doc-id → word-count map, the per-document statistics BM25 needs (doc length, average doc
/// length, total doc count). Kept fully in memory — it is O(documents), not O(text) — and rebuilt
/// from the segments' doc areas on open. Mirrors the trie's DocWordCounts, except Set is an upsert
/// so a WAL replay that re-delivers an add the index already contains stays idempotent.
/// </summary>
internal sealed class DocLengths {
    readonly ValueByIdMap<int> _counts = new(); // dense array once the ids allow it: a few bytes per document instead of a dictionary entry
    long _total; // exact running total, average is derived to avoid float drift
    public int DocCount => _counts.Count;
    public double AverageWordCount => _counts.Count > 0 ? (double)_total / _counts.Count : 0d;
    /// <summary>The words of every document added up - the most postings the index can hold. What a count is estimated from.</summary>
    public long TotalWordCount => _total;
    public bool TryGet(int id, out int wordCount) => _counts.TryGetValue(id, out wordCount);
    public void Set(int id, int wordCount) {
        if (_counts.TryGetValue(id, out var old)) _total += wordCount - old;
        else _total += wordCount;
        _counts.Set(id, wordCount);
    }
    public void Remove(int id) {
        if (!_counts.TryGetValue(id, out var old)) return;
        _total -= old;
        _counts.Remove(id);
    }
    public IEnumerable<KeyValuePair<int, int>> All => _counts;
}
