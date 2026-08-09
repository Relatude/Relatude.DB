namespace Relatude.DB.DataStores.Indexes.TextIndexing;

/// <summary>
/// The mutable in-memory tier of the index. Every add or remove is a last-write-wins op keyed on
/// (word, nodeId): a positive value is the word's hit count in that document, <see cref="Tombstone"/>
/// cancels whatever an older segment holds. Last-write-wins makes a WAL replay that re-delivers
/// already-flushed ops idempotent. The memtable is flushed to an immutable segment by
/// <see cref="TextIndex"/>; searches see it as an overlay on top of the segments.
/// </summary>
internal sealed class MemTable {
    public const short Tombstone = -1;
    // Rough per-entry heap costs, used only to decide when the buffer is big enough to flush.
    // Deliberately on the generous side: the alternative to overestimating is a flush threshold
    // that quietly permits several times the memory it names.
    const int bytesPerTerm = 144;  // outer dictionary entry + string object + the inner dictionary
    const int bytesPerOp = 40;     // inner dictionary entry, including the slack of its growth
    const int bytesPerDocOp = 40;

    readonly Dictionary<string, Dictionary<int, short>> _terms = new(StringComparer.Ordinal);
    readonly Dictionary<int, int> _docOps = []; // word count, or -1 for document removed
    long _bytes;
    string[]? _sortedTerms;
    public bool IsEmpty => _terms.Count == 0 && _docOps.Count == 0;
    /// <summary>Estimated heap held by the buffered ops. Drives
    /// <see cref="TextIndexOptions.MemTableFlushThresholdBytes"/>, nothing else.</summary>
    public long ApproxBytes => _bytes;
    public IReadOnlyDictionary<int, int> DocOps => _docOps;
    public Dictionary<int, short>? GetOverlay(string word) => _terms.TryGetValue(word, out var d) ? d : null;
    public void Set(string word, int nodeId, short op) {
        if (!_terms.TryGetValue(word, out var d)) {
            d = [];
            _terms.Add(word, d);
            _bytes += bytesPerTerm + word.Length * 2;
            _sortedTerms = null;
        }
        if (!d.ContainsKey(nodeId)) _bytes += bytesPerOp;
        d[nodeId] = op;
    }
    public void SetDocOp(int nodeId, int wordCountOrRemove) {
        if (!_docOps.ContainsKey(nodeId)) _bytes += bytesPerDocOp;
        _docOps[nodeId] = wordCountOrRemove;
    }
    /// <summary>Words in ordinal order, cached between mutations (term scans need sorted input).</summary>
    public string[] SortedTerms {
        get {
            var s = _sortedTerms;
            if (s == null) {
                s = [.. _terms.Keys];
                Array.Sort(s, StringComparer.Ordinal);
                _sortedTerms = s;
            }
            return s;
        }
    }
    /// <summary>Flush input: terms in ordinal order, each with its ops as sorted add/tombstone arrays.</summary>
    public IEnumerable<(string term, TermPostings postings)> SortedTermPostings() {
        foreach (var word in SortedTerms) {
            var ops = _terms[word];
            var addCount = 0;
            foreach (var v in ops.Values) if (v >= 0) addCount++;
            var addIds = new int[addCount];
            var delIds = new int[ops.Count - addCount];
            int a = 0, d = 0;
            foreach (var kv in ops) {
                if (kv.Value >= 0) addIds[a++] = kv.Key;
                else delIds[d++] = kv.Key;
            }
            Array.Sort(addIds);
            Array.Sort(delIds);
            var addHits = new byte[addCount];
            for (var i = 0; i < addIds.Length; i++) addHits[i] = (byte)ops[addIds[i]];
            yield return (word, new TermPostings(addIds, addHits, delIds));
        }
    }
    public List<(int id, int wordCountOrRemove)> SortedDocOps() {
        var list = new List<(int, int)>(_docOps.Count);
        foreach (var kv in _docOps) list.Add((kv.Key, kv.Value));
        list.Sort((x, y) => x.Item1.CompareTo(y.Item1));
        return list;
    }
}

/// <summary>One term's ops within one segment: documents added (with hit counts) and documents
/// tombstoned, both sorted by node id. A node id appears in at most one of the two lists.</summary>
internal readonly struct TermPostings(int[] addIds, byte[] addHits, int[] delIds) {
    public readonly int[] AddIds = addIds;
    public readonly byte[] AddHits = addHits;
    public readonly int[] DelIds = delIds;
}
