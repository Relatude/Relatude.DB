using Relatude.DB.Common;
using Relatude.DB.DataStores.Indexes.TextIndexing;
using Relatude.DB.DataStores.Sets;

namespace Relatude.DB.DataStores.Indexes;

/// <summary>
/// A persistent, disk-based word index owned by <see cref="TextIndexEngine"/>, functionally
/// equivalent to the in-memory trie index (BM25 ranking with identical scoring, and/or search,
/// prefix, infix and fuzzy terms, spelling suggestions, the same word-length gates and evaluation
/// caps) while keeping the bulk of its data — the postings — on disk.
///
/// <para><b>Storage.</b> A small log-structured merge tree: writes go to an in-memory
/// <see cref="MemTable"/> as last-write-wins ops keyed on (word, nodeId) — a positive value is the
/// word's hit count in that document, a tombstone cancels older segments — and are flushed into
/// immutable sorted segment files. Newer sources win over older ones, so an update is simply
/// remove + add, and replaying already-applied WAL ops is idempotent. Similar-sized adjacent
/// segments are merged into a size ladder, keeping the segment count logarithmic.</para>
///
/// <para><b>Reads.</b> Each segment's sorted term dictionary is navigated through an in-memory
/// skip level (one first-term per prefix-compressed block); a lookup binary-searches that, decodes
/// one block, and reads the term's postings with a positional read — any number of searches can
/// read concurrently. Decoded blocks and per-word disk-merged postings live in a shared LRU cache
/// with a configurable byte budget (<see cref="TextIndexOptions.MaxCacheBytes"/>); the memtable is
/// applied as an overlay at read time so writes never invalidate that cache, only flushes do.
/// What stays permanently in memory is O(unique words / documents), not O(text): the skip levels
/// and the per-document word counts BM25 needs.</para>
///
/// <para><b>Durability.</b> Like the Lucene-backed index, this index carries its own persisted
/// position: the manifest records the live segments together with the timestamp and WAL file id
/// they correspond to, and is replaced atomically after the segments are fsynced. On open, a
/// missing, corrupt or foreign-WAL manifest resets the index to empty (position 0), and the
/// startup loader replays exactly the missing part of the WAL.</para>
/// </summary>
public class TextIndex : IWordIndex {
    const byte cacheKindPostings = 1;
    static int _ownerCounter;
    readonly int _ownerId = Interlocked.Increment(ref _ownerCounter);
    readonly TextIndexEngine _engine; // owns this index; single source of the current WAL file id
    readonly SetRegister _sets;
    readonly StateIdValueTracker<string> _stateId;
    readonly TextIndexOptions _options;
    readonly TextIndexCache _cache;
    readonly string _folder;
    readonly List<Segment> _segments = []; // oldest first; later entries win during reads
    MemTable _mem = new();
    DocLengths _docs = new();
    long _nextSegmentId = 1;
    long _generation; // bumped when the segment list changes; stamps the merged-postings cache keys
    long _persistedTimestamp;
    Guid _persistedWalFileId;
    public int MinWordLength { get; }
    public int MaxWordLength { get; }
    public bool PrefixSearch { get; }
    public bool InfixSearch { get; }
    internal TextIndex(SetRegister sets, string indexId, string friendlyName, string folderPath, WordIndexOptions options, TextIndexOptions indexOptions, TextIndexCache cache, TextIndexEngine engine) {
        _sets = sets;
        _stateId = new();
        _engine = engine;
        _options = indexOptions;
        _cache = cache;
        _folder = folderPath;
        UniqueKey = indexId;
        FriendlyName = friendlyName;
        MinWordLength = Math.Max(1, options.MinWordLength); // 0 would allow empty words, crashing the tokenizer
        MaxWordLength = Math.Max(MinWordLength, options.MaxWordLength);
        PrefixSearch = options.PrefixSearch;
        InfixSearch = options.InfixSearch;
        open();
    }
    public string UniqueKey { get; }
    public string FriendlyName { get; }

