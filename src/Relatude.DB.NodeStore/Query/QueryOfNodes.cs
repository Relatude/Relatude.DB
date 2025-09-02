using System.Linq.Expressions;
using Relatude.DB.Datamodels;
using Relatude.DB.Nodes;
using Relatude.DB.Query.Expressions;

namespace Relatude.DB.Query;
public static class QueryOfNodes {
    public static QueryOfObjects<TResult> Select<TSource, TInclude, TResult>(this QueryOfNodes<TSource, TInclude> query, Expression<Func<TSource, TResult>> expression) {
        query._q.Select(expression);
        return new QueryOfObjects<TResult>(query._q.Store, query._q._sb, query._q._parameters);
    }
    public static QueryOfObjects<TResult> Select<TSource, TInclude, TResult>(this IQueryOfNodes<TSource, TInclude> query, Expression<Func<TSource, TResult>> expression) {
        return ((QueryOfNodes<TSource, TInclude>)query).Select(expression);
    }
    public static TResult Sum<TSource, TInclude, TResult>(this QueryOfNodes<TSource, TInclude> query, Expression<Func<TSource, TResult>> expression) {
        return query._q.Sum(expression).Prepare().EvaluateValue<TResult>();
    }
    public static int Sum<TSource, TInclude>(this IQueryOfNodes<TSource, TInclude> query, Expression<Func<TSource, int>> expression) {
        return ((QueryOfNodes<TSource, TInclude>)query).Sum(expression);
    }
    public static float Sum<TSource, TInclude>(this IQueryOfNodes<TSource, TInclude> query, Expression<Func<TSource, float>> expression) {
        return ((QueryOfNodes<TSource, TInclude>)query).Sum(expression);
    }
    public static decimal Sum<TSource, TInclude>(this IQueryOfNodes<TSource, TInclude> query, Expression<Func<TSource, decimal>> expression) {
        return ((QueryOfNodes<TSource, TInclude>)query).Sum(expression);
    }
    public static double Sum<TSource, TInclude>(this IQueryOfNodes<TSource, TInclude> query, Expression<Func<TSource, double>> expression) {
        return ((QueryOfNodes<TSource, TInclude>)query).Sum(expression);
    }
}
public class QueryOfNodes<TNode, TInclude> : IQueryOfNodes<TNode, TInclude> {
    internal QueryStringBuilder _q;
    internal QueryOfNodes(QueryStringBuilder q) {
        _q = q;
    }
    internal NodeStore Store { get => _q.Store; }
    public QueryOfNodes(NodeStore store) {
        _q = new QueryStringBuilder(store, typeof(TNode).Name);
    }
    public QueryOfNodes(NodeStore store, string typeName) {
        _q = new QueryStringBuilder(store, typeName);
    }
    public Task<ResultSet<TNode>> ExecuteAsync() => _q.Prepare().EvaluateSetAsync<TNode>();
    public ResultSet<TNode> Execute() => _q.Prepare().EvaluateSet<TNode>();
    public ResultSet<TNode> Execute(out int totalCount) {
        var result = _q.Prepare().EvaluateSet<TNode>();
        totalCount = result.TotalCount;
        return result;
    }

