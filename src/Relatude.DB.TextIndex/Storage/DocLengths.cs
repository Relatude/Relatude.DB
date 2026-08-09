namespace Relatude.DB.DataStores.Indexes.TextIndexing;

/// <summary>
/// Live doc-id → word-count map, the per-document statistics BM25 needs (doc length, average doc
/// length, total doc count). Kept fully in memory — it is O(documents), not O(text) — and rebuilt
/// from the segments' doc areas on open. Mirrors the trie's DocWordCounts, except Set is an upsert
/// so a WAL replay that re-delivers an add the index already contains stays idempotent.
/// </summary>
internal sealed class DocLengths {
    readonly Dictionary<int, int> _counts = [];
    long _total; // exact running total, average is derived to avoid float drift
    public int DocCount => _counts.Count;
    public double AverageWordCount => _counts.Count > 0 ? (double)_total / _counts.Count : 0d;
    public bool TryGet(int id, out int wordCount) => _counts.TryGetValue(id, out wordCount);
    public void Set(int id, int wordCount) {
        if (_counts.TryGetValue(id, out var old)) _total += wordCount - old;
        else _total += wordCount;
        _counts[id] = wordCount;
    }
    public void Remove(int id) {
        if (_counts.Remove(id, out var old)) _total -= old;
    }
    public IEnumerable<KeyValuePair<int, int>> All => _counts;
}