    /// <summary>The timestamp of the last durable manifest; 0 for a fresh (or reset) index, which
    /// makes the startup loader rebuild it from the whole WAL.</summary>
    public long PersistedTimestamp => _persistedTimestamp;
    // The first-commit protocol is for indexes that borrow their engine's timestamp; this index
    // carries its own (in the manifest), so there is nothing to flag.
    public void FlagFirstCommit() { }
    // Data lives in the segment files, not in the memory-index state files.
    public void ReadStateForMemoryIndexes(Guid walFileId) { }
    public void SaveStateForMemoryIndexes(long logTimestamp, Guid walFileId) { }
    // After a log rewrite hot-swap the engine re-stamps every index in one call.
    public void WriteNewTimestampDueToRewriteHotswap(long newTimestamp, Guid walFileId) { }

    // ---- writes --------------------------------------------------------------------------------

    public void Add(int nodeId, object value) {
        var text = (string)value;
        if (!string.IsNullOrEmpty(text)) {
            var entries = IndexUtil.CleanToStrings(text, MinWordLength, MaxWordLength, out var wordCount);
            _docs.Set(nodeId, wordCount);
            _mem.SetDocOp(nodeId, wordCount);
            foreach (var kv in entries) _mem.Set(kv.Key, nodeId, kv.Value);
        }
        _stateId.RegisterAddition(nodeId, text ?? string.Empty); // invalidates cached result sets in the SetRegister
    }
    public void Remove(int nodeId, object value) {
        var text = (string)value;
        if (!string.IsNullOrEmpty(text)) {
            var entries = IndexUtil.CleanToStrings(text, MinWordLength, MaxWordLength, out _);
            _docs.Remove(nodeId);
            _mem.SetDocOp(nodeId, -1);
            foreach (var kv in entries) _mem.Set(kv.Key, nodeId, MemTable.Tombstone);
        }
        _stateId.RegisterRemoval(nodeId, text ?? string.Empty);
    }
    public void RegisterAddDuringStateLoad(int nodeId, object value) => Add(nodeId, value);
    public void RegisterRemoveDuringStateLoad(int nodeId, object value) => Remove(nodeId, value);

    // ---- searches ------------------------------------------------------------------------------

