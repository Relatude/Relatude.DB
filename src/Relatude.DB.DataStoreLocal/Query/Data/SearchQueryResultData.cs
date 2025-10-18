using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Definitions.PropertyTypes;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
namespace Relatude.DB.Query.Data;
internal class SearchQueryResultData : ISearchQueryResultData {
    DataStoreLocal _db;
    StringProperty _searchProperty;
    IEnumerable<RawSearchHit> _rawHits;
    List<SearchResultHitData>? _hits;
    Metrics _metrics;
    public SearchQueryResultData(DataStoreLocal db,Metrics metrics, List<IncludeBranch>? includeBranches, StringProperty searchProperty, string search, IEnumerable<RawSearchHit> hits, int pageUsed, int? pageSizeUsed, int totalHits, bool capped) {
        Search = search;
        _metrics = metrics;
        _rawHits = hits;
        PageIndexUsed = pageUsed;
        PageSizeUsed = pageSizeUsed;
        TotalCount = totalHits;
        Capped = capped;
        _db = db;
        _searchProperty = searchProperty;
        _includeBranches = includeBranches;
    }
    public string Search { get; }
    public List<SearchResultHitData> Hits {
        get {
            EnsureRetrivalOfRelationNodesDataBeforeExitingReadLock(_metrics);
            return _hits!;
        }
    }

    public int Count => Hits.Count;
    public int TotalCount { get; }
    public bool Capped { get; }
    public int PageIndexUsed { get; }
    public int? PageSizeUsed { get; }
    public double DurationMs { get; set; }

    List<IncludeBranch>? _includeBranches;
    public void IncludeBranch(IncludeBranch relationPropertyIdBranch) {
        if (_includeBranches == null) _includeBranches = new();
        _includeBranches.Add(relationPropertyIdBranch);
    }
    public void EnsureRetrivalOfRelationNodesDataBeforeExitingReadLock(Metrics metrics) {
        if (_hits != null) return;
        if (_includeBranches != null) _includeBranches = IncludeUtil.JoinPathsToUniqueBranches(_includeBranches);
        var ids = IdSet.UncachableSet(new FixedOrderedSet(_rawHits.Select(h => h.NodeId), metrics.NodeCount));
        var nodes = IncludeUtil.GetNodesWithIncludes(metrics, ids, _db, _includeBranches);
        _hits = new List<SearchResultHitData>(_rawHits.Count());
        var nodeIdsToGet = _rawHits.Select(h => h.NodeId);
        var nodeIdsById = nodes.ToDictionary(n => n.__Id, n => n);
        var searchTerms = TermSet.Parse(Search, _searchProperty.MinWordLength, _searchProperty.MaxWordLength);
        foreach (var hit in _rawHits) {
            var node = nodeIdsById[hit.NodeId];
            if (node.TryGetValue(_searchProperty.Id, out var textO)) {
                var text = (string)textO;
                var sample = _searchProperty.GetTextSample(searchTerms, text, 255);
                _hits.Add(new(node, hit.Score, sample));
            }
        }
    }
}
