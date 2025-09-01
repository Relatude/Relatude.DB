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
    public void RegisterAddDuringStateLoad(int id, object value, long timestampId) => _o.RegisterAddDuringStateLoad(id, value, timestampId);
    public void RegisterRemoveDuringStateLoad(int id, object value, long timestampId) => _o.RegisterRemoveDuringStateLoad(id, value, timestampId);

    public IdSet SearchForIdSetUnranked(TermSet search, bool orSearch) { _o.Dequeue(); return _i.SearchForIdSetUnranked(search, orSearch); }
    public List<RawSearchHit> SearchForRankedHitData(TermSet value, int pageIndex, int pageSize, int maxHitsEvaluated, int maxWordsEvaluated, bool orSearch, out int totalHits) {
        _o.Dequeue();
        return _i.SearchForRankedHitData(value, pageIndex, pageSize, maxHitsEvaluated, maxWordsEvaluated, orSearch, out totalHits);
    }
    public IEnumerable<string> SuggestSpelling(string query, bool boostCommonWords) { _o.Dequeue(); return _i.SuggestSpelling(query, boostCommonWords); }
    public void ReadState(IReadStream stream) { _o.Dequeue(); _i.ReadState(stream); }
    public void SaveState(IAppendStream stream) { _o.Dequeue(); _i.SaveState(stream); }
    //public int MaxCount(string value, bool orSearch) { _o.Dequeue(); return _i.MaxCount(value, orSearch); }
    public void ClearCache() { _o.Dequeue(); _i.ClearCache(); }
    public void CompressMemory() { _o.Dequeue(); _i.CompressMemory(); }
    public void Dispose() { _o.Dequeue(); _i.Dispose(); }
}