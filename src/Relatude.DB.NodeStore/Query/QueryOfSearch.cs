using System.Diagnostics.Contracts;
using Relatude.DB.Query.Data;
namespace Relatude.DB.Query;

public sealed class QueryOfSearch<T, TInclude> : IQueryExecutable<ResultSetSearch<T>> {
    readonly QueryOfNodes<T, TInclude> _query;
    internal QueryOfSearch(QueryOfNodes<T, TInclude> query) {
        _query = query;
    }
    // immutable like the node query it wraps: operators return a new search query
    [Pure]
    public QueryOfSearch<T, TInclude> Page(int pageIndex0based, int pageSize) {
        return new QueryOfSearch<T, TInclude>((QueryOfNodes<T, TInclude>)_query.Page(pageIndex0based, pageSize));
    }
    [Pure]
    public QueryOfSearch<T, TInclude> Top(int count) {
        // emitted as Page(0, count): the engine pages search results but does not support Take on them
        return new QueryOfSearch<T, TInclude>((QueryOfNodes<T, TInclude>)_query.Page(0, count));
    }
    public override string ToString() {
        return _query.ToString();
    }
    ResultSetSearch<T> buildResult(object? data) {
        if (data is not ISearchQueryResultData s)
            throw new NotSupportedException("Only results of type " + nameof(ISearchQueryResultData) + " is supported. Type provided: " + data?.GetType().FullName);
        List<SearchResultHit<T>> values = [];
        foreach (var hit in s.Hits) {
            var node = _query.Store.Mapper.CreateObjectFromNodeData<T>(hit.NodeData, null);
            var searchResultHit = new SearchResultHit<T>(node, hit.Score, hit.Sample);
            values.Add(searchResultHit);
        }
        int count = s.Hits.Count;
        int totalCount = s.TotalCount;
        int pageIndex = 0;
        int pageSize = s.Hits.Count;
        return new ResultSetSearch<T>(values, count, totalCount, pageIndex, pageSize, s.DurationMs, s.InnerSearchTimeMs);
    }
    public async Task<ResultSetSearch<T>> ExecuteAsync() {
        var data = await _query.Store.Datastore.QueryAsync(ToString(), _query._q._parameters.ToArray(), _query._q._ctx);
        return buildResult(data);
    }
    public ResultSetSearch<T> Execute() {
        var data = _query.Store.Datastore.Query(ToString(), _query._q._parameters.ToArray(), _query._q._ctx);
        return buildResult(data);
    }
    public object? EvaluateForJson() => _query._q.Prepare().EvaluateForJsonAsync().Result;
    public async Task<object?> EvaluateForJsonAsync() => await _query._q.Prepare().EvaluateForJsonAsync();

}
