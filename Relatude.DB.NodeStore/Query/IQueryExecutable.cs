using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Xml.Linq;
using Relatude.DB.Nodes;

namespace Relatude.DB.Query;
public interface IQueryExecutable<T> {
    T Execute();
    Task<T> ExecuteAsync();
}
public static class IQueryExecutableExtentions {
    public static List<T> ToList<T>(this IQueryExecutable<ResultSet<T>> query) {
        var result = query.Execute();
        var list = new List<T>(result.Count);
        foreach (var item in result) {
            list.Add(item);
        }
        return list;
    }
    public static async Task<T[]> ToAsyncList<T>(this IQueryExecutable<ResultSet<T>> query) {
        var result = await query.ExecuteAsync();
        var array = new T[result.Count];
        return array;
    }
}