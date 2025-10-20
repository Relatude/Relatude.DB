using System.Linq.Expressions;
using System.Text;
using Relatude.DB.Nodes;
using Relatude.DB.Query.Expressions;

namespace Relatude.DB.Query;
public static class QueryOfObjects {
    public static QueryOfObjects<TResult> Select<TSource, TResult>(this QueryOfObjects<TSource> query, Expression<Func<TSource, TResult>> expression) {
        query._q._sb.Append(".Select(");
        query._q._sb.Append(expression.ToQueryString());
        query._q._sb.Append(')');
        return new QueryOfObjects<TResult>(query._q.Store, query._q._sb, query._q._parameters);
    }
    public static int Sum<TSource>(this QueryOfObjects<TSource> query, Expression<Func<TSource, int>> expression) {
        return query._q.Sum(expression).Prepare().EvaluateValue<int>();
    }
    public static double Sum<TSource>(this QueryOfObjects<TSource> query, Expression<Func<TSource, double>> expression) {
        return query._q.Sum(expression).Prepare().EvaluateValue<double>();
    }
    public static float Sum<TSource>(this QueryOfObjects<TSource> query, Expression<Func<TSource, float>> expression) {
        return query._q.Sum(expression).Prepare().EvaluateValue<float>();
    }
    public static decimal Sum<TSource>(this QueryOfObjects<TSource> query, Expression<Func<TSource, decimal>> expression) {
        return query._q.Sum(expression).Prepare().EvaluateValue<decimal>();
    }
    public static int Sum(this QueryOfObjects<int> query) {
        return query._q.Sum().Prepare().EvaluateValue<int>();
    }
    public static double Sum(this QueryOfObjects<double> query) {
        return query._q.Sum().Prepare().EvaluateValue<double>();
    }
}
public class QueryOfObjects<T> : IQueryCollection<ResultSet<T>> {
    internal readonly QueryStringBuilder _q;
    public QueryOfObjects(NodeStore store) {
        _q = new QueryStringBuilder(store, typeof(T).Name);
    }
    public QueryOfObjects(NodeStore store, StringBuilder sb, List<Parameter> parameters) {
        _q = new QueryStringBuilder(store, sb, parameters);
    }
    internal QueryOfObjects(QueryStringBuilder q) {
        _q = q;
    }
    public void Page(int pageIndex, int pageSize) => _q.Page(pageIndex, pageSize);
    public Task<ResultSet<T>> ExecuteAsync() => _q.Prepare().EvaluateSetAsync<T>();
    public ResultSet<T> Execute() => _q.Prepare().EvaluateSet<T>();
    public ResultSet<T> Execute(out int totalCount) { 
        var result = _q.Prepare().EvaluateSet<T>();
        totalCount = result.TotalCount;
        return result;
    }
    public Task<int> CountAsync() => _q.CountAsync();
    public int Count() => _q.Count();
    public QueryOfObjects<T> OrderBy(Expression<Func<T, object>> expression, bool descending = false) {
        _q.OrderBy(expression, descending);
        return this;
    }
    public QueryOfObjects<T> OrderByDescending(Expression<Func<T, object>> expression) {
        _q.OrderBy(expression, true);
        return this;
    }
    //public QueryOfObjects<T> Where(Expression<Func<T, bool>> expression) {
    //    _q.Where(expression);
    //    return this;
    //}
    //public QueryOfObjects<T> Where(string lambdaCodeAsString) {
    //    _q.Where(lambdaCodeAsString);
    //    return this;
    //}
    public override string ToString() => _q.ToString();
}
