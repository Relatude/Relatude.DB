using Relatude.DB.Common;
using Relatude.DB.DataStores.Definitions.PropertyTypes;
namespace Relatude.DB.Query.Data;
internal partial class NodeCollectionData : IStoreNodeDataCollection, IFacetSource, ISearchCollection {
    public ISearchQueryResultData Search(string search, Guid searchPropertyId, double? ratioSemantic, int pageIndex, int pageSize, int maxHitsEvaluated, int maxWordsEvaluated) {
        var property = _def.Properties[searchPropertyId];
        if (property is not StringProperty p) throw new Exception("Search property must be a string property");
        if (ratioSemantic == null) ratioSemantic = _db._ai == null ? 0 : _db._ai.Settings.DefaultSemanticRatio;
        if (maxHitsEvaluated < int.MaxValue) maxHitsEvaluated++; // we want to know if there are more hits than requested, so we need to evaluate one more
        var hits = p.SearchForRankedHitData(_ids, search, ratioSemantic.Value, false, pageIndex, pageSize, maxHitsEvaluated, maxWordsEvaluated, _db, out var totalHits);
        var capped = false;
        if (maxHitsEvaluated < int.MaxValue && totalHits >= maxHitsEvaluated) { // if we have more hits than requested, we know the result is capped
            totalHits = maxHitsEvaluated - 1; // adjust total hits to the maximum hits evaluated
            capped = true; // we have more hits than requested
        }
        return new SearchQueryResultData(_db, _metrics, _includeBranches, p, search, hits, pageIndex, pageSize, totalHits, capped);
    }
    public IStoreNodeDataCollection FilterBySearch(string search, Guid searchPropertyId, double? ratioSemantic) {
        var property = _def.Properties[searchPropertyId];
        if (property is not StringProperty p) throw new Exception("Search property must be a string property");
        if (ratioSemantic == null) ratioSemantic = _db._ai == null ? 0 : _db._ai.Settings.DefaultSemanticRatio;
        var searchIds = p.SearchForIdSet(search, ratioSemantic.Value, false, _db);
        var newSet = _def.Sets.Intersection(searchIds, _ids);
        return new NodeCollectionData(_db, _metrics, newSet, _nodeType, _includeBranches);
    }
}