    public IdSet SearchForIdSetUnranked(TermSet value, bool orSearch, int maxWordsEval) {
        if (value.Terms.Length == 0) return IdSet.Empty;
        return _sets.SearchForIdSetUnranked(_stateId.Current, value, orSearch, () => searchIdsUnsorted(value, orSearch, maxWordsEval));
    }
    ICollection<int> searchIdsUnsorted(TermSet expressions, bool orSearch, int maxWordsEval) {
        // fast path: a single plain term matches at most one word whose merged postings already
        // hold distinct node ids
        if (expressions.Terms.Length == 1 && expressions.Terms[0] is { Prefix: false, Infix: false, Fuzzy: false }) {
            var singleWord = expressions.Terms[0].Word;
            if (singleWord.Length > MaxWordLength) singleWord = singleWord[..MaxWordLength];
            if (singleWord.Length < MinWordLength) return []; // words this short are never indexed
            var view = getView(singleWord);
            var set = new HashSet<int>(view.Count);
            foreach (var (nodeId, _) in view.Enumerate()) set.Add(nodeId);
            return set;
        }
        List<HashSet<int>> results = [];
        foreach (var expression in expressions.Terms) {
            var word = expression.Word;
            if (word.Length > MaxWordLength) word = word[..MaxWordLength];
            if (word.Length < MinWordLength) continue; // same gate as the ranked search
            var ids = new HashSet<int>();
            foreach (var w in getWordVariations(word, expression.Infix, expression.Fuzzy, maxWordsEval)) {
                foreach (var view in expression.Prefix ? prefixViews(w, maxWordsEval) : exactViews(w)) {
                    foreach (var (nodeId, _) in view.Enumerate()) ids.Add(nodeId);
                }
            }
            if (!orSearch && ids.Count == 0) return []; // AND search with a term without hits can never match
            results.Add(ids);
        }
        return orSearch ? SearchSetOperations.Union(results) : SearchSetOperations.Intersection(results);
    }
    public List<RawSearchHit> SearchForRankedHitData(TermSet value, int pageIndex, int pageSize, int maxHitsEvaluated, int maxWordsEvaluated, bool orSearch, out int totalHits) {
        totalHits = 0;
        if (value.Terms.Length == 0) return [];
        var results = new List<Dictionary<int, double>>();
        double totalDocCount = _docs.DocCount;
        var averageDocWordCount = _docs.AverageWordCount;
        foreach (var expression in value.Terms) {
            var word = expression.Word;
            if (word.Length > MaxWordLength) word = word[..MaxWordLength];
            if (word.Length < MinWordLength) continue; // words below min length are never indexed
            var scorePerNodeId = new Dictionary<int, double>();
            var variation = 1;
            foreach (var w in getWordVariations(word, expression.Infix, expression.Fuzzy, maxWordsEvaluated)) {
                foreach (var view in expression.Prefix ? prefixViews(w, maxWordsEvaluated) : exactViews(w)) {
                    double docsWithHit = view.Count;
                    var taken = 0;
                    foreach (var (nodeId, hits) in view.Enumerate()) {
                        if (++taken > maxHitsEvaluated) break;
                        if (!_docs.TryGet(nodeId, out var docWordCount)) continue; // stale hit, doc no longer has a word count
                        var score = Bm25.Score(hits, docsWithHit, docWordCount, averageDocWordCount, totalDocCount) / variation; // variation 1 == exact match
                        if (scorePerNodeId.TryGetValue(nodeId, out var score0)) scorePerNodeId[nodeId] = score0 + score;
                        else scorePerNodeId.Add(nodeId, score);
                    }
                }
                variation++;
            }
            if (!orSearch && scorePerNodeId.Count == 0) return []; // AND search with a term without hits can never match
            results.Add(scorePerNodeId);
        }
        var combined = orSearch ? SearchSetOperations.Union(results) : SearchSetOperations.Intersection(results);
        totalHits = combined.Count;
        IEnumerable<KeyValuePair<int, double>> ordered = combined.OrderByDescending(i => i.Value);
        var skip = pageIndex * pageSize;
        if (skip > 0) ordered = ordered.Skip(skip);
        if (pageSize > 0) ordered = ordered.Take(pageSize);
        List<RawSearchHit> page = [];
        foreach (var r in ordered) page.Add(new() { NodeId = r.Key, Score = (float)(r.Value / 100d) });
        return page;
    }
    public IEnumerable<string> SuggestSpelling(string query, bool boostCommonWords) {
        var word = query.ToLowerInvariant(); // the index only contains lowercased words
        if (word.Length < MinWordLength) return new List<string>();
        return suggestCandidates(word, int.MaxValue)
            .Select(c => (c.term, c.dist, count: getView(c.term).Count))
            .Where(c => c.count > 0) // skip words no longer present in any document
            .OrderBy(c => c.dist / (boostCommonWords ? (double)c.count : 1d))
            .Take(10)
            .Select(c => c.term)
            .ToList();
    }

    // ---- word variations (shared by ranked and unranked search) --------------------------------

