using Relatude.DB.Query.Data;
namespace Relatude.DB.Query;

public sealed class QueryOfShortestPath<T, TInclude> : IQueryExecutable<GraphPathResult<T>> {
    QueryOfNodes<T, TInclude> _query;
    internal QueryOfShortestPath(QueryOfNodes<T, TInclude> query) {
        _query = query;
    }
    public override string ToString() {
        return _query.ToString();
    }
    GraphPathResult<T> buildResult(object? data) {
        if (data is not IGraphPathResultData p)
            throw new NotSupportedException("Only results of type " + nameof(IGraphPathResultData) + " is supported. Type provided: " + data?.GetType().FullName);
        var nodes = new T[p.Nodes.Count];
        var i = 0;
        foreach (var nodeData in p.Nodes) nodes[i++] = _query.Store.Mapper.CreateObjectFromNodeData<T>(nodeData, null)!;
        return new GraphPathResult<T>(p.Found, [.. p.NodeIds], nodes, p.DurationMs);
    }
    public async Task<GraphPathResult<T>> ExecuteAsync() {
        var data = await _query.Store.Datastore.QueryAsync(ToString(), _query._q._parameters.ToArray());
        return buildResult(data);
    }
    public GraphPathResult<T> Execute() {
        var data = _query.Store.Datastore.Query(ToString(), _query._q._parameters.ToArray());
        return buildResult(data);
    }
    public object? EvaluateForJson() => _query._q.Prepare().EvaluateForJsonAsync().Result;
    public async Task<object?> EvaluateForJsonAsync() => await _query._q.Prepare().EvaluateForJsonAsync();
}
