using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Linq.Expressions;
using Relatude.DB.Datamodels;
using Relatude.DB.Nodes;
using Relatude.DB.Transactions;
namespace Relatude.DB.Query;

/// <summary>
/// A query over nodes. Queries are immutable, like LINQ queries: every operator returns a NEW query
/// with the clause appended and leaves the original unchanged, so a base query can be stored and
/// forked safely (also across threads). The result must therefore always be used: q.Where(...) alone
/// does nothing, write q = q.Where(...). The operators carry [Pure] so an ignored result is flagged
/// by code analysis (CA1806).
/// </summary>
public interface IQueryOfNodes<TNode, TInclude> : IQueryCollection<ResultSet<TNode>> {
    int Count();
    TProperty Sum<TProperty>(Expression<Func<TNode, TProperty>> property);
    Task<int> CountAsync();
    [Pure] QueryOfFacets<TNode, TInclude> Facets();
    //QueryOfFacets<TNode, TInclude> Facets(params string[] propertyNames);
    /// <summary>
    /// Switches to a pivot: group the matching nodes into rows and columns by property values and
    /// compute measures (count, sum, average, min, max, distinct count) per cell, like a spreadsheet
    /// pivot table. The nodes themselves are not returned; see <see cref="QueryOfPivot{T, TInclude}"/>.
    /// </summary>
    [Pure] QueryOfPivot<TNode, TInclude> Pivot();
    /// <summary>
    /// Groups the matching nodes by a key built from their properties - x => x.Brand, x => new { x.Brand, x.Created.Year },
    /// x => Bucket.Ranges(x.Price, 5) - the way LINQ and EF Core do. Execute() gives the groups with their counts;
    /// Select(g => new { g.Key, Total = g.Sum(x => x.Price) }) shapes them with aggregates. Aggregate-only: no nodes are read.
    /// </summary>
    [Pure] QueryOfGroups<TNode, TInclude, TKey> GroupBy<TKey>(Expression<Func<TNode, TKey>> keySelector);
    /// <summary>Groups by properties chosen at runtime (GroupKey.Values / Interval / Ranges); the key is an object?[] with one entry per level.</summary>
    [Pure] QueryOfGroups<TNode, TInclude, object?[]> GroupBy(params GroupKey[] keys);
    [Pure] IQueryOfNodes<TNode, TInclude> Page(int pageIndex0based, int pageSize);
    [Pure] IQueryOfNodes<TNode, TInclude> Take(int maxCount);
    [Pure] IQueryOfNodes<TNode, TInclude> Skip(int offset);
    [Pure] IQueryCollection<ResultSet<Guid>> SelectId();

