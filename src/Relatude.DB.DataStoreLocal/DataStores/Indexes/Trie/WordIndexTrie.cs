using Relatude.DB.Common;
using Relatude.DB.DataStores.Indexes.Trie.CharArraySearch;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Indexes.Trie;

internal class WordIndexTrie : IWordIndex {
    readonly CharArrayTrie _trie;
    long _searchIndexStateId;
    SetRegister _register;
    readonly IIOProvider _io;
    readonly Action<string, Exception>? _logError;
    // true whenever the in-memory state may differ from the persisted body; starts true so an
    // index that was never persisted is never re-stamped as if it were (see WriteNewTimestampDueToRewriteHotswap)
    bool _changedSinceLastSave = true;
    public WordIndexTrie(SetRegister sets, string uniqueKey, string friendlyName, IIOProvider io, int minWordLength, int maxWordLength, bool prefixSearch, bool infixSearch, Action<string, Exception>? logError = null) {
        _trie = new(minWordLength, maxWordLength, prefixSearch, infixSearch);
        _register = sets;
        UniqueKey = uniqueKey;
        _io = io;
        _logError = logError;
        newSetState();
        FriendlyName = friendlyName;
    }
    public string UniqueKey { get; private set; }
    void newSetState() {
        _searchIndexStateId = SetRegister.NewStateId();
    }
    public IdSet SearchForIdSetUnranked(TermSet value, bool orSearch, int maxWordsEval) {
        return _register.SearchForIdSetUnranked(_searchIndexStateId, value, orSearch, () => _trie.SearchIdsUnsorted(value, orSearch, maxWordsEval));
    }
    public void Add(int nodeId, object value) {
        _changedSinceLastSave = true;
#if DEBUG
        _trie.IndexText((string)value, nodeId);
#else
        try {
            _trie.IndexText((string)value, nodeId);
        } catch (Exception err) {
            // swallowed to keep the transaction alive, but the index may now be out of sync until rebuilt, so never silently
            _logError?.Invoke("Word index \"" + FriendlyName + "\" failed indexing node " + nodeId + ". The index may be out of sync until rebuilt. ", err);
        }
#endif
        newSetState();
    }
    public void Remove(int nodeId, object value) {
        _changedSinceLastSave = true;
#if DEBUG
        _trie.DeIndexText((string)value, nodeId);
#else
        try {
            _trie.DeIndexText((string)value, nodeId);
        } catch (Exception err) {
            // swallowed to keep the transaction alive, but the index may now be out of sync until rebuilt, so never silently
            _logError?.Invoke("Word index \"" + FriendlyName + "\" failed deindexing node " + nodeId + ". The index may be out of sync until rebuilt. ", err);
        }
#endif
        newSetState();
    }
    public void RegisterAddDuringStateLoad(int nodeId, object value) => Add(nodeId, value);
    public void RegisterRemoveDuringStateLoad(int nodeId, object value) => Remove(nodeId, value);
    public IEnumerable<string> SuggestSpelling(string query, bool boostCommonWords) => _trie.Suggest(query, boostCommonWords);
    public void WriteNewTimestampDueToRewriteHotswap(long newTimestamp, Guid walFileId) {
        // appending a stamp is only sound when the persisted body equals the in-memory state: the
        // stamp is trusted on the next open, so changes missing from a stale body would be skipped
        // by the log replay and silently lost — and with no persisted body there is nothing to
        // stamp. Persist the full state whenever the body may be behind or is missing:
        if (_changedSinceLastSave || !IndexStateFiles.TryAppendNewTimestamp(_io, UniqueKey, newTimestamp, walFileId)) {
            SaveStateForMemoryIndexes(newTimestamp, walFileId);
            return;
        }
        PersistedTimestamp = newTimestamp;
    }
    public void SaveStateForMemoryIndexes(long logTimestamp, Guid walFileId) {
        IndexStateFiles.Save(_io, UniqueKey, logTimestamp, walFileId, _trie.WriteState);
        PersistedTimestamp = logTimestamp;
        _changedSinceLastSave = false;
    }
    public void ReadStateForMemoryIndexes(Guid walFileId) {
        PersistedTimestamp = 0;
        if (!IndexStateFiles.TryRead(_io, UniqueKey, walFileId, _trie.ReadState, out var persistedTimestamp)) return;
        PersistedTimestamp = persistedTimestamp;
        newSetState();
        _changedSinceLastSave = false; // memory now equals the body just read
    }
    public void CompressMemory() => _trie.CompressMemory();
    public void Dispose() => _trie.Dispose();
    public void ClearCache() => _trie.ClearCache();
    public List<RawSearchHit> SearchForRankedHitData(TermSet value, int pageIndex, int pageSize, int maxHitsEvaluated, int maxWordsEvaluated, bool orSearch, out int totalHits) {
        if (value.Terms.Length == 0) {
            totalHits = 0;
            return [];
        }
        var result = _trie.Search(value, out totalHits, true, pageSize * pageIndex, pageSize, maxHitsEvaluated, maxWordsEvaluated, orSearch);
        List<RawSearchHit> hits = [];
        foreach (var r in result) {
            hits.Add(new() { NodeId = r.Key, Score = (float)(r.Value / 100d) });
        }
        return hits;
    }
    public long PersistedTimestamp { get; private set; }
    public void FlagFirstCommit() { }
    public string FriendlyName { get; }
}