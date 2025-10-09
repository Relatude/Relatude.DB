using Relatude.DB.Common;
using Relatude.DB.DataStores.Indexes.Trie.CharArraySearch;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Indexes.Trie;
internal class WordIndexTrie : IWordIndex {
    readonly CharArrayTrie _trie;
    long _searchIndexStateId;
    SetRegister _register;
    public WordIndexTrie(SetRegister sets, string uniqueKey, int minWordLength, int maxWordLength, bool prefixSearch, bool infixSearch) {
        _trie = new(minWordLength, maxWordLength, prefixSearch, infixSearch);
        _register = sets;
        UniqueKey = uniqueKey;
        newSetState();
    }
    public string UniqueKey { get; private set; }
    void newSetState() {
        _searchIndexStateId = SetRegister.NewStateId();
    }
    public IdSet SearchForIdSetUnranked(TermSet value, bool orSearch, int maxWordsEval) {
        return _register.SearchForIdSetUnranked(_searchIndexStateId, value, orSearch, () => _trie.SearchIdsUnsorted(value, orSearch, maxWordsEval));
    }
    public void Add(int nodeId, object value) {
#if DEBUG
        _trie.IndexText((string)value, nodeId);
#else
        try {
            _trie.IndexText((string)value, nodeId);
        } catch { }
#endif
        newSetState();
    }
    public void Remove(int nodeId, object value) {
#if DEBUG
        _trie.DeIndexText((string)value, nodeId);
#else
        try {
            _trie.DeIndexText((string)value, nodeId);
        } catch { }
#endif
        newSetState();
    }
    public void RegisterAddDuringStateLoad(int nodeId, object value, long timestampId) => Add(nodeId, value);
    public void RegisterRemoveDuringStateLoad(int nodeId, object value, long timestampId) => Remove(nodeId, value);

    public IEnumerable<string> SuggestSpelling(string query, bool boostCommonWords) => _trie.Suggest(query, boostCommonWords);
    //public int MaxCount(string value, bool orSearch) => _trie.SearchCount(TermSet.Parse(value, _trie.MinWordLength, _trie.MaxWordLength), orSearch);
    public void ReadState(IReadStream stream) {
        _trie.ReadState(stream);
        newSetState();
    }
    public void SaveState(IAppendStream stream) => _trie.WriteState(stream);
    public void CompressMemory() => _trie.CompressMemory();
    public void Dispose() => _trie.Dispose();
    public void ClearCache() => _trie.ClearCache();
    public List<RawSearchHit> SearchForRankedHitData(TermSet value, int pageIndex, int pageSize, int maxHitsEvaluated, int maxWordsEvaluated, bool orSearch, out int totalHits) {
        if (value.Terms.Length == 0) {
            totalHits = 0;
            return [];
        }
        var result = _trie.Search(value, out totalHits, true, pageSize * pageIndex, pageSize, maxHitsEvaluated, maxWordsEvaluated,  orSearch);
        List<RawSearchHit> hits = [];
        foreach (var r in result) {
            hits.Add(new() { NodeId = r.Key, Semantic = false, Score = (float)(r.Value / 100d) });
        }
        return hits;
    }
}