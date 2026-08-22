using System.Diagnostics.Contracts;
using System.Linq.Expressions;
using System.Text;
using Relatude.DB.Datamodels;
using Relatude.DB.Nodes;
using Relatude.DB.Query.Expressions;
using Relatude.DB.Query.Linq;

namespace Relatude.DB.Query;
public static class QueryOfObjects {
    [Pure]
    public static QueryOfObjects<TResult> Select<TSource, TResult>(this QueryOfObjects<TSource> query, Expression<Func<TSource, TResult>> expression) {
        var q = query._q.Clone();
        q.Select(expression);
        return new QueryOfObjects<TResult>(q);
    }
    public static int Sum<TSource>(this QueryOfObjects<TSource> query, Expression<Func<TSource, int>> expression) {
        return query._q.Clone().Sum(expression).Prepare().EvaluateValue<int>();
    }
    public static double Sum<TSource>(this QueryOfObjects<TSource> query, Expression<Func<TSource, double>> expression) {
        return query._q.Clone().Sum(expression).Prepare().EvaluateValue<double>();
    }
    public static float Sum<TSource>(this QueryOfObjects<TSource> query, Expression<Func<TSource, float>> expression) {
        return query._q.Clone().Sum(expression).Prepare().EvaluateValue<float>();
    }
    public static decimal Sum<TSource>(this QueryOfObjects<TSource> query, Expression<Func<TSource, decimal>> expression) {
        return query._q.Clone().Sum(expression).Prepare().EvaluateValue<decimal>();
    }
    public static int Sum(this QueryOfObjects<int> query) {
        return query._q.Clone().Sum().Prepare().EvaluateValue<int>();
    }
    public static double Sum(this QueryOfObjects<double> query) {
        return query._q.Clone().Sum().Prepare().EvaluateValue<double>();
    }
}
/// <summary>
/// A query over plain values or projected objects (after Select or SelectId). Immutable like
/// QueryOfNodes: every operator returns a new query, so the result must be used: q = q.Take(10).
/// </summary>
public class QueryOfObjects<T> : IQueryCollection<ResultSet<T>> {
    internal readonly QueryStringBuilder _q; // never mutated after the query object is handed out; operators clone it
    public QueryOfObjects(NodeStore store, QueryContext? ctx) {
        _q = new QueryStringBuilder(store, ctx, typeof(T).Name);
    }
    public QueryOfObjects(NodeStore store, QueryContext? ctx, StringBuilder sb, List<Parameter> parameters) {
        _q = new QueryStringBuilder(store, ctx, sb, parameters);
    }
    internal QueryOfObjects(QueryStringBuilder q) {
        _q = q;
    }
    QueryOfObjects<T> fork(Action<QueryStringBuilder> append) {
        var q = _q.Clone();
        append(q);
        return new QueryOfObjects<T>(q);
    }
    [Pure]
    public QueryOfObjects<T> Page(int pageIndex, int pageSize) => fork(q => q.Page(pageIndex, pageSize));
    [Pure]
    public QueryOfObjects<T> Take(int count) => fork(q => q.Take(count));
    [Pure]
    public QueryOfObjects<T> Skip(int count) => fork(q => q.Skip(count));

    public Task<ResultSet<T>> ExecuteAsync() => _q.Prepare().EvaluateSetAsync<T>()!;
    public ResultSet<T> Execute() => _q.Prepare().EvaluateSet<T>()!;
    public ResultSet<T> Execute(out int totalCount) {
        var result = _q.Prepare().EvaluateSet<T>();
        totalCount = result.TotalCount;
        return result!;
    }
    public object? EvaluateForJson() => _q.Prepare().EvaluateForJsonAsync().Result;
    public async Task<object?> EvaluateForJsonAsync() => await _q.Prepare().EvaluateForJsonAsync();
    public Task<int> CountAsync() => _q.CountAsync();
    public int Count() => _q.Count();
    [Pure]
    public QueryOfObjects<T> OrderBy(Expression<Func<T, object>> expression, bool descending = false) => fork(q => q.OrderBy(expression, descending));
    [Pure]
    public QueryOfObjects<T> OrderByDescending(Expression<Func<T, object>> expression) => fork(q => q.OrderBy(expression, true));
    public override string ToString() => _q.ToString();
}
