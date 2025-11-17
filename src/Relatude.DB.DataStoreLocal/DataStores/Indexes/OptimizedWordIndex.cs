using Relatude.DB.Common;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Indexes;

public class OptimizedWordIndex(IWordIndex index) : IWordIndex {
    readonly IWordIndex _i = index;
    readonly AddRemoveOptimization _o = new(index);

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
    public void ReadStateForMemoryIndexes() { _o.Dequeue(); _i.ReadStateForMemoryIndexes(); }
    public void SaveStateForMemoryIndexes(long logTimestamp) { _o.Dequeue(); _i.SaveStateForMemoryIndexes(logTimestamp); }
    //public int MaxCount(string value, bool orSearch) { _o.Dequeue(); return _i.MaxCount(value, orSearch); }
    public void ClearCache() { _o.Dequeue(); _i.ClearCache(); }
    public void CompressMemory() { _o.Dequeue(); _i.CompressMemory(); }
    public long PersistedTimestamp { get { _o.Dequeue(); return _i.PersistedTimestamp; } set { _o.Dequeue(); _i.PersistedTimestamp = value; } }
    public void Dispose() { _o.Dequeue(); _i.Dispose(); }
}