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
    readonly FileKeyUtility _fileKeys;
    public WordIndexTrie(SetRegister sets, string uniqueKey, string friendlyName, IIOProvider io, FileKeyUtility fileKey, int minWordLength, int maxWordLength, bool prefixSearch, bool infixSearch) {
        _trie = new(minWordLength, maxWordLength, prefixSearch, infixSearch);
        _register = sets;
        UniqueKey = uniqueKey;
        _io = io;
        _fileKeys = fileKey;
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
    public void RegisterAddDuringStateLoad(int nodeId, object value) => Add(nodeId, value);
    public void RegisterRemoveDuringStateLoad(int nodeId, object value) => Remove(nodeId, value);
    public IEnumerable<string> SuggestSpelling(string query, bool boostCommonWords) => _trie.Suggest(query, boostCommonWords);
    public void WriteNewTimestampDueToRewriteHotswap(long newTimestamp, Guid walFileId) {
        var fileName = _fileKeys.Index_GetFileKey(UniqueKey);
        using var stream = _io.OpenAppend(fileName);
        stream.WriteVerifiedLong(newTimestamp);
        stream.WriteGuid(walFileId);
        PersistedTimestamp = newTimestamp;
    }
    public void SaveStateForMemoryIndexes(long logTimestamp, Guid walFileId) {
        var fileName = _fileKeys.Index_GetFileKey(UniqueKey);
        _io.DeleteIfItExists(fileName); // could be optimized to keep old file
        using var stream = _io.OpenAppend(fileName);
        _trie.WriteState(stream);
        stream.WriteVerifiedLong(logTimestamp);
        stream.WriteGuid(walFileId);
        PersistedTimestamp = logTimestamp;
    }
    public void ReadStateForMemoryIndexes(Guid walFileId) {
        PersistedTimestamp = 0;
        var fileName = _fileKeys.Index_GetFileKey(UniqueKey);
        if (_io.DoesNotExistsOrIsEmpty(fileName)) return;
        using var stream = _io.OpenRead(fileName, 0);
        _trie.ReadState(stream);
        Guid walId = Guid.Empty;
        while (stream.More()) {
            PersistedTimestamp = stream.ReadVerifiedLong();
            walId = stream.ReadGuid();
        }
        if (walId != walFileId) throw new Exception("WAL file ID mismatch when reading index state. ");
        newSetState();
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
    public long PersistedTimestamp { get; set; }
    public string FriendlyName { get; }
}