    IEnumerable<PostingsView> exactViews(string word) {
        var view = getView(word);
        if (view.Count > 0) yield return view;
    }
    IEnumerable<PostingsView> prefixViews(string prefix, int maxWords) {
        var count = 0;
        foreach (var term in mergedTerms(prefix)) {
            if (!term.StartsWith(prefix, StringComparison.Ordinal)) break;
            var view = getView(term);
            if (view.Count == 0) continue;
            yield return view;
            if (++count >= maxWords) break;
        }
    }
    List<string> getWordVariations(string word, bool infix, bool fuzzy, int maxWordsEval) {
        // first variation is always the word itself, infix and fuzzy expansions are deduped
        var variations = new List<string> { word };
        if (!infix && !fuzzy) return variations;
        var seen = new HashSet<string>(StringComparer.Ordinal) { word };
        if (infix && InfixSearch) {
            foreach (var w in infixVariations(word)) {
                if (variations.Count >= maxWordsEval) return variations;
                if (seen.Add(w)) variations.Add(w);
            }
        }
        if (fuzzy) {
            foreach (var (term, _) in suggestCandidates(word, maxWordsEval)) {
                if (variations.Count >= maxWordsEval) return variations;
                if (seen.Add(term)) variations.Add(term);
            }
        }
        return variations;
    }
    IEnumerable<string> infixVariations(string word) {
        foreach (var term in mergedTerms(string.Empty)) {
            if (term.Contains(word, StringComparison.Ordinal)) yield return term;
        }
    }
    // mirrors the trie's suggest flow: try the narrow default distance first, widen only when it
    // yields too little; exact matches (distance 0) are never suggestions
    List<(string term, int dist)> suggestCandidates(string word, int max) {
        DefaultLevenshtein.GetDefaultSearchDistance(word.Length, out var distance1, out var distance2);
        if (distance1 > 0) {
            var r = scanSimilar(word, distance1, max);
            if (r.Count > 2) return r;
            if (distance1 == distance2) return r;
        }
        if (distance2 == 0) return [];
        return scanSimilar(word, distance2, max);
    }
    List<(string term, int dist)> scanSimilar(string word, int maxDist, int max) {
        var result = new List<(string, int)>();
        foreach (var term in mergedTerms(string.Empty)) {
            if (result.Count >= max) break;
            if (Math.Abs(term.Length - word.Length) > maxDist) continue;
            var d = BoundedLevenshtein.Distance(word, term, maxDist);
            if (d >= 1 && d <= maxDist) result.Add((term, d));
        }
        return result;
    }

    // ---- postings and term access ----------------------------------------------------------------

    PostingsView getView(string word) {
        var overlay = _mem.GetOverlay(word);
        DiskPostings disk;
        var key = new CacheKey(_ownerId, cacheKindPostings, _generation, 0, word);
        if (_cache.TryGet(key, out var cached)) {
            disk = (DiskPostings)cached;
        } else {
            disk = loadDiskPostings(word);
            _cache.Set(key, disk, disk.ByteSize);
        }
        if (overlay == null && disk.Ids.Length == 0) return PostingsView.Empty;
        return new PostingsView(disk, overlay);
    }
    DiskPostings loadDiskPostings(string word) {
        List<SegPostings>? found = null;
        for (var i = _segments.Count - 1; i >= 0; i--) { // newest first
            if (_segments[i].TryGetTerm(word, _cache, _ownerId, out var entry)) {
                (found ??= []).Add(_segments[i].ReadPostings(entry));
            }
        }
        if (found == null) return DiskPostings.Empty;
        if (found.Count == 1 && found[0].DelIds.Length == 0) return new DiskPostings(found[0].AddIds, found[0].AddHits);
        var (ids, hits, _) = resolve(found, keepDels: false);
        return ids.Length == 0 ? DiskPostings.Empty : new DiskPostings(ids, hits);
    }
    /// <summary>Last-write-wins resolution of one word's postings, newest source first. Returns
    /// surviving adds sorted by node id, plus surviving tombstones when they must be kept (a merge
    /// that does not include the oldest segment).</summary>
    static (int[] ids, byte[] hits, int[] dels) resolve(List<SegPostings> newestFirst, bool keepDels) {
        var decided = new Dictionary<int, short>();
        foreach (var p in newestFirst) {
            for (var i = 0; i < p.AddIds.Length; i++) decided.TryAdd(p.AddIds[i], p.AddHits[i]);
            foreach (var id in p.DelIds) decided.TryAdd(id, MemTable.Tombstone);
        }
        var addCount = 0;
        foreach (var v in decided.Values) if (v >= 0) addCount++;
        var ids = new int[addCount];
        var dels = new int[keepDels ? decided.Count - addCount : 0];
        int a = 0, d = 0;
        foreach (var kv in decided) {
            if (kv.Value >= 0) ids[a++] = kv.Key;
            else if (keepDels) dels[d++] = kv.Key;
        }
        Array.Sort(ids);
        if (dels.Length > 0) Array.Sort(dels);
        var hits = new byte[addCount];
        for (var i = 0; i < ids.Length; i++) hits[i] = (byte)decided[ids[i]];
        return (ids, hits, dels);
    }
    /// <summary>Every unique term &gt;= <paramref name="from"/> across the memtable and all
    /// segments, in ordinal order (a k-way merge over the sorted sources).</summary>
    IEnumerable<string> mergedTerms(string from) {
        var sources = new List<IEnumerator<string>>();
        try {
            var memTerms = _mem.SortedTerms;
            var memStart = 0;
            if (from.Length > 0) {
                memStart = Array.BinarySearch(memTerms, from, StringComparer.Ordinal);
                if (memStart < 0) memStart = ~memStart;
            }
            sources.Add(enumerateFrom(memTerms, memStart).GetEnumerator());
            foreach (var s in _segments) sources.Add(s.Scan(from, _cache, _ownerId).Select(e => e.Term).GetEnumerator());
            var active = new List<IEnumerator<string>>();
            foreach (var s in sources) if (s.MoveNext()) active.Add(s);
            while (active.Count > 0) {
                var min = active[0].Current;
                for (var i = 1; i < active.Count; i++) {
                    if (string.CompareOrdinal(active[i].Current, min) < 0) min = active[i].Current;
                }
                yield return min;
                for (var i = active.Count - 1; i >= 0; i--) {
                    if (string.CompareOrdinal(active[i].Current, min) == 0 && !active[i].MoveNext()) active.RemoveAt(i);
                }
            }
        } finally {
            foreach (var s in sources) s.Dispose();
        }
    }
    static IEnumerable<string> enumerateFrom(string[] terms, int start) {
        for (var i = start; i < terms.Length; i++) yield return terms[i];
    }

