using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Relatude.DB.Nodes;
using Relatude.DB.Transactions;
namespace Relatude.DB.Query;
public interface IQueryOfNodes<TNode, TInclude> : IQueryCollection<ResultSet<TNode>> {
    int Count();
    TProperty Sum<TProperty>(Expression<Func<TNode, TProperty>> property);
    Task<int> CountAsync();
    QueryOfFacets<TNode, TInclude> Facets();
    //QueryOfFacets<TNode, TInclude> Facets(params string[] propertyNames);
    IQueryOfNodes<TNode, TInclude> Page(int pageIndex0based, int pageSize);
    IQueryOfNodes<TNode, TInclude> Take(int maxCount);
    IQueryOfNodes<TNode, TInclude> Skip(int offset);

    IQueryCollection<ResultSet<Guid>> SelectId();

    IQueryOfNodes<TNode, TInclude> Where(Expression<Func<TNode, bool>> boolExpression);
    IQueryOfNodes<TNode, TInclude> Where(string lambdaCodeAsString);
    IQueryOfNodes<TNode, TInclude> Where(Guid id);
    IQueryOfNodes<TNode, TInclude> Where(int id);
    IQueryOfNodes<TNode, TInclude> Where(IEnumerable<Guid> ids);
    IQueryOfNodes<TNode, TInclude> Where(IEnumerable<int> ids);
    IQueryOfNodes<TNode, TInclude> WhereSearch(string text, double? semanticRatio = null, float? minimumVectorSimilarity = null, bool? orSearch = null, int? maxWordsEvaluated = null);
    IQueryOfNodes<TNode, TInclude> WhereTypes(IEnumerable<Guid> nodeTypes, bool includeDescendants = true);
    IQueryOfNodes<TNode, TInclude> WhereTypes(IEnumerable<Type> nodeTypes, bool includeDescendants = true);
    IQueryOfNodes<TNode, TInclude> WhereRelates<TProperty>(Expression<Func<TNode, TProperty>> relationProperty, Guid nodeId);
    IQueryOfNodes<TNode, TInclude> WhereRelates<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty>> relationProperty, Guid nodeId);
    IQueryOfNodes<TNode, TInclude> WhereNotRelates<TProperty>(Expression<Func<TNode, TProperty>> relationProperty, Guid nodeId);
    IQueryOfNodes<TNode, TInclude> WhereRelatesAny<TProperty>(Expression<Func<TNode, TProperty>> relationProperty, IEnumerable<Guid> nodeId);
    IQueryOfNodes<TNode, TInclude> WhereIn<TProperty>(Expression<Func<TNode, TProperty>> property, IEnumerable<TProperty> values);


    QueryOfSearch<TNode, TInclude> Search(string text, double? semanticRatio = null, float? minimumVectorSimilarity = null, bool? orSearch = null, int? maxWordsEvaluated = null, int? maxHitsEvaluated = null);
    IQueryOfNodes<TNode, TInclude> OrderBy(Expression<Func<TNode, object>> expression, bool descending = false);
    IQueryOfNodes<TNode, TInclude> OrderByDescending(Expression<Func<TNode, object>> expression);

    //IIncludeQueryOfNodes<TNode, TProperty> Include<TProperty>(Guid relationPropertyId, int? top = null);
    IIncludeQueryOfNodes<TNode, TProperty> Include<TProperty>(Expression<Func<TNode, TProperty>> relationProperty, int? top = null);
    IIncludeQueryOfNodes<TNode, TProperty> Include<TProperty>(Expression<Func<TNode, TProperty[]?>> relationProperty, int? top = null);
    IIncludeQueryOfNodes<TNode, TProperty> Include<TProperty>(Expression<Func<TNode, IEnumerable<TProperty>>> relationProperty, int? top = null);
    IIncludeQueryOfNodes<TNode, TProperty> Include<TProperty>(Expression<Func<TNode, ICollection<TProperty>>> relationProperty, int? top = null);

    // subclass
    //IIncludeQueryOfNodes<TNode, TProperty> Include<TSubClass, TProperty>(Guid relationPropertyId, int? top = null);
    IIncludeQueryOfNodes<TNode, TProperty> Include<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty>> relationProperty, int? top = null);
    IIncludeQueryOfNodes<TNode, TProperty> Include<TSubClass, TProperty>(Expression<Func<TSubClass, IEnumerable<TProperty>>> relationProperty, int? top = null);
    IIncludeQueryOfNodes<TNode, TProperty> Include<TSubClass, TProperty>(Expression<Func<TSubClass, ICollection<TProperty>>> relationProperty, int? top = null);
    IIncludeQueryOfNodes<TNode, TProperty> Include<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty[]>> relationProperty, int? top = null);

    //long Update<TProperty>(Expression<Func<TNode, TProperty>> property, object newValue);

}
public interface IIncludeQueryOfNodes<TNode, TInclude> : IQueryOfNodes<TNode, TInclude> {
    IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TProperty>(Expression<Func<TInclude, TProperty>> relationProperty, int? top = null);
    IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TProperty>(Expression<Func<TInclude, TProperty[]>> relationProperty, int? top = null);
    IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TProperty>(Expression<Func<TInclude, IEnumerable<TProperty>>> relationProperty, int? top = null);
    IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TProperty>(Expression<Func<TInclude, ICollection<TProperty>>> relationProperty, int? top = null);
    IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty>> relationProperty, int? top = null);
    IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TSubClass, TProperty>(Expression<Func<TSubClass, TProperty[]>> relationProperty, int? top = null);
    IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TSubClass, TProperty>(Expression<Func<TSubClass, IEnumerable<TProperty>>> relationProperty, int? top = null);
    IIncludeQueryOfNodes<TNode, TProperty> ThenInclude<TSubClass, TProperty>(Expression<Func<TSubClass, ICollection<TProperty>>> relationProperty, int? top = null);
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

    public static TNode Single<TNode, TInclude>(this IQueryOfNodes<TNode, TInclude> query) => query.Take(1).Execute().Single();

    public static T? FirstOrDefault<T>(this QueryOfObjects<T> query) => query.Execute().FirstOrDefault();
    public static T First<T>(this QueryOfObjects<T> query) => query.Execute().First();
    public static T Single<T>(this QueryOfObjects<T> query) => query.Execute().Single();
}