using Relatude.DB.DataStores.Definitions.PropertyTypes;
namespace Relatude.DB.Query.Data;

internal partial class NodeCollectionData : IStoreNodeDataCollection, IFacetSource, ISearchCollection {
    /// <summary>
    /// The search settings a caller left open, filled in from the store settings - or with the search
    /// turned fully lexical when no AI engine is configured. Shared by the two collection level
    /// searches and by the per property MatchesSearch filter, so all three default alike.
    /// Order of precedence: the value given by the caller, then the AI provider setting if it was
    /// explicitly configured, then SettingsLocal.DefaultSemanticIndexWeight / DefaultSemanticSimilarityLimit.
    /// </summary>
    internal static (double RatioSemantic, float MinimumVectorSimilarity, bool OrSearch, int MaxWordsEvaluated) ResolveSearchSettings(
        DataStores.DataStoreLocal db, double? ratioSemantic, float? minimumVectorSimilarity, bool? orSearch, int? maxWordsEvaluated) => (
            ratioSemantic ?? (db._ai == null ? 0 : defaultSemanticWeight(db)),
            minimumVectorSimilarity ?? (db._ai == null ? 0 : defaultSimilarityLimit(db)),
            orSearch ?? false,
            maxWordsEvaluated ?? int.MaxValue);

    /// <summary>
    /// The weight is a 0-1 blend between the word index and the semantic index. It comes from a config
    /// file, so it is clamped here rather than trusted. The similarity limit is a cosine similarity and
    /// is left alone, -1 is a legal value meaning "no limit".
    /// </summary>
    static double defaultSemanticWeight(DataStores.DataStoreLocal db) {
        var weight = db._ai!.Settings.DefaultSemanticRatio ?? db.Settings.DefaultSemanticIndexWeight;
        return weight < 0 ? 0 : (weight > 1 ? 1 : weight);
    }
    static float defaultSimilarityLimit(DataStores.DataStoreLocal db)
        => (float)(db._ai!.Settings.DefaultMinimumSimilarity ?? db.Settings.DefaultSemanticSimilarityLimit);

    public ISearchQueryResultData Search(string search, Guid searchPropertyId, double? ratioSemantic, float? minimumVectorSimilarity, bool? orSearch, int pageIndex, int pageSize, int? maxHitsEvaluated, int? maxWordsEvaluated) {
        var property = _def.Properties[searchPropertyId];
        if (property is not StringProperty p) throw new Exception("Search property must be a string property");
        var settings = ResolveSearchSettings(_db, ratioSemantic, minimumVectorSimilarity, orSearch, maxWordsEvaluated);
        if (!maxHitsEvaluated.HasValue) maxHitsEvaluated = int.MaxValue;
        if (maxHitsEvaluated < int.MaxValue) maxHitsEvaluated++; // we want to know if there are more hits than requested, so we need to evaluate one more
        var hits = p.SearchForRankedHitData(_ids, search, settings.RatioSemantic, settings.MinimumVectorSimilarity, settings.OrSearch, pageIndex, pageSize, maxHitsEvaluated.Value, settings.MaxWordsEvaluated, _db, _ctx
            , out var totalHits, out var innerSearchTimeMs);
        var capped = false;
        if (maxHitsEvaluated < int.MaxValue && totalHits >= maxHitsEvaluated) { // if we have more hits than requested, we know the result is capped
            totalHits = maxHitsEvaluated.Value - 1; // adjust total hits to the maximum hits evaluated
            capped = true; // we have more hits than requested
        }
        return new SearchQueryResultData(_db, _metrics, _includeBranches, p, search, hits, pageIndex, pageSize, totalHits, capped, innerSearchTimeMs, _ctx);
    }
    public IStoreNodeDataCollection FilterBySearch(string search, Guid searchPropertyId, double? ratioSemantic, float? minimumVectorSimilarity, bool? orSearch, int? maxWordVariations) {
        var property = _def.Properties[searchPropertyId];
        if (property is not StringProperty p) throw new Exception("Search property must be a string property");
        var settings = ResolveSearchSettings(_db, ratioSemantic, minimumVectorSimilarity, orSearch, maxWordVariations);
        var searchIds = p.SearchForIdSet(search, settings.RatioSemantic, settings.MinimumVectorSimilarity, settings.OrSearch, settings.MaxWordsEvaluated, _db, _ctx);
        var newSet = _def.Sets.Intersection(searchIds, _ids);
        return new NodeCollectionData(_db, _ctx, _metrics, newSet, _nodeType, _includeBranches);
    }
}