    // ---- persistence -----------------------------------------------------------------------------

    string manifestPath => Path.Combine(_folder, "manifest.bin");
    string segmentPath(long id) => Path.Combine(_folder, "seg_" + id.ToString("d16") + ".bin");

    /// <summary>
    /// Durably persists the committed write buffer as a new segment and re-points the manifest at
    /// the result, stamped with the index's position (timestamp + WAL file id). Skips the segment
    /// write while the buffer is below the flush threshold and the flush is not forced — the index
    /// then keeps reporting its previous durable position and the WAL replay covers a crash. Never
    /// regresses the persisted timestamp (during replay the engine may checkpoint at a position
    /// this index is already past).
    /// </summary>
    internal void Flush(long timestamp, Guid walFileId, bool force) {
        if (timestamp <= 0) return; // nothing durable to claim yet
        if (timestamp < _persistedTimestamp) return;
        if (_mem.IsEmpty) {
            // advancing the stamp on a clean index is a cheap manifest write and spares a replay
            if (timestamp != _persistedTimestamp || walFileId != _persistedWalFileId) writeManifest(timestamp, walFileId);
            return;
        }
        if (!force && _mem.ApproxBytes < _options.MemTableFlushThresholdBytes) return;
        var id = _nextSegmentId++;
        SegmentWriter.Write(segmentPath(id), id, _options.TermsPerBlock, _mem.SortedTermPostings(), _mem.SortedDocOps());
        _segments.Add(Segment.Open(segmentPath(id), id));
        _mem = new MemTable();
        var replaced = mergeIfNeeded();
        bumpGeneration();
        writeManifest(timestamp, walFileId);
        // only after the manifest swap: a crash in between leaves orphans, which open() deletes
        if (replaced != null) retireSegments(replaced);
    }

