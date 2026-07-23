using Relatude.DB.IO;
using Relatude.DB.Common;
using Relatude.DB.DataStores.Indexes.Trie.CharArraySearch.Trie;
using Relatude.DB.DataStores.Sets;
using System.Linq.Expressions;
namespace Relatude.DB.DataStores.Indexes.Trie.CharArraySearch;
// not threadsafe
public class CharArrayTrie : IDisposable {
    public bool PrefixSearch { get; }
    public bool InfixSearch { get; }
    public int MaxWordLength { get; }
    public int MinWordLength { get; }
    DocWordCounts _docWordCounts = new();
    CharArrayTrie<HitCounts> _trie;
    InFixVariations? _infixTrie;

    public CharArrayTrie(int minWordLength, int maxWordLength, bool prefixSearch, bool infixSearch) {
        MinWordLength = Math.Max(1, minWordLength); // 0 would allow empty words, crashing the tokenizer and trie
        MaxWordLength = Math.Max(MinWordLength, maxWordLength);
        PrefixSearch = prefixSearch;
        InfixSearch = infixSearch;
        _trie = new();
        if (InfixSearch) _infixTrie = new(MinWordLength);
    }
    void indexWord(char[] word, int nodeId, byte count) {
        if (_trie.TryGet(word, out var hits)) {
            hits.Add(new WordHit(nodeId, count));
        } else {
            _trie.Add(word, new([new(nodeId, count)]));
        }
        // must also run for the first occurrence of a word, the infix trie dedupes repeats internally
        if (InfixSearch && _infixTrie != null) _infixTrie.Add(new string(word), word);
    }
    public void IndexText(string text, int nodeId) {
        if (string.IsNullOrEmpty(text)) return;
        var entries = IndexUtil.Clean(text, MinWordLength, MaxWordLength, out var wordCount);
        _docWordCounts.Add(nodeId, wordCount);
        foreach (var kv in entries) indexWord(kv.Key, nodeId, kv.Value);
    }
    void deIndexWord(char[] word, int nodeId, byte wordHits) {
        if (_trie.TryGet(word, out var hits)) {
            hits.RemoveIfPresent(new(nodeId, wordHits)); // remove the nodeId from the hits, but leaves empty nodes in the trie to improve performance
            //if (hits.Count == 1) {
            //    _trie.Remove(word);
            //} else {
            //    hits.RemoveIfPresent(new(nodeId, wordHits));
            //}
        } else {
            throw new Exception("Deindexed content does not match indexed. ");
        }
    }
    public void DeIndexText(string text, int nodeId) {
        if (string.IsNullOrEmpty(text)) return;
        var entries = IndexUtil.Clean(text, MinWordLength, MaxWordLength, out var wordCount);
        _docWordCounts.Remove(nodeId, wordCount);
        foreach (var kv in entries) deIndexWord(kv.Key, nodeId, kv.Value);
    }
    public bool Contains(string cleanedWord) {
        return _trie.Contains(cleanedWord.ToCharArray());
    }
    public int GetHitCount(string cleanedWord) {
        if (_trie.TryGet(cleanedWord.ToCharArray(), out var hits)) {
            return hits.Count;
        } else {
            return 0;
        }
    }
    public int SearchCount(TermSet query, bool orSearch, int maxWordsEval) {
        return SearchIdsUnsorted(query, orSearch,maxWordsEval).Count; // TODO: optimize this to not return all ids, also if only one search term not need for hashset...
    }
    public ICollection<int> SearchIdsUnsorted(TermSet expressions, bool orSearch, int maxWordsEval) {
        // Be careful with changes as some logic doubles with Search() method...
        if (expressions.Terms.Length == 0) return [];
        // fast path: a single plain term needs no set combining and matches at most one word,
        // whose hit list already holds distinct node ids - collect them straight into the best
        // set representation (bit set when large). The general path below pays a hash insert
        // per id, which dominated the cold cost of one-word searches with many hits.
        if (expressions.Terms.Length == 1 && expressions.Terms[0] is { Prefix: false, Infix: false, Fuzzy: false }) {
            var singleWord = expressions.Terms[0].Word;
            if (singleWord.Length > MaxWordLength) singleWord = singleWord[..MaxWordLength];
            if (singleWord.Length < MinWordLength) return []; // same gate as below: words this short are never indexed
            foreach (var hits in _trie.SearchExact(singleWord.ToCharArray())) {
                if (hits == null) throw new NullReferenceException();
                return IdSet.CollectUnique(nodeIds(hits));
            }
            return [];
            static IEnumerable<int> nodeIds(HitCounts hits) {
                foreach (var wordHit in hits.Values) yield return wordHit.NodeId;
            }
        }
        List<HashSet<int>> results = [];
        foreach (var expression in expressions.Terms) {
            var ids = new HashSet<int>();
            var word = expression.Word;
            if (word.Length > MaxWordLength) word = word[..MaxWordLength];
            if (word.Length >= MinWordLength) { // same gate as Search(), words below min length are never indexed
                foreach (var w in getWordVariations(word, expression.Infix, expression.Fuzzy, maxWordsEval)) {
                    foreach (var hits in expression.Prefix ? _trie.SearchPrefix(w, maxWordsEval) : _trie.SearchExact(w)) {
                        if (hits == null) throw new NullReferenceException();
                        foreach (var wordHit in hits.Values) {
                            ids.Add(wordHit.NodeId);
                        }
                    }
                }
                if (!orSearch && ids.Count == 0) return []; // AND search with a term without hits can never match
                results.Add(ids);
            }
        }
        return orSearch ? SearchSetOperations.Union(results) : SearchSetOperations.Intersection(results);
    }
    public IEnumerable<string> Suggest(string query, bool boostCommonWords) {
        var word = query.ToLowerInvariant().ToCharArray(); // index only contains lowercased words
        if (word.Length < MinWordLength) return new List<string>();
        return _trie.Suggest(word, int.MaxValue)
            .Where(f => f.Value != null && f.Value.Count > 0) // skip words no longer present in any document
            .OrderBy(f => f.LevDist / (boostCommonWords ? (double)f.Value!.Count : 1d))
            .Take(10)
            .Select(f => new string(f.Word));
    }
    //IEnumerable<Term> expandTermsWithFuzzyMathes(Term[] terms) {
    //    var result = new List<Term>();
    //    foreach (var term in terms) {
    //        result.Add(term);
    //        if (term.Fuzzy) {
    //            var hits = _trie.Suggest(term.Word.ToCharArray());
    //            foreach (var hit in hits) {
    //                var similarTerm = new Term(new string(hit.Word), false, false, false);
    //                result.Add(similarTerm);
    //                Console.WriteLine($"Fuzzy match for '{term.Word}' found: '{similarTerm.Word}' with Levenshtein distance {hit.LevDist}");
    //            }
    //        }
    //    }
    //    return result;
    //}
    List<char[]> getWordVariations(string word, bool infix, bool fuzzy, int maxWordsEval) {
        // first variation is always the word itself, infix and fuzzy expansions are deduped
        // (the infix trie returns the word itself too, without dedup exact matches would be scored twice)
        var variations = new List<char[]>() { word.ToCharArray() };
        if (!infix && !fuzzy) return variations;
        var seen = new HashSet<string>() { word };
        if (infix && _infixTrie != null) {
            foreach (var w in _infixTrie.Retrieve(word)) {
                if (variations.Count >= maxWordsEval) return variations;
                if (seen.Add(new string(w))) variations.Add(w);
            }
        }
        if (fuzzy) {
            foreach (var w in fuzzyVariations(word, maxWordsEval)) {
                if (variations.Count >= maxWordsEval) return variations;
                if (seen.Add(new string(w))) variations.Add(w);
            }
        }
        return variations;
    }
    char[][] fuzzyVariations(string word, int max) {
        var result = _trie.Suggest(word.ToCharArray(), max).Select(f => f.Word).ToArray();
        //Console.WriteLine($"Fuzzy variations for '{word}': {string.Join(", ", result.Select(r => new string(r)))}");
        return result;
    }
    public IEnumerable<KeyValuePair<int, double>> Search(TermSet searches, out int totalHits, bool sorted, int skip, int take, int maxHitsEval, int maxWordsEval, bool orSearch) { // Be careful with changes as some logic doubles with SearchIdsUnsorted() method...
        totalHits = 0;
        if (searches.Terms.Length == 0) return [];
        var results = new List<Dictionary<int, double>>();
        var totalDocDount = _docWordCounts.DocCount;
        var averageDocWordCount = _docWordCounts.AverageWordCount;
        int alternativeTotalCount = -1;
        double score = 0;
        foreach (var expression in searches.Terms) {
            var word = expression.Word;
            if (word.Length > MaxWordLength) word = word.Substring(0, MaxWordLength);
            var scorePerNodeId = new Dictionary<int, double>();
            if (word.Length >= MinWordLength) {
                var variation = 1;
                foreach (var w in getWordVariations(word, expression.Infix, expression.Fuzzy, maxWordsEval)) {
                    var matches = expression.Prefix ? _trie.SearchPrefix(w, maxWordsEval) : _trie.SearchExact(w);
                    foreach (var hits in matches) {
                        if (hits == null) throw new NullReferenceException();
                        double docsWithHit = hits.Count;
                        foreach (var hit in hits.Values.Take(maxHitsEval)) {
                            if (sorted) {
                                if (!_docWordCounts.TryGet(hit.NodeId, out var docWordCount)) continue; // stale hit, doc no longer has a word count (partially deindexed), skip it instead of failing the search
                                var bm25 = BM25.Score(hit.Hits, docsWithHit, docWordCount, averageDocWordCount, totalDocDount);
                                score = bm25 / variation; // isFirstVariation == exact match
                            }
                            if (scorePerNodeId.TryGetValue(hit.NodeId, out var score0)) {
                                scorePerNodeId[hit.NodeId] = score0 + score;
                            } else {
                                scorePerNodeId.Add(hit.NodeId, score);
                            }
                        }
                    }
                    variation++;
                }
                if (!orSearch && scorePerNodeId.Count == 0) return []; // AND search with a term without hits can never match
                results.Add(scorePerNodeId);
            }
        }
        IEnumerable<KeyValuePair<int, double>> result = orSearch ? SearchSetOperations.Union(results) : SearchSetOperations.Intersection(results);
        totalHits = alternativeTotalCount > -1 ? alternativeTotalCount : result.Count();
        if (sorted) result = result.OrderByDescending(i => i.Value);
        if (skip > 0) result = result.Skip(skip);
        if (take > 0) result = result.Take(take);
        return result;
    }
    public int GetTotalWordCount() => 0;
    public int GetTotalTextLength() => 0;
    public int GetUniqueWordCount() => _trie.CountWords();
    public void ReadState(IReadStream stream) {

        // word counts
        _docWordCounts = new DocWordCounts();
        _docWordCounts.ReadState(stream);

        // read word trie
        _trie = new();
        _trie.Read(stream);
        if (InfixSearch && _infixTrie != null) _trie.ForEachWord(word => _infixTrie.Add(word, word.ToCharArray()));

        // read values
        var wordCount = stream.ReadVerifiedInt();
        for (int n = 0; n < wordCount; n++) {
            var word = stream.ReadString();
            var cKey = word.ToCharArray();
            if (_trie.TryGetValueNodeRef(cKey, out var nodeRef)) {
                if (nodeRef.Value == null) {
                    var nodeIds = stream.ReadIntArray();
                    var hits = stream.ReadByteArray();
                    var wordHits = new WordHit[nodeIds.Length];
                    for (int m = 0; m < hits.Length; m++) wordHits[m] = new WordHit(nodeIds[m], hits[m]);
                    nodeRef.Value = new(wordHits);
                } else {
                    throw new InvalidDataException(); // all word nodes should be empty
                }
            } else {
                throw new InvalidDataException(); // all words should match
            }
        }
    }
    public void WriteState(IAppendStream stream) {
        
        // word counts
        _docWordCounts.WriteState(stream);

        // word trie
        _trie.Write(stream);

        // values
        stream.WriteVerifiedInt(_trie.CountWords());
        _trie.ForEachWordAndValue((word, value) => {
            stream.WriteString(word);
            if (value == null) throw new NullReferenceException();
            var hits = value.Values.ToArray(); // single enumeration
            var nodeIds = new int[hits.Length];
            var counts = new byte[hits.Length];
            for (int i = 0; i < hits.Length; i++) {
                nodeIds[i] = hits[i].NodeId;
                counts[i] = hits[i].Hits;
            }
            stream.WriteIntArray(nodeIds);
            stream.WriteByteArray(counts);
        });

    }
    public void CompressMemory() {
        _trie.ForEachValue(v => v?.Compress());
        // logic to remove empty nodes added later....
    }
    public void Dispose() {
    }
    public void ClearCache() {

    }
}
