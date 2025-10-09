using Relatude.DB.IO;
using Relatude.DB.Common;
using Relatude.DB.DataStores.Indexes.Trie.CharArraySearch.Trie;
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
        MaxWordLength = maxWordLength;
        MinWordLength = minWordLength;
        PrefixSearch = prefixSearch;
        InfixSearch = infixSearch;
        _trie = new();
        if (InfixSearch) _infixTrie = new(minWordLength);
    }
    void indexWord(char[] word, int nodeId, byte count) {
        if (_trie.TryGet(word, out var hits)) {
            hits.Add(new WordHit(nodeId, count));
            if (InfixSearch && _infixTrie != null) _infixTrie.Add(new string(word), word);
        } else {
            _trie.Add(word, new([new(nodeId, count)]));
        }
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
    public HashSet<int> SearchIdsUnsorted(TermSet expressions, bool orSearch, int maxWordsEval) {
        // Be careful with changes as some logic doubles with Search() method...
        if (expressions.Terms.Length == 0) return [];
        List<HashSet<int>> results = [];
        foreach (var expression in expressions.Terms) {
            var ids = new HashSet<int>();
            var word = expression.Word;
            if (word.Length > MaxWordLength) word = word[..MaxWordLength];
            if (word.Length > 0) {
                var variations = new List<char[]>() { word.ToArray() };
                if (expression.Infix && _infixTrie != null) variations.AddRange(_infixTrie.Retrieve(word));
                if (expression.Fuzzy) variations.AddRange(fuzzyVariations(expression.Word, maxWordsEval));
                foreach (var w in variations) {
                    foreach (var hits in expression.Prefix ? _trie.SearchPrefix(w, maxWordsEval) : _trie.SearchExact(w)) {
                        if (hits == null) throw new NullReferenceException();
                        foreach (var wordHit in hits.Values) {
                            ids.Add(wordHit.NodeId);
                        }
                    }
                }
                results.Add(ids);
            }
        }
        return orSearch ? SearchSetOperations.Union(results) : SearchSetOperations.Intersection(results);
    }
    public IEnumerable<string> Suggest(string query, bool boostCommonWords) {
        var word = query.ToArray();
        if (word.Length < MinWordLength) return new List<string>();
        return _trie.Suggest(word, int.MaxValue)
            .OrderBy(f => f.LevDist / (boostCommonWords ? (f.Value?.Count) : 1d))
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
    bool noHitsPossible(TermSet searches, int maxWordVariations, bool orSearch) {
        int hitsPerWord = 0;
        foreach (var expression in searches.Terms) {
            var word = expression.Word;
            if (word.Length > MaxWordLength) word = word.Substring(0, MaxWordLength);
            if (word.Length >= MinWordLength) {
                var variations = new List<char[]>() { word.ToArray() };
                if (expression.Infix && _infixTrie != null) variations.AddRange(_infixTrie.Retrieve(word));
                if (expression.Fuzzy) variations.AddRange(fuzzyVariations(expression.Word, maxWordVariations));
                foreach (var w in variations) {
                    foreach (var hits in expression.Prefix ? _trie.SearchPrefix(w, maxWordVariations) : _trie.SearchExact(w)) {
                        if (hits == null) throw new NullReferenceException();
                        hitsPerWord += hits.Count;
                    }
                }
                if (hitsPerWord == 0 && !orSearch) return true;
            }
        }
        return hitsPerWord == 0;
    }
    char[][] fuzzyVariations(string word, int max) {
        var result = _trie.Suggest(word.ToCharArray(), max).Select(f => f.Word).ToArray();
        //Console.WriteLine($"Fuzzy variations for '{word}': {string.Join(", ", result.Select(r => new string(r)))}");
        return result;
    }
    public IEnumerable<KeyValuePair<int, double>> Search(TermSet searches, out int totalHits, bool sorted, int skip, int take, int maxHitsEval, int maxWordsEval, bool orSearch) { // Be careful with changes as some logic doubles with Search() method...
        totalHits = 0;
        if (searches.Terms.Length == 0) return [];
        if (searches.Terms.Length > 1 && noHitsPossible(searches, maxWordsEval, orSearch)) return []; // just abort
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
                var variations = new List<char[]>() { word.ToArray() };
                if (expression.Infix && _infixTrie != null) variations.AddRange(_infixTrie!.Retrieve(word));
                if (expression.Fuzzy) variations.AddRange(fuzzyVariations(expression.Word, maxWordsEval));
                var variation = 1;
                foreach (var w in variations) {
                    var matches = expression.Prefix ? _trie.SearchPrefix(w, maxWordsEval) : _trie.SearchExact(w);
                    foreach (var hits in matches) {
                        if (hits == null) throw new NullReferenceException();
                        double docsWithHit = hits.Count;
                        foreach (var hit in hits.Values.Take(maxHitsEval)) {
                            if (sorted) {
                                var docWordCount = _docWordCounts.Get(hit.NodeId);
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
            stream.WriteIntArray(value.Values.Select(h => h.NodeId).ToArray());
            stream.WriteByteArray(value.Values.Select(h => h.Hits).ToArray());
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