    /// <summary>
    /// Retires the segments the manifest no longer references: closes the handles, deletes the
    /// files, and drops the dictionary blocks cached from them. The eviction is the point — block
    /// entries are keyed by segment id and segment ids are never reused, so entries from a merged
    /// away segment can never be read again, and without this they would sit in the shared cache
    /// until the byte budget pushed them out. In a long write-heavy run that is most of the cache:
    /// memory held for data that no longer exists, and that no GC can reclaim.
    /// </summary>
    void retireSegments(List<Segment> retired) {
        _cache.Evict(_ownerId, Segment.CacheKindBlock, retired.Select(s => s.Id).ToArray());
        foreach (var s in retired) {
            s.Dispose();
            try { File.Delete(s.Path); } catch { } // a locked file is an orphan for the next open
        }
    }
    List<Segment>? mergeIfNeeded() {
        List<Segment>? replaced = null;
        // size ladder: merge the newest two while they are within 2x of each other; segment sizes
        // then grow geometrically toward the old end and the count stays logarithmic
        while (_segments.Count >= 2 && _segments[^2].FileLength <= _segments[^1].FileLength * 2) {
            mergeRun(_segments.Count - 2, 2, ref replaced);
        }
        if (_segments.Count >= _options.MergeSegmentThreshold) mergeRun(0, _segments.Count, ref replaced);
        return replaced;
    }
    /// <summary>Merges <paramref name="count"/> adjacent segments into one, which takes the run's
    /// place in the recency order. Tombstones and doc removals only annihilate when the run
    /// includes the oldest segment; otherwise they must survive to cancel older data below the run.</summary>
    void mergeRun(int start, int count, ref List<Segment>? replaced) {
        var run = _segments.GetRange(start, count);
        var includesOldest = start == 0;
        var id = _nextSegmentId++;
        SegmentWriter.Write(segmentPath(id), id, _options.TermsPerBlock, mergeTerms(run, keepDels: !includesOldest), mergeDocOps(run, keepRemoves: !includesOldest));
        var merged = Segment.Open(segmentPath(id), id);
        _segments.RemoveRange(start, count);
        _segments.Insert(start, merged);
        (replaced ??= []).AddRange(run);
    }
    static IEnumerable<(string term, TermPostings postings)> mergeTerms(List<Segment> run, bool keepDels) {
        var sources = new IEnumerator<TermEntry>[run.Count]; // index-aligned with run (oldest first)
        for (var i = 0; i < run.Count; i++) sources[i] = run[i].Scan(string.Empty, null, 0).GetEnumerator();
        try {
            var active = new List<int>();
            for (var i = 0; i < sources.Length; i++) if (sources[i].MoveNext()) active.Add(i);
            var buffer = new List<SegPostings>();
            while (active.Count > 0) {
                var min = sources[active[0]].Current.Term;
                foreach (var i in active) {
                    if (string.CompareOrdinal(sources[i].Current.Term, min) < 0) min = sources[i].Current.Term;
                }
                buffer.Clear();
                for (var i = run.Count - 1; i >= 0; i--) { // newest first for last-write-wins
                    if (active.Contains(i) && string.CompareOrdinal(sources[i].Current.Term, min) == 0) {
                        buffer.Add(run[i].ReadPostings(sources[i].Current));
                    }
                }
                var (ids, hits, dels) = resolve(buffer, keepDels);
                if (ids.Length > 0 || dels.Length > 0) yield return (min, new TermPostings(ids, hits, dels));
                for (var i = active.Count - 1; i >= 0; i--) {
                    var s = active[i];
                    if (string.CompareOrdinal(sources[s].Current.Term, min) == 0 && !sources[s].MoveNext()) active.RemoveAt(i);
                }
            }
        } finally {
            foreach (var s in sources) s.Dispose();
        }
    }
    static List<(int id, int wordCountOrRemove)> mergeDocOps(List<Segment> run, bool keepRemoves) {
        var decided = new Dictionary<int, int>();
        for (var i = run.Count - 1; i >= 0; i--) { // newest first
            foreach (var (id, op) in run[i].ReadDocOps()) decided.TryAdd(id, op);
        }
        var list = new List<(int, int)>(decided.Count);
        foreach (var kv in decided) {
            if (keepRemoves || kv.Value >= 0) list.Add((kv.Key, kv.Value));
        }
        list.Sort((x, y) => x.Item1.CompareTo(y.Item1));
        return list;
    }
    void writeManifest(long timestamp, Guid walFileId) {
        new TextIndexManifest {
            WalFileId = walFileId,
            Timestamp = timestamp,
            NextSegmentId = _nextSegmentId,
            SegmentIds = _segments.Select(s => s.Id).ToArray(),
        }.Write(manifestPath);
        _persistedTimestamp = timestamp;
        _persistedWalFileId = walFileId;
    }
    /// <summary>
    /// The manifest is only trusted when it belongs to the engine's WAL file and every segment it
    /// references opens cleanly; otherwise the index holds data of unknown provenance (foreign log
    /// file, torn files, a legacy layout) and is reset to empty so the replay rebuilds it from
    /// timestamp 0 instead of duplicating or resurrecting documents.
    /// </summary>
    void open() {
        Directory.CreateDirectory(_folder);
        var m = TextIndexManifest.TryRead(manifestPath);
        if (m != null && m.WalFileId != Guid.Empty && m.WalFileId == _engine.WalFileId) {
            try {
                foreach (var id in m.SegmentIds) _segments.Add(Segment.Open(segmentPath(id), id));
                _nextSegmentId = m.NextSegmentId;
                _persistedTimestamp = m.Timestamp;
                _persistedWalFileId = m.WalFileId;
                deleteStrayFiles();
                foreach (var s in _segments) { // oldest first, newer ops overwrite older ones
                    foreach (var (docId, op) in s.ReadDocOps()) {
                        if (op >= 0) _docs.Set(docId, op);
                        else _docs.Remove(docId);
                    }
                }
                return;
            } catch {
                // fall through to the reset below
            }
        }
        resetFiles();
    }
    void resetFiles() {
        _cache.Evict(_ownerId); // the segments these entries came from are about to be deleted
        foreach (var s in _segments) s.Dispose();
        _segments.Clear();
        try { File.Delete(manifestPath); } catch { }
        deleteStrayFiles();
        _mem = new MemTable();
        _docs = new DocLengths();
        _nextSegmentId = 1;
        _persistedTimestamp = 0;
        _persistedWalFileId = Guid.Empty; // the first flush stamps the engine's WAL id
        bumpGeneration();
    }
    void deleteStrayFiles() {
        var live = _segments.Select(s => Path.GetFileName(s.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.GetFiles(_folder, "seg_*.bin")) {
            if (!live.Contains(Path.GetFileName(f))) {
                try { File.Delete(f); } catch { }
            }
        }
        try { File.Delete(manifestPath + ".tmp"); } catch { }
    }
    void bumpGeneration() {
        _generation++;
        _cache.Evict(_ownerId, cacheKindPostings); // merged postings are per segment-list generation
    }
    /// <summary>Wipes the index back to empty (timestamp 0). Used by the engine's reset paths.</summary>
    internal void ResetToEmpty() {
        resetFiles();
        _cache.Evict(_ownerId);
    }
    internal long GetTotalDiskSpace() {
        long total = 0;
        foreach (var s in _segments) total += s.FileLength;
        try { if (File.Exists(manifestPath)) total += new FileInfo(manifestPath).Length; } catch { }
        return total;
    }
    /// <summary>Full merge of all segments (and the write buffer via the forced flush the engine
    /// does first), dropping every tombstone. No-op when there is nothing to merge.</summary>
    internal void OptimizeDisk() {
        if (_segments.Count < 2) return;
        List<Segment>? replaced = null;
        mergeRun(0, _segments.Count, ref replaced);
        bumpGeneration();
        writeManifest(_persistedTimestamp, _persistedWalFileId);
        if (replaced != null) retireSegments(replaced);
    }

    // ---- lifecycle -------------------------------------------------------------------------------

    public void ClearCache() => _cache.Evict(_ownerId);
    public void CompressMemory() { }
    public void Dispose() {
        // an un-flushed memtable is discarded by design: its ops are covered by the WAL and the
        // persisted timestamp still points at the last durable manifest, so the replay rebuilds them
        _cache.Evict(_ownerId); // the cache belongs to the engine and can outlive this index
        foreach (var s in _segments) s.Dispose();
        _segments.Clear();
        _mem = new MemTable();
        _docs = new DocLengths();
    }
}