    public IQueryOfNodes<TNode, TInclude> OrderBy(Expression<Func<TNode, object>> expression, bool descending = false) {
        _q.OrderBy(expression, descending);
        return this;
    }
    public IQueryOfNodes<TNode, TInclude> OrderByDescending(Expression<Func<TNode, object>> expression) {
        _q.OrderBy(expression, true);
        return this;
    }
    public IQueryCollection<ResultSet<Guid>> SelectId() {
        _q.SelectId();
        return new QueryOfObjects<Guid>(_q);
    }
    public IQueryOfNodes<TNode, TInclude> Where(Expression<Func<TNode, bool>> expression) {
        _q.Where(expression);
        return this;
    }
    public IQueryOfNodes<TNode, TInclude> Where(Guid id) {
        _q.Where(id);
        return this;
    }
    public IQueryOfNodes<TNode, TInclude> Where(int id) {
        _q.Where(id);
        return this;
    }
    public IQueryOfNodes<TNode, TInclude> Where(IEnumerable<Guid> ids) {
        _q.Where(ids);
        return this;
    }
    public IQueryOfNodes<TNode, TInclude> Where(IEnumerable<int> ids) {
        _q.Where(ids);
        return this;
    }
    public IQueryOfNodes<TNode, TInclude> WhereTypes(IEnumerable<Guid> nodeTypes) {
        _q.WhereTypes(nodeTypes);
        return this;
    }
    public IQueryOfNodes<TNode, TInclude> WhereTypes(IEnumerable<Type> nodeTypes) {
        _q.WhereTypes(nodeTypes.Select(t => _q.Store.Mapper.GetNodeTypeId(t)));
        return this;
    }
    public IQueryOfNodes<TNode, TInclude> Where(string lambdaCodeAsString) {
        _q.Where(lambdaCodeAsString);
        return this;
    }
    public IQueryOfNodes<TNode, TInclude> WhereRelates(Guid relationPropertyId, Guid nodeId) {
        _q.Relates(relationPropertyId, nodeId);
        return this;
    }
    public IQueryOfNodes<TNode, TInclude> WhereRelates<TProperty>(Expression<Func<TNode, TProperty>> relationProperty, Guid nodeId) {
        _q.Relates(relationProperty, nodeId);
        return this;
    }
    public IQueryOfNodes<TNode, TInclude> WhereRelates<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty>> relationProperty, Guid nodeId) {
        _q.Relates(relationProperty, nodeId);
        return this;
    }
    public IQueryOfNodes<TNode, TInclude> WhereNotRelates<TProperty>(Expression<Func<TNode, TProperty>> relationProperty, Guid nodeId) {
        _q.RelatesNot(relationProperty, nodeId);
        return this;
    }
    public IQueryOfNodes<TNode, TInclude> WhereRelatesAny<TProperty>(Expression<Func<TNode, TProperty>> relationProperty, IEnumerable<Guid> nodeIds) {
        _q.RelatesAny(relationProperty, nodeIds);
        return this;
    }
    public IQueryOfNodes<TNode, TInclude> WhereIn<TProperty>(Expression<Func<TNode, TProperty>> property, IEnumerable<TProperty> values) {
        _q.WhereIn(property, values);
        return this;
    }
    public IQueryOfNodes<TNode, TInclude> WhereIn<TProperty>(string propertyName, IEnumerable<TProperty> values) {
        _q.WhereIn(propertyName, values);
        return this;
    }
    public IQueryOfNodes<TNode, TInclude> WhereInIds(IEnumerable<Guid> ids) {
        _q.WhereInIds(ids);
        return this;
    }
    public IQueryOfNodes<TNode, TInclude> Page(int pageIndex0based, int pageSize) {
        _q.Page(pageIndex0based, pageSize);
        return this;
    }
    public IQueryOfNodes<TNode, TInclude> Take(int count) {
        _q.Take(count);
        return this;
    }
    public IQueryOfNodes<TNode, TInclude> Skip(int offset) {
        _q.Skip(offset);
        return this;
    }
    public Task<int> CountAsync() => _q.CountAsync();
    public int Count() => _q.Count();

    public TProperty Sum<TProperty>(Expression<Func<TNode, TProperty>> property) {
        return _q.Sum(property).Prepare().EvaluateValue<TProperty>();
    }

    public QueryOfFacets<TNode, TInclude> Facets() {
        return new QueryOfFacets<TNode, TInclude>(this);
    }
    public IQueryOfNodes<TNode, TInclude> WhereSearch(string? text, double? semanticRatio = null) {
        _q.WhereSearch(text, semanticRatio);
        return this;
    }
    public QueryOfSearch<TNode, TInclude> Search(string text, double? semanticRatio = null) {
        _q.Search(text, semanticRatio);
        return new QueryOfSearch<TNode, TInclude>(this);
    }

    //public IIncludeQueryOfNodes<TNode, TProperty> Include<TProperty>(Guid relationPropertyId, int? top = null) {
    //    var branch = _q.CreateBranch(relationPropertyId, top);
    //    return new IncludeQueryOfNodes<TNode, TProperty>(_q, branch);
    //}
    public IIncludeQueryOfNodes<TNode, TProperty> Include<TProperty>(Expression<Func<TNode, TProperty>> expression, int? top = null) {
        var branch = _q.CreateBranch(expression, top);
        return new IncludeQueryOfNodes<TNode, TProperty>(_q, branch);
    }
    public IIncludeQueryOfNodes<TNode, TProperty> Include<TProperty>(Expression<Func<TNode, TProperty[]?>> expression, int? top = null) {
        var branch = _q.CreateBranch(expression, top);
        return new IncludeQueryOfNodes<TNode, TProperty>(_q, branch);
    }
    public IIncludeQueryOfNodes<TNode, TProperty> Include<TProperty>(Expression<Func<TNode, IEnumerable<TProperty>>> expression, int? top = null) {
        var branch = _q.CreateBranch(expression, top);
        return new IncludeQueryOfNodes<TNode, TProperty>(_q, branch);
    }
    public IIncludeQueryOfNodes<TNode, TProperty> Include<TProperty>(Expression<Func<TNode, ICollection<TProperty>>> expression, int? top = null) {
        var branch = _q.CreateBranch(expression, top);
        return new IncludeQueryOfNodes<TNode, TProperty>(_q, branch);
    }

    //public IIncludeQueryOfNodes<TNode, TProperty> Include<TSubClass, TProperty>(Guid relationPropertyId, int? top = null) {
    //    return new IncludeQueryOfNodes<TNode, TProperty>(_q, _q.CreateBranch(relationPropertyId, top));
    //}
    public IIncludeQueryOfNodes<TNode, TProperty> Include<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty>> expression, int? top = null) {
        return new IncludeQueryOfNodes<TNode, TProperty>(_q, _q.CreateBranch(expression, top));
    }
    public IIncludeQueryOfNodes<TNode, TProperty> Include<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty[]>> expression, int? top = null) {
        return new IncludeQueryOfNodes<TNode, TProperty>(_q, _q.CreateBranch(expression, top));
    }
    public IIncludeQueryOfNodes<TNode, TProperty> Include<TSubClass, TProperty>(Expression<Func<TSubClass, ICollection<TProperty>>> expression, int? top = null) {
        return new IncludeQueryOfNodes<TNode, TProperty>(_q, _q.CreateBranch(expression, top));
    }
    public IIncludeQueryOfNodes<TNode, TProperty> Include<TSubClass, TProperty>(Expression<Func<TSubClass, IEnumerable<TProperty>>> expression, int? top = null) {
        return new IncludeQueryOfNodes<TNode, TProperty>(_q, _q.CreateBranch(expression, top));
    }

    public override string ToString() => _q.ToString();

}
public class IncludeQueryOfNodes<TNode, TInclude> : QueryOfNodes<TNode, TInclude>, IIncludeQueryOfNodes<TNode, TInclude> {
    IncludeBranch _branch;
    internal IncludeQueryOfNodes(QueryStringBuilder q, IncludeBranch branch) : base(q) {
        _branch = branch;
    }
    public IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TProperty>(Expression<Func<TInclude, TProperty>> expression, int? top = null) {
        return new IncludeQueryOfNodes<TNode, TProperty>(_q, _q.CreateChildBranch(_branch, expression, top));
    }
    public IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TProperty>(Expression<Func<TInclude, TProperty[]>> expression, int? top = null) {
        return new IncludeQueryOfNodes<TNode, TProperty>(_q, _q.CreateChildBranch(_branch, expression, top));
    }
    public IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TProperty>(Expression<Func<TInclude, IEnumerable<TProperty>>> expression, int? top = null) {
        return new IncludeQueryOfNodes<TNode, TProperty>(_q, _q.CreateChildBranch(_branch, expression, top));
    }
    public IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TProperty>(Expression<Func<TInclude, ICollection<TProperty>>> expression, int? top = null) {
        return new IncludeQueryOfNodes<TNode, TProperty>(_q, _q.CreateChildBranch(_branch, expression, top));
    }
    public IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty>> expression, int? top = null) {
        return new IncludeQueryOfNodes<TNode, TProperty>(_q, _q.CreateChildBranch(_branch, expression, top));
    }
    public IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty[]>> expression, int? top = null) {
        return new IncludeQueryOfNodes<TNode, TProperty>(_q, _q.CreateChildBranch(_branch, expression, top));
    }
    public IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TSubClass, TProperty>(Expression<Func<TSubClass, ICollection<TProperty>>> expression, int? top = null) {
        return new IncludeQueryOfNodes<TNode, TProperty>(_q, _q.CreateChildBranch(_branch, expression, top));
    }
    public IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TSubClass, TProperty>(Expression<Func<TSubClass, IEnumerable<TProperty>>> expression, int? top = null) {
        return new IncludeQueryOfNodes<TNode, TProperty>(_q, _q.CreateChildBranch(_branch, expression, top));
    }

    //IIncludeQueryOfNodes<TNode, TProperty> IIncludeQueryOfNodes<TNode, TInclude>.ThenInclude<TProperty>(Expression<Func<TInclude, TProperty>> relationProperty, int? top) {
    //    throw new NotImplementedException();
    //}
    //IIncludeQueryOfNodes<TNode, TProperty> IIncludeQueryOfNodes<TNode, TInclude>.ThenInclude<TProperty>(Expression<Func<TInclude, TProperty[]>> relationProperty, int? top) {
    //    throw new NotImplementedException();
    //}
    //IIncludeQueryOfNodes<TNode, TProperty> IIncludeQueryOfNodes<TNode, TInclude>.ThenInclude<TProperty>(Expression<Func<TInclude, IEnumerable<TProperty>>> relationProperty, int? top) {
    //    throw new NotImplementedException();
    //}
    //IIncludeQueryOfNodes<TNode, TProperty> IIncludeQueryOfNodes<TNode, TInclude>.ThenInclude<TProperty>(Expression<Func<TInclude, ICollection<TProperty>>> relationProperty, int? top) {
    //    throw new NotImplementedException();
    //}
    //IIncludeQueryOfNodes<TNode, TProperty> IIncludeQueryOfNodes<TNode, TInclude>.ThenInclude<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty>> relationProperty, int? top) {
    //    throw new NotImplementedException();
    //}
    //IIncludeQueryOfNodes<TNode, TProperty> IIncludeQueryOfNodes<TNode, TInclude>.ThenInclude<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty[]>> relationProperty, int? top) {
    //    throw new NotImplementedException();
    //}
    //IIncludeQueryOfNodes<TNode, TProperty> IIncludeQueryOfNodes<TNode, TInclude>.ThenInclude<TSubClass, TProperty>(Expression<Func<TSubClass, IEnumerable<TProperty>>> relationProperty, int? top) {
    //    throw new NotImplementedException();
    //}
    //IIncludeQueryOfNodes<TNode, TProperty> IIncludeQueryOfNodes<TNode, TInclude>.ThenInclude<TSubClass, TProperty>(Expression<Func<TSubClass, ICollection<TProperty>>> relationProperty, int? top) {
    //    throw new NotImplementedException();
    //}
}