    [Pure] IQueryOfNodes<TNode, TInclude> Where(Expression<Func<TNode, bool>> boolExpression);
    [Pure] IQueryOfNodes<TNode, TInclude> Where(string lambdaCodeAsString);
    [Pure] IQueryOfNodes<TNode, TInclude> Where(Guid id);
    [Pure] IQueryOfNodes<TNode, TInclude> Where(int id);
    [Pure] IQueryOfNodes<TNode, TInclude> Where(IEnumerable<Guid> ids);
    [Pure] IQueryOfNodes<TNode, TInclude> Where(IEnumerable<int> ids);
    [Pure] IQueryOfNodes<TNode, TInclude> WhereSearch(string text, double? semanticRatio = null, float? minimumVectorSimilarity = null, bool? orSearch = null, int? maxWordsEvaluated = null);
    [Pure] IQueryOfNodes<TNode, TInclude> WhereTypes(IEnumerable<Guid> nodeTypes, bool includeDescendants = true);
    [Pure] IQueryOfNodes<TNode, TInclude> WhereTypes(IEnumerable<Type> nodeTypes, bool includeDescendants = true);
    [Pure] IQueryOfNodes<TNode, TInclude> WhereRelates<TProperty>(Expression<Func<TNode, TProperty>> relationProperty, Guid nodeId);
    [Pure] IQueryOfNodes<TNode, TInclude> WhereRelates<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty>> relationProperty, Guid nodeId);
    [Pure] IQueryOfNodes<TNode, TInclude> WhereNotRelates<TProperty>(Expression<Func<TNode, TProperty>> relationProperty, Guid nodeId);
    [Pure] IQueryOfNodes<TNode, TInclude> WhereRelatesAny<TProperty>(Expression<Func<TNode, TProperty>> relationProperty, IEnumerable<Guid> nodeId);
    [Pure] IQueryOfNodes<TNode, TInclude> WhereIn<TProperty>(Expression<Func<TNode, TProperty>> property, IEnumerable<TProperty> values);
    [Pure] IQueryOfNodes<TNode, TInclude> WhereCulture(string? cultureCode);
    [Pure] IQueryOfNodes<TNode, TInclude> WhereCulture(Guid cultureId);
    [Pure] IQueryOfNodes<TNode, TInclude> WhereHidden(bool include);
    [Pure] IQueryOfNodes<TNode, TInclude> WhereCultureFallback(bool include);


    [Pure] QueryOfSearch<TNode, TInclude> Search(string text, double? semanticRatio = null, float? minimumVectorSimilarity = null, bool? orSearch = null, int? maxWordsEvaluated = null, int? maxHitsEvaluated = null);

    /// <summary>
    /// Expands the current result set over a relation with a breadth first traversal and returns the reached nodes,
    /// typed as the related node type. The current result set is the seed set at level 0; the result contains every node
    /// whose minimum distance from any seed is within [minLevel, maxLevel]. Cycle safe. The result is a regular node
    /// query: Where, OrderBy, Count, Page, Include and Facets can be chained after it.
    /// </summary>
    [Pure] IQueryOfNodes<TProperty, TProperty> Traverse<TProperty>(Expression<Func<TNode, TProperty>> relationProperty, int maxLevel, int minLevel = 1, GraphDirection direction = GraphDirection.Default, int? maxVisited = null);
    [Pure] IQueryOfNodes<TProperty, TProperty> Traverse<TProperty>(Expression<Func<TNode, IEnumerable<TProperty>>> relationProperty, int maxLevel, int minLevel = 1, GraphDirection direction = GraphDirection.Default, int? maxVisited = null);
    [Pure] IQueryOfNodes<TProperty, TProperty> Traverse<TProperty>(Expression<Func<TNode, TProperty[]?>> relationProperty, int maxLevel, int minLevel = 1, GraphDirection direction = GraphDirection.Default, int? maxVisited = null);
    [Pure] IQueryOfNodes<TProperty, TProperty> Traverse<TProperty>(Expression<Func<TNode, ICollection<TProperty>>> relationProperty, int maxLevel, int minLevel = 1, GraphDirection direction = GraphDirection.Default, int? maxVisited = null);

    /// <summary>
    /// Finds one shortest path (breadth first, unweighted) between two nodes over a relation.
    /// Returns a path result with the node ids and materialized nodes in order, from -> to inclusive.
    /// </summary>
    [Pure] QueryOfShortestPath<TNode, TInclude> ShortestPath<TProperty>(Expression<Func<TNode, TProperty>> relationProperty, Guid fromNodeId, Guid toNodeId, int maxLevel = 1000, GraphDirection direction = GraphDirection.Default, int? maxVisited = null);
    [Pure] IQueryOfNodes<TNode, TInclude> OrderBy(Expression<Func<TNode, object>> expression, bool descending = false);
    [Pure] IQueryOfNodes<TNode, TInclude> OrderByDescending(Expression<Func<TNode, object>> expression);

    //IIncludeQueryOfNodes<TNode, TProperty> Include<TProperty>(Guid relationPropertyId, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> Include<TProperty>(Expression<Func<TNode, TProperty>> relationProperty, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> Include<TProperty>(Expression<Func<TNode, TProperty[]?>> relationProperty, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> Include<TProperty>(Expression<Func<TNode, IEnumerable<TProperty>>> relationProperty, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> Include<TProperty>(Expression<Func<TNode, ICollection<TProperty>>> relationProperty, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> Preload<TProperty>(Expression<Func<TNode, IRelationProperty<TProperty>>> relationProperty, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> Preload<TProperty>(Expression<Func<TNode, IReference<TProperty>>> referenceProperty, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> Preload<TProperty>(Expression<Func<TNode, IReferences<TProperty>>> referencesProperty, int? top = null);

    // subclass
    //IIncludeQueryOfNodes<TNode, TProperty> Include<TSubClass, TProperty>(Guid relationPropertyId, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> Include<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty>> relationProperty, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> Include<TSubClass, TProperty>(Expression<Func<TSubClass, IEnumerable<TProperty>>> relationProperty, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> Include<TSubClass, TProperty>(Expression<Func<TSubClass, ICollection<TProperty>>> relationProperty, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> Include<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty[]>> relationProperty, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> Preload<TSubClass, TProperty>(Expression<Func<TSubClass, IRelationProperty<TProperty>>> relationProperty, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> Preload<TSubClass, TProperty>(Expression<Func<TSubClass, IReference<TProperty>>> relationProperty, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> Preload<TSubClass, TProperty>(Expression<Func<TSubClass, IReferences<TProperty>>> referencesProperty, int? top = null);

    // Include / Preload with a filter on the related nodes: only related nodes passing the filter are loaded.
    // The filter never affects the main result set, and it is applied before top.
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> Include<TProperty>(Expression<Func<TNode, TProperty>> relationProperty, Expression<Func<TProperty, bool>> filter, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> Include<TProperty>(Expression<Func<TNode, TProperty[]?>> relationProperty, Expression<Func<TProperty, bool>> filter, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> Include<TProperty>(Expression<Func<TNode, IEnumerable<TProperty>>> relationProperty, Expression<Func<TProperty, bool>> filter, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> Include<TProperty>(Expression<Func<TNode, ICollection<TProperty>>> relationProperty, Expression<Func<TProperty, bool>> filter, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> Preload<TProperty>(Expression<Func<TNode, IRelationProperty<TProperty>>> relationProperty, Expression<Func<TProperty, bool>> filter, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> Preload<TProperty>(Expression<Func<TNode, IReference<TProperty>>> referenceProperty, Expression<Func<TProperty, bool>> filter, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> Preload<TProperty>(Expression<Func<TNode, IReferences<TProperty>>> referencesProperty, Expression<Func<TProperty, bool>> filter, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> Include<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty>> relationProperty, Expression<Func<TProperty, bool>> filter, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> Include<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty[]>> relationProperty, Expression<Func<TProperty, bool>> filter, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> Include<TSubClass, TProperty>(Expression<Func<TSubClass, IEnumerable<TProperty>>> relationProperty, Expression<Func<TProperty, bool>> filter, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> Include<TSubClass, TProperty>(Expression<Func<TSubClass, ICollection<TProperty>>> relationProperty, Expression<Func<TProperty, bool>> filter, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> Preload<TSubClass, TProperty>(Expression<Func<TSubClass, IRelationProperty<TProperty>>> relationProperty, Expression<Func<TProperty, bool>> filter, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> Preload<TSubClass, TProperty>(Expression<Func<TSubClass, IReference<TProperty>>> relationProperty, Expression<Func<TProperty, bool>> filter, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> Preload<TSubClass, TProperty>(Expression<Func<TSubClass, IReferences<TProperty>>> referencesProperty, Expression<Func<TProperty, bool>> filter, int? top = null);

    //long Update<TProperty>(Expression<Func<TNode, TProperty>> property, object newValue);

}
public interface IIncludeQueryOfNodes<TNode, TInclude> : IQueryOfNodes<TNode, TInclude> {

    [Pure] IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TProperty>(Expression<Func<TInclude, TProperty>> relationProperty, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TProperty>(Expression<Func<TInclude, TProperty[]>> relationProperty, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TProperty>(Expression<Func<TInclude, IEnumerable<TProperty>>> relationProperty, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TProperty>(Expression<Func<TInclude, ICollection<TProperty>>> relationProperty, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> ThenPreload<TProperty>(Expression<Func<TInclude, IRelationProperty<TProperty>>> relationProperty, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> ThenPreload<TProperty>(Expression<Func<TInclude, IReference<TProperty>>> relationProperty, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> ThenPreload<TProperty>(Expression<Func<TInclude, IReferences<TProperty>>> referencesProperty, int? top = null);

    [Pure] IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty>> relationProperty, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty[]>> relationProperty, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TSubClass, TProperty>(Expression<Func<TSubClass, IEnumerable<TProperty>>> relationProperty, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TSubClass, TProperty>(Expression<Func<TSubClass, ICollection<TProperty>>> relationProperty, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> ThenPreload<TSubClass, TProperty>(Expression<Func<TSubClass, IRelationProperty<TProperty>>> relationProperty, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> ThenPreload<TSubClass, TProperty>(Expression<Func<TSubClass, IReference<TProperty>>> relationProperty, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> ThenPreload<TSubClass, TProperty>(Expression<Func<TSubClass, IReferences<TProperty>>> referencesProperty, int? top = null);

    // ThenInclude / ThenPreload with a filter on the related nodes of the deeper level:
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TProperty>(Expression<Func<TInclude, TProperty>> relationProperty, Expression<Func<TProperty, bool>> filter, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TProperty>(Expression<Func<TInclude, TProperty[]>> relationProperty, Expression<Func<TProperty, bool>> filter, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TProperty>(Expression<Func<TInclude, IEnumerable<TProperty>>> relationProperty, Expression<Func<TProperty, bool>> filter, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TProperty>(Expression<Func<TInclude, ICollection<TProperty>>> relationProperty, Expression<Func<TProperty, bool>> filter, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> ThenPreload<TProperty>(Expression<Func<TInclude, IRelationProperty<TProperty>>> relationProperty, Expression<Func<TProperty, bool>> filter, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> ThenPreload<TProperty>(Expression<Func<TInclude, IReference<TProperty>>> relationProperty, Expression<Func<TProperty, bool>> filter, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> ThenPreload<TProperty>(Expression<Func<TInclude, IReferences<TProperty>>> referencesProperty, Expression<Func<TProperty, bool>> filter, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty>> relationProperty, Expression<Func<TProperty, bool>> filter, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty[]>> relationProperty, Expression<Func<TProperty, bool>> filter, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TSubClass, TProperty>(Expression<Func<TSubClass, IEnumerable<TProperty>>> relationProperty, Expression<Func<TProperty, bool>> filter, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TSubClass, TProperty>(Expression<Func<TSubClass, ICollection<TProperty>>> relationProperty, Expression<Func<TProperty, bool>> filter, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> ThenPreload<TSubClass, TProperty>(Expression<Func<TSubClass, IRelationProperty<TProperty>>> relationProperty, Expression<Func<TProperty, bool>> filter, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> ThenPreload<TSubClass, TProperty>(Expression<Func<TSubClass, IReference<TProperty>>> relationProperty, Expression<Func<TProperty, bool>> filter, int? top = null);
    [Pure] IIncludeQueryOfNodes<TNode, TProperty> ThenPreload<TSubClass, TProperty>(Expression<Func<TSubClass, IReferences<TProperty>>> referencesProperty, Expression<Func<TProperty, bool>> filter, int? top = null);

}

public static class IQueryExecutableExtensions {
    public static bool TryGet<TNode, TInclude>(this IQueryOfNodes<TNode, TInclude> query, [MaybeNullWhen(false)] out TNode item) {
        var items = query.Execute();
        var enumerator = items.GetEnumerator();
        if (enumerator.MoveNext()) {
            item = enumerator.Current;
            if (enumerator.MoveNext()) throw new Exception("More than one item found. ");
            return true;
        }
        item = default;
        return false;
    }
    public static TNode? FirstOrDefault<TNode, TInclude>(this IQueryOfNodes<TNode, TInclude> query) => query.Take(1).Execute().FirstOrDefault();
    public static async Task<TNode?> FirstOrDefaultAsync<TNode, TInclude>(this IQueryOfNodes<TNode, TInclude> query) {
        var res = await query.Take(1).ExecuteAsync();
        return res.FirstOrDefault();
    }

    public static TNode First<TNode, TInclude>(this IQueryOfNodes<TNode, TInclude> query) => query.Take(1).Execute().First();
    public static async Task<TNode> FirstAsync<TNode, TInclude>(this IQueryOfNodes<TNode, TInclude> query) {
        var res = await query.Take(1).ExecuteAsync();
        return res.First();
    }

    // Take(2), not Take(1): two rows are enough for Single to detect a duplicate, while Take(1)
    // would hide every duplicate and make Single behave like First
    public static TNode Single<TNode, TInclude>(this IQueryOfNodes<TNode, TInclude> query) => query.Take(2).Execute().Single();

    public static T? FirstOrDefault<T>(this QueryOfObjects<T> query) => query.Execute().FirstOrDefault();
    public static T First<T>(this QueryOfObjects<T> query) => query.Execute().First();
    public static T Single<T>(this QueryOfObjects<T> query) => query.Execute().Single();
}
