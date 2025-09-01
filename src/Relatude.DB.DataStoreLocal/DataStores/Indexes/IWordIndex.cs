using Relatude.DB.Common;
using Relatude.DB.DataStores.Sets;
namespace Relatude.DB.DataStores.Indexes;
public interface IWordIndex : IIndex {
    //int MaxCount(string value, bool orSearch);
    IdSet SearchForIdSetUnranked(TermSet search, bool orSearch);
    IEnumerable<string> SuggestSpelling(string query, bool boostCommonWords);
    List<RawSearchHit> SearchForRankedHitData(TermSet search, int pageIndex, int pageSize, int maxHitsEvaluated,int maxWordsEvaluated, bool orSearch, out int totalHits);
}
