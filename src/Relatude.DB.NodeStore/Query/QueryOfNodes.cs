using System.Diagnostics.Contracts;
using System.Linq.Expressions;
using Relatude.DB.Datamodels;
using Relatude.DB.Nodes;

namespace Relatude.DB.Query;

public static class QueryOfNodes {
    [Pure]
    public static QueryOfObjects<TResult> Select<TSource, TInclude, TResult>(this QueryOfNodes<TSource, TInclude> query, Expression<Func<TSource, TResult>> expression) {
        var q = query._q.Clone();
        q.Select(expression);
        return new QueryOfObjects<TResult>(q);
    }
    [Pure]
    public static QueryOfObjects<TResult> Select<TSource, TInclude, TResult>(this IQueryOfNodes<TSource, TInclude> query, Expression<Func<TSource, TResult>> expression) {
        return ((QueryOfNodes<TSource, TInclude>)query).Select(expression);
    }
    public static TResult Sum<TSource, TInclude, TResult>(this QueryOfNodes<TSource, TInclude> query, Expression<Func<TSource, TResult>> expression) {
        return query._q.Clone().Sum(expression).Prepare().EvaluateValue<TResult>();
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
/// <summary>
/// A query over nodes. Immutable, like LINQ queries: every operator returns a NEW query with the
/// clause appended and leaves this one unchanged, so a base query can be stored and forked safely
/// (also across threads). This means the result must always be used: q.Where(...) alone does
/// nothing, write q = q.Where(...). The operators carry [Pure] so an ignored result is flagged by
/// code analysis (CA1806).
/// </summary>
public class QueryOfNodes<TNode, TInclude> : IQueryOfNodes<TNode, TInclude> {
    internal readonly QueryStringBuilder _q; // never mutated after the query object is handed out; operators clone it
    internal QueryOfNodes(QueryStringBuilder q) {
        _q = q;
    }
    internal NodeStore Store { get => _q.Store; }
    public QueryOfNodes(NodeStore store, QueryContext? ctx) {
        _q = new QueryStringBuilder(store, ctx, typeof(TNode).Name);
    }
    public QueryOfNodes(NodeStore store, QueryContext? ctx, string typeName) {
        _q = new QueryStringBuilder(store, ctx, typeName);
    }
    QueryOfNodes<TNode, TInclude> fork(Action<QueryStringBuilder> append) {
        var q = _q.Clone();
        append(q);
        return new QueryOfNodes<TNode, TInclude>(q);
    }
    IncludeQueryOfNodes<TNode, TProperty> forkInclude<TProperty>(Func<QueryStringBuilder, IncludeBranch> createBranch) {
        var q = _q.Clone();
        return new IncludeQueryOfNodes<TNode, TProperty>(q, createBranch(q));
    }
    public Task<ResultSet<TNode>> ExecuteAsync() => _q.Prepare().EvaluateSetAsync<TNode>()!;
    public ResultSet<TNode> Execute() => _q.Prepare().EvaluateSet<TNode>()!;
    public ResultSet<TNode> Execute(out int totalCount) {
        var result = _q.Prepare().EvaluateSet<TNode?>();
        totalCount = result.TotalCount;
        return result!;
    }

    public IQueryOfNodes<TNode, TInclude> OrderBy(Expression<Func<TNode, object>> expression, bool descending = false)
        => fork(q => q.OrderBy(expression, descending));
    public IQueryOfNodes<TNode, TInclude> OrderByDescending(Expression<Func<TNode, object>> expression)
        => fork(q => q.OrderBy(expression, true));
    public IQueryCollection<ResultSet<Guid>> SelectId() {
        var q = _q.Clone();
        q.SelectId();
        return new QueryOfObjects<Guid>(q);
    }
    public IQueryOfNodes<TNode, TInclude> Where(Expression<Func<TNode, bool>> expression)
        => fork(q => q.Where(expression));
    public IQueryOfNodes<TNode, TInclude> Where(Guid id)
        => fork(q => q.Where(id));
    public IQueryOfNodes<TNode, TInclude> Where(int id)
        => fork(q => q.Where(id));
    public IQueryOfNodes<TNode, TInclude> Where(IEnumerable<Guid> ids)
        => fork(q => q.Where(ids));
    public IQueryOfNodes<TNode, TInclude> Where(IEnumerable<int> ids)
        => fork(q => q.Where(ids));
    public IQueryOfNodes<TNode, TInclude> WhereTypes(IEnumerable<Guid> nodeTypes, bool includeDescendants = true)
        => fork(q => q.WhereTypes(nodeTypes, includeDescendants));
    public IQueryOfNodes<TNode, TInclude> WhereTypes(IEnumerable<Type> nodeTypes, bool includeDescendants = true)
        => fork(q => q.WhereTypes(nodeTypes.Select(t => q.Store.Mapper.GetNodeTypeId(t)), includeDescendants));
    public IQueryOfNodes<TNode, TInclude> Where(string lambdaCodeAsString)
        => fork(q => q.Where(lambdaCodeAsString));
    public IQueryOfNodes<TNode, TInclude> WhereRelates(Guid relationPropertyId, Guid nodeId)
        => fork(q => q.Relates(relationPropertyId, nodeId));
    public IQueryOfNodes<TNode, TInclude> WhereRelates<TProperty>(Expression<Func<TNode, TProperty>> relationProperty, Guid nodeId)
        => fork(q => q.Relates(relationProperty, nodeId));
    public IQueryOfNodes<TNode, TInclude> WhereRelates<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty>> relationProperty, Guid nodeId)
        => fork(q => q.Relates(relationProperty, nodeId));
    public IQueryOfNodes<TNode, TInclude> WhereNotRelates<TProperty>(Expression<Func<TNode, TProperty>> relationProperty, Guid nodeId)
        => fork(q => q.RelatesNot(relationProperty, nodeId));
    public IQueryOfNodes<TNode, TInclude> WhereRelatesAny<TProperty>(Expression<Func<TNode, TProperty>> relationProperty, IEnumerable<Guid> nodeIds)
        => fork(q => q.RelatesAny(relationProperty, nodeIds));
    public IQueryOfNodes<TNode, TInclude> WhereIn<TProperty>(Expression<Func<TNode, TProperty>> property, IEnumerable<TProperty> values)
        => fork(q => q.WhereIn(property, values));
    public IQueryOfNodes<TNode, TInclude> WhereIn<TProperty>(string propertyName, IEnumerable<TProperty> values)
        => fork(q => q.WhereIn(propertyName, values));
    public IQueryOfNodes<TNode, TInclude> WhereInIds(IEnumerable<Guid> ids)
        => fork(q => q.WhereInIds(ids));
    public IQueryOfNodes<TNode, TInclude> WhereCulture(string? cultureCode)
        => fork(q => q.WhereCulture(cultureCode));
    public IQueryOfNodes<TNode, TInclude> WhereCulture(Guid cultureId)
        => fork(q => q.WhereCulture(cultureId));
    public IQueryOfNodes<TNode, TInclude> WhereHidden(bool include)
        => fork(q => q.WhereHidden(include));
    public IQueryOfNodes<TNode, TInclude> WhereCultureFallback(bool include)
        => fork(q => q.WhereCultureFallback(include));
    public IQueryOfNodes<TNode, TInclude> Page(int pageIndex0based, int pageSize)
        => fork(q => q.Page(pageIndex0based, pageSize));
    public IQueryOfNodes<TNode, TInclude> Take(int count)
        => fork(q => q.Take(count));
    public IQueryOfNodes<TNode, TInclude> Skip(int offset)
        => fork(q => q.Skip(offset));
    // Count and Sum evaluate a snapshot; the query object stays unchanged and usable
    public Task<int> CountAsync() => _q.CountAsync();
    public int Count() => _q.Count();

    public TProperty Sum<TProperty>(Expression<Func<TNode, TProperty>> property) {
        return _q.Clone().Sum(property).Prepare().EvaluateValue<TProperty>();
    }

    public QueryOfFacets<TNode, TInclude> Facets() {
        return new QueryOfFacets<TNode, TInclude>(this);
    }
    public IQueryOfNodes<TNode, TInclude> WhereSearch(string? text, double? semanticRatio = null, float? minimumVectorSimilarity = null, bool? orSearch = null, int? maxWordsEvaluatedWhenFuzzy = null)
        => fork(q => q.WhereSearch(text, semanticRatio, minimumVectorSimilarity, orSearch, maxWordsEvaluatedWhenFuzzy));
    public QueryOfSearch<TNode, TInclude> Search(string text, double? semanticRatio = null, float? minimumVectorSimilarity = null, bool? orSearch = null, int? maxWordsEvaluatedWhenFuzzy = null, int? maxHitsEvaluatedBeforeRanked = null) {
        return new QueryOfSearch<TNode, TInclude>(fork(q => q.Search(text, semanticRatio, minimumVectorSimilarity, orSearch, maxWordsEvaluatedWhenFuzzy, maxHitsEvaluatedBeforeRanked)));
    }

    IQueryOfNodes<TProperty, TProperty> traverse<TProperty>(Guid propertyId, int maxLevel, int minLevel, GraphDirection direction, int? maxVisited) {
        var q = _q.Clone();
        q.Traverse(propertyId.ToString(), minLevel, maxLevel, direction, maxVisited);
        return new QueryOfNodes<TProperty, TProperty>(q); // the query continues re-typed to the related node type
    }
    public IQueryOfNodes<TProperty, TProperty> Traverse<TProperty>(Expression<Func<TNode, TProperty>> relationProperty, int maxLevel, int minLevel = 1, GraphDirection direction = GraphDirection.Default, int? maxVisited = null)
        => traverse<TProperty>(Store.Mapper.GetProperty(relationProperty).Id, maxLevel, minLevel, direction, maxVisited);
    public IQueryOfNodes<TProperty, TProperty> Traverse<TProperty>(Expression<Func<TNode, IEnumerable<TProperty>>> relationProperty, int maxLevel, int minLevel = 1, GraphDirection direction = GraphDirection.Default, int? maxVisited = null)
        => traverse<TProperty>(Store.Mapper.GetProperty(relationProperty).Id, maxLevel, minLevel, direction, maxVisited);
    public IQueryOfNodes<TProperty, TProperty> Traverse<TProperty>(Expression<Func<TNode, TProperty[]?>> relationProperty, int maxLevel, int minLevel = 1, GraphDirection direction = GraphDirection.Default, int? maxVisited = null)
        => traverse<TProperty>(Store.Mapper.GetProperty(relationProperty).Id, maxLevel, minLevel, direction, maxVisited);
    public IQueryOfNodes<TProperty, TProperty> Traverse<TProperty>(Expression<Func<TNode, ICollection<TProperty>>> relationProperty, int maxLevel, int minLevel = 1, GraphDirection direction = GraphDirection.Default, int? maxVisited = null)
        => traverse<TProperty>(Store.Mapper.GetProperty(relationProperty).Id, maxLevel, minLevel, direction, maxVisited);

    public QueryOfShortestPath<TNode, TInclude> ShortestPath<TProperty>(Expression<Func<TNode, TProperty>> relationProperty, Guid fromNodeId, Guid toNodeId, int maxLevel = 1000, GraphDirection direction = GraphDirection.Default, int? maxVisited = null) {
        return new QueryOfShortestPath<TNode, TInclude>(fork(q => q.ShortestPath(Store.Mapper.GetProperty(relationProperty).Id.ToString(), fromNodeId, toNodeId, maxLevel, direction, maxVisited)));
    }

    public IIncludeQueryOfNodes<TNode, TProperty> Include<TProperty>(Expression<Func<TNode, TProperty>> expression, int? top = null)
        => forkInclude<TProperty>(q => q.CreateBranch(expression, top));
    public IIncludeQueryOfNodes<TNode, TProperty> Include<TProperty>(Expression<Func<TNode, TProperty[]?>> expression, int? top = null)
        => forkInclude<TProperty>(q => q.CreateBranch(expression, top));
    public IIncludeQueryOfNodes<TNode, TProperty> Include<TProperty>(Expression<Func<TNode, IEnumerable<TProperty>>> expression, int? top = null)
        => forkInclude<TProperty>(q => q.CreateBranch(expression, top));
    public IIncludeQueryOfNodes<TNode, TProperty> Include<TProperty>(Expression<Func<TNode, ICollection<TProperty>>> expression, int? top = null)
        => forkInclude<TProperty>(q => q.CreateBranch(expression, top));
    public IIncludeQueryOfNodes<TNode, TProperty> Preload<TProperty>(Expression<Func<TNode, IRelationProperty<TProperty>>> expression, int? top = null)
        => forkInclude<TProperty>(q => q.CreateBranch(expression, top));
    public IIncludeQueryOfNodes<TNode, TProperty> Preload<TProperty>(Expression<Func<TNode, IReference<TProperty>>> expression, int? top = null)
        => forkInclude<TProperty>(q => q.CreateBranch(expression, top));
    public IIncludeQueryOfNodes<TNode, TProperty> Preload<TProperty>(Expression<Func<TNode, IReferences<TProperty>>> expression, int? top = null)
        => forkInclude<TProperty>(q => q.CreateBranch(expression, top));

    // Include / Preload with a filter on the related nodes: only nodes passing the filter are loaded.
    // The filter never affects the main result set, and it is applied before top.
    public IIncludeQueryOfNodes<TNode, TProperty> Include<TProperty>(Expression<Func<TNode, TProperty>> expression, Expression<Func<TProperty, bool>> filter, int? top = null)
        => forkInclude<TProperty>(q => q.CreateBranch(expression, top, filter));
    public IIncludeQueryOfNodes<TNode, TProperty> Include<TProperty>(Expression<Func<TNode, TProperty[]?>> expression, Expression<Func<TProperty, bool>> filter, int? top = null)
        => forkInclude<TProperty>(q => q.CreateBranch(expression, top, filter));
    public IIncludeQueryOfNodes<TNode, TProperty> Include<TProperty>(Expression<Func<TNode, IEnumerable<TProperty>>> expression, Expression<Func<TProperty, bool>> filter, int? top = null)
        => forkInclude<TProperty>(q => q.CreateBranch(expression, top, filter));
    public IIncludeQueryOfNodes<TNode, TProperty> Include<TProperty>(Expression<Func<TNode, ICollection<TProperty>>> expression, Expression<Func<TProperty, bool>> filter, int? top = null)
        => forkInclude<TProperty>(q => q.CreateBranch(expression, top, filter));
    public IIncludeQueryOfNodes<TNode, TProperty> Preload<TProperty>(Expression<Func<TNode, IRelationProperty<TProperty>>> expression, Expression<Func<TProperty, bool>> filter, int? top = null)
        => forkInclude<TProperty>(q => q.CreateBranch(expression, top, filter));
    public IIncludeQueryOfNodes<TNode, TProperty> Preload<TProperty>(Expression<Func<TNode, IReference<TProperty>>> expression, Expression<Func<TProperty, bool>> filter, int? top = null)
        => forkInclude<TProperty>(q => q.CreateBranch(expression, top, filter));
    public IIncludeQueryOfNodes<TNode, TProperty> Preload<TProperty>(Expression<Func<TNode, IReferences<TProperty>>> expression, Expression<Func<TProperty, bool>> filter, int? top = null)
        => forkInclude<TProperty>(q => q.CreateBranch(expression, top, filter));
    public IIncludeQueryOfNodes<TNode, TProperty> Include<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty>> expression, Expression<Func<TProperty, bool>> filter, int? top = null)
        => forkInclude<TProperty>(q => q.CreateBranch(expression, top, filter));
    public IIncludeQueryOfNodes<TNode, TProperty> Include<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty[]>> expression, Expression<Func<TProperty, bool>> filter, int? top = null)
        => forkInclude<TProperty>(q => q.CreateBranch(expression, top, filter));
    public IIncludeQueryOfNodes<TNode, TProperty> Include<TSubClass, TProperty>(Expression<Func<TSubClass, IEnumerable<TProperty>>> expression, Expression<Func<TProperty, bool>> filter, int? top = null)
        => forkInclude<TProperty>(q => q.CreateBranch(expression, top, filter));
    public IIncludeQueryOfNodes<TNode, TProperty> Include<TSubClass, TProperty>(Expression<Func<TSubClass, ICollection<TProperty>>> expression, Expression<Func<TProperty, bool>> filter, int? top = null)
        => forkInclude<TProperty>(q => q.CreateBranch(expression, top, filter));
    public IIncludeQueryOfNodes<TNode, TProperty> Preload<TSubClass, TProperty>(Expression<Func<TSubClass, IRelationProperty<TProperty>>> expression, Expression<Func<TProperty, bool>> filter, int? top = null)
        => forkInclude<TProperty>(q => q.CreateBranch(expression, top, filter));
    public IIncludeQueryOfNodes<TNode, TProperty> Preload<TSubClass, TProperty>(Expression<Func<TSubClass, IReference<TProperty>>> expression, Expression<Func<TProperty, bool>> filter, int? top = null)
        => forkInclude<TProperty>(q => q.CreateBranch(expression, top, filter));
    public IIncludeQueryOfNodes<TNode, TProperty> Preload<TSubClass, TProperty>(Expression<Func<TSubClass, IReferences<TProperty>>> expression, Expression<Func<TProperty, bool>> filter, int? top = null)
        => forkInclude<TProperty>(q => q.CreateBranch(expression, top, filter));

    public IIncludeQueryOfNodes<TNode, TProperty> Include<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty>> expression, int? top = null)
        => forkInclude<TProperty>(q => q.CreateBranch(expression, top));
    public IIncludeQueryOfNodes<TNode, TProperty> Include<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty[]>> expression, int? top = null)
        => forkInclude<TProperty>(q => q.CreateBranch(expression, top));
    public IIncludeQueryOfNodes<TNode, TProperty> Include<TSubClass, TProperty>(Expression<Func<TSubClass, ICollection<TProperty>>> expression, int? top = null)
        => forkInclude<TProperty>(q => q.CreateBranch(expression, top));
    public IIncludeQueryOfNodes<TNode, TProperty> Include<TSubClass, TProperty>(Expression<Func<TSubClass, IEnumerable<TProperty>>> expression, int? top = null)
        => forkInclude<TProperty>(q => q.CreateBranch(expression, top));
    public IIncludeQueryOfNodes<TNode, TProperty> Preload<TSubClass, TProperty>(Expression<Func<TSubClass, IRelationProperty<TProperty>>> expression, int? top = null)
        => forkInclude<TProperty>(q => q.CreateBranch(expression, top));
    public IIncludeQueryOfNodes<TNode, TProperty> Preload<TSubClass, TProperty>(Expression<Func<TSubClass, IReference<TProperty>>> expression, int? top = null)
        => forkInclude<TProperty>(q => q.CreateBranch(expression, top));
    public IIncludeQueryOfNodes<TNode, TProperty> Preload<TSubClass, TProperty>(Expression<Func<TSubClass, IReferences<TProperty>>> expression, int? top = null)
        => forkInclude<TProperty>(q => q.CreateBranch(expression, top));

    public object? EvaluateForJson() => _q.Prepare().EvaluateForJsonAsync().Result;
    public async Task<object?> EvaluateForJsonAsync() => await _q.Prepare().EvaluateForJsonAsync();

    public override string ToString() => _q.ToString();

}
public class IncludeQueryOfNodes<TNode, TInclude> : QueryOfNodes<TNode, TInclude>, IIncludeQueryOfNodes<TNode, TInclude> {
    readonly IncludeBranch _branch; // belongs to _q's branch tree; forks translate it to the cloned tree
    internal IncludeQueryOfNodes(QueryStringBuilder q, IncludeBranch branch) : base(q) {
        _branch = branch;
    }
    IncludeQueryOfNodes<TNode, TProperty> forkThen<TProperty>(Func<QueryStringBuilder, IncludeBranch, IncludeBranch> createChild) {
        var map = new Dictionary<IncludeBranch, IncludeBranch>();
        var q = _q.Clone(map);
        return new IncludeQueryOfNodes<TNode, TProperty>(q, createChild(q, map[_branch]));
    }
    public IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TProperty>(Expression<Func<TInclude, TProperty>> expression, int? top = null)
        => forkThen<TProperty>((q, parent) => q.CreateChildBranch(parent, expression, top));
    public IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TProperty>(Expression<Func<TInclude, TProperty[]>> expression, int? top = null)
        => forkThen<TProperty>((q, parent) => q.CreateChildBranch(parent, expression, top));
    public IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TProperty>(Expression<Func<TInclude, IEnumerable<TProperty>>> expression, int? top = null)
        => forkThen<TProperty>((q, parent) => q.CreateChildBranch(parent, expression, top));
    public IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TProperty>(Expression<Func<TInclude, ICollection<TProperty>>> expression, int? top = null)
        => forkThen<TProperty>((q, parent) => q.CreateChildBranch(parent, expression, top));
    public IIncludeQueryOfNodes<TNode, TProperty> ThenPreload<TProperty>(Expression<Func<TInclude, IRelationProperty<TProperty>>> expression, int? top = null)
        => forkThen<TProperty>((q, parent) => q.CreateChildBranch(parent, expression, top));
    public IIncludeQueryOfNodes<TNode, TProperty> ThenPreload<TProperty>(Expression<Func<TInclude, IReferences<TProperty>>> expression, int? top = null)
        => forkThen<TProperty>((q, parent) => q.CreateChildBranch(parent, expression, top));
    public IIncludeQueryOfNodes<TNode, TProperty> ThenPreload<TProperty>(Expression<Func<TInclude, IReference<TProperty>>> expression, int? top = null)
        => forkThen<TProperty>((q, parent) => q.CreateChildBranch(parent, expression, top));

