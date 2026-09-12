using Relatude.DB.Common;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
using Relatude.DB.Query.Data;

namespace Relatude.DB.DataStores.Indexes;

public class OptimizedWordIndex(IWordIndex index) : IWordIndex, IWordCountIndex {
    readonly IWordIndex _i = index;
    readonly AddRemoveOptimization _o = new(index);

    // exposed so the persisted index store can flush the queued remove into its backend before a
    // commit and on rollback, discarding only if the flush fails (see IndexEngineBase)
    internal AddRemoveOptimization Queue => _o;

    public string UniqueKey => _i.UniqueKey;

    public void Add(int id, object value) => _o.Add(id, value);
    public void Remove(int id, object value) => _o.Remove(id, value);
    public void RegisterAddDuringStateLoad(int id, object value) => _o.RegisterAddDuringStateLoad(id, value);
    public void RegisterRemoveDuringStateLoad(int id, object value) => _o.RegisterRemoveDuringStateLoad(id, value);

    public IdSet SearchForIdSetUnranked(TermSet search, bool orSearch, int maxWordsEval) { _o.Dequeue(); return _i.SearchForIdSetUnranked(search, orSearch, maxWordsEval); }
    public List<RawSearchHit> SearchForRankedHitData(TermSet value, int pageIndex, int pageSize, int maxHitsEvaluated, int maxWordsEvaluated, bool orSearch, out int totalHits) {
        _o.Dequeue();
        return _i.SearchForRankedHitData(value, pageIndex, pageSize, maxHitsEvaluated, maxWordsEvaluated, orSearch, out totalHits);
    }
    public IEnumerable<string> SuggestSpelling(string query, bool boostCommonWords) { _o.Dequeue(); return _i.SuggestSpelling(query, boostCommonWords); }

    // Counting words is optional, and this wrapper sits in front of every index whether its engine
    // can do it or not - so the wrapper always implements the interface and forwards the question.
    public bool CanCountWords => _i is IWordCountIndex { CanCountWords: true };
    public WordCountSet CountWords(IdSet subset, WordCountOptions options) {
        if (_i is not IWordCountIndex counter || !counter.CanCountWords)
            throw new NotSupportedException("The word index \"" + FriendlyName + "\" cannot count the words of a set of nodes. ");
        _o.Dequeue(); // a queued add or remove is part of the index as far as any reader is concerned
        return counter.CountWords(subset, options);
    }
    public void WriteNewTimestampDueToRewriteHotswap(long newTimestamp, Guid walFileId) { _o.Dequeue(); _i.WriteNewTimestampDueToRewriteHotswap(newTimestamp, walFileId); }
    public void ReadStateForMemoryIndexes(Guid walFileId) { _o.Dequeue(); _i.ReadStateForMemoryIndexes(walFileId); }
    public void SaveStateForMemoryIndexes(long logTimestamp, Guid walFileId) { _o.Dequeue(); _i.SaveStateForMemoryIndexes(logTimestamp, walFileId); }
    //public int MaxCount(string value, bool orSearch) { _o.Dequeue(); return _i.MaxCount(value, orSearch); }
    public void ClearCache() { _o.Dequeue(); _i.ClearCache(); }
    public void CompressMemory() { _o.Dequeue(); _i.CompressMemory(); }
    public long PersistedTimestamp { get { return _i.PersistedTimestamp; } }
    public void FlagFirstCommit() { _i.FlagFirstCommit(); }
    public string FriendlyName => _i.FriendlyName;
    public void Dispose() { _o.Dequeue(); _i.Dispose(); }
}