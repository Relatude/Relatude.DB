using WAF.Query.Data;
namespace WAF.Query;
public sealed class QueryOfSearch<T, TInclude> : IQueryExecutable<ResultSetSearch<T>> {
    QueryOfNodes<T, TInclude> _query;
    internal QueryOfSearch(QueryOfNodes<T, TInclude> query) {
        _query = query;
    }
    public QueryOfSearch<T, TInclude> Page(int pageIndex0based, int pageSize) {
        _query.Page(pageIndex0based, pageSize);
        return this;
    }
    public QueryOfSearch<T, TInclude> Top(int count) {
        _query.Take(count);
        return this;
    }
    public override string ToString() {
        return _query.ToString();
    }
    ResultSetSearch<T> buildResult(object data) {
        if (data is not ISearchQueryResultData s)
            throw new NotSupportedException("Only results of type " + nameof(ISearchQueryResultData) + " is supported. Type provided: " + data.GetType().FullName);
        List<SearchResultHit<T>> values = new();
        foreach (var hit in s.Hits) {
            var node = _query.Store.Mapper.CreateObjectFromNodeData<T>(hit.NodeData);
            var searchResultHit = new SearchResultHit<T>(node, hit.Score, hit.Sample);
            values.Add(searchResultHit);
        }
        int count = s.Hits.Count;
        int totalCount = s.Hits.Count;
        int pageIndex = 0;
        int pageSize = s.Hits.Count;
        return new ResultSetSearch<T>(values, count, totalCount, pageIndex, pageSize, s.DurationMs);
    }
    public async Task<ResultSetSearch<T>> ExecuteAsync() {
        var data = await _query.Store.Datastore.QueryAsync(ToString(), _query._q._parameters.ToArray());
        return buildResult(data);
    }
    public ResultSetSearch<T> Execute() {
        var data = _query.Store.Datastore.Query(ToString(), _query._q._parameters.ToArray());
        return buildResult(data);
    }
    IEnumerable<ResultSetSearch<T>> toEnumerable(object data) {
        throw new NotImplementedException();
        //if (data is IStoreNodeDataCollection coll) {
        //    foreach (var nodeData in coll.NodeValues) {
        //        yield return (T)_query.Store._activator.CreateInstance(nodeData);
        //    }
        //}
    }

}