    public IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty>> expression, int? top = null)
        => forkThen<TProperty>((q, parent) => q.CreateChildBranch(parent, expression, top));
    public IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty[]>> expression, int? top = null)
        => forkThen<TProperty>((q, parent) => q.CreateChildBranch(parent, expression, top));
    public IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TSubClass, TProperty>(Expression<Func<TSubClass, ICollection<TProperty>>> expression, int? top = null)
        => forkThen<TProperty>((q, parent) => q.CreateChildBranch(parent, expression, top));
    public IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TSubClass, TProperty>(Expression<Func<TSubClass, IEnumerable<TProperty>>> expression, int? top = null)
        => forkThen<TProperty>((q, parent) => q.CreateChildBranch(parent, expression, top));
    public IIncludeQueryOfNodes<TNode, TProperty> ThenPreload<TSubClass, TProperty>(Expression<Func<TSubClass, IRelationProperty<TProperty>>> expression, int? top = null)
        => forkThen<TProperty>((q, parent) => q.CreateChildBranch(parent, expression, top));
    public IIncludeQueryOfNodes<TNode, TProperty> ThenPreload<TSubClass, TProperty>(Expression<Func<TSubClass, IReference<TProperty>>> expression, int? top = null)
        => forkThen<TProperty>((q, parent) => q.CreateChildBranch(parent, expression, top));
    public IIncludeQueryOfNodes<TNode, TProperty> ThenPreload<TSubClass, TProperty>(Expression<Func<TSubClass, IReferences<TProperty>>> expression, int? top = null)
        => forkThen<TProperty>((q, parent) => q.CreateChildBranch(parent, expression, top));

    // ThenInclude / ThenPreload with a filter on the related nodes of the deeper level:
    public IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TProperty>(Expression<Func<TInclude, TProperty>> expression, Expression<Func<TProperty, bool>> filter, int? top = null)
        => forkThen<TProperty>((q, parent) => q.CreateChildBranch(parent, expression, top, filter));
    public IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TProperty>(Expression<Func<TInclude, TProperty[]>> expression, Expression<Func<TProperty, bool>> filter, int? top = null)
        => forkThen<TProperty>((q, parent) => q.CreateChildBranch(parent, expression, top, filter));
    public IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TProperty>(Expression<Func<TInclude, IEnumerable<TProperty>>> expression, Expression<Func<TProperty, bool>> filter, int? top = null)
        => forkThen<TProperty>((q, parent) => q.CreateChildBranch(parent, expression, top, filter));
    public IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TProperty>(Expression<Func<TInclude, ICollection<TProperty>>> expression, Expression<Func<TProperty, bool>> filter, int? top = null)
        => forkThen<TProperty>((q, parent) => q.CreateChildBranch(parent, expression, top, filter));
    public IIncludeQueryOfNodes<TNode, TProperty> ThenPreload<TProperty>(Expression<Func<TInclude, IRelationProperty<TProperty>>> expression, Expression<Func<TProperty, bool>> filter, int? top = null)
        => forkThen<TProperty>((q, parent) => q.CreateChildBranch(parent, expression, top, filter));
    public IIncludeQueryOfNodes<TNode, TProperty> ThenPreload<TProperty>(Expression<Func<TInclude, IReference<TProperty>>> expression, Expression<Func<TProperty, bool>> filter, int? top = null)
        => forkThen<TProperty>((q, parent) => q.CreateChildBranch(parent, expression, top, filter));
    public IIncludeQueryOfNodes<TNode, TProperty> ThenPreload<TProperty>(Expression<Func<TInclude, IReferences<TProperty>>> expression, Expression<Func<TProperty, bool>> filter, int? top = null)
        => forkThen<TProperty>((q, parent) => q.CreateChildBranch(parent, expression, top, filter));
    public IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty>> expression, Expression<Func<TProperty, bool>> filter, int? top = null)
        => forkThen<TProperty>((q, parent) => q.CreateChildBranch(parent, expression, top, filter));
    public IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty[]>> expression, Expression<Func<TProperty, bool>> filter, int? top = null)
        => forkThen<TProperty>((q, parent) => q.CreateChildBranch(parent, expression, top, filter));
    public IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TSubClass, TProperty>(Expression<Func<TSubClass, IEnumerable<TProperty>>> expression, Expression<Func<TProperty, bool>> filter, int? top = null)
        => forkThen<TProperty>((q, parent) => q.CreateChildBranch(parent, expression, top, filter));
    public IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TSubClass, TProperty>(Expression<Func<TSubClass, ICollection<TProperty>>> expression, Expression<Func<TProperty, bool>> filter, int? top = null)
        => forkThen<TProperty>((q, parent) => q.CreateChildBranch(parent, expression, top, filter));
    public IIncludeQueryOfNodes<TNode, TProperty> ThenPreload<TSubClass, TProperty>(Expression<Func<TSubClass, IRelationProperty<TProperty>>> expression, Expression<Func<TProperty, bool>> filter, int? top = null)
        => forkThen<TProperty>((q, parent) => q.CreateChildBranch(parent, expression, top, filter));
    public IIncludeQueryOfNodes<TNode, TProperty> ThenPreload<TSubClass, TProperty>(Expression<Func<TSubClass, IReference<TProperty>>> expression, Expression<Func<TProperty, bool>> filter, int? top = null)
        => forkThen<TProperty>((q, parent) => q.CreateChildBranch(parent, expression, top, filter));
    public IIncludeQueryOfNodes<TNode, TProperty> ThenPreload<TSubClass, TProperty>(Expression<Func<TSubClass, IReferences<TProperty>>> expression, Expression<Func<TProperty, bool>> filter, int? top = null)
        => forkThen<TProperty>((q, parent) => q.CreateChildBranch(parent, expression, top, filter));
}
