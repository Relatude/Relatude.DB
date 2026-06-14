namespace Relatude.DB.Query;

public interface IQueryExecutable<T> {
    T Execute();
    Task<T> ExecuteAsync();
    object? EvaluateForJson();
    Task<object?> EvaluateForJsonAsync();
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
    public static T[] ToArray<T>(this IQueryExecutable<ResultSet<T>> query) {
        var result = query.Execute();
        var array = new T[result.Count];
        var index = 0;
        foreach (var item in result) {
            array[index++] = item;
        }
        return array;
    }
    public static async Task<List<T>> ToListAsync<T>(this IQueryExecutable<ResultSet<T>> query) {
        var result = await query.ExecuteAsync();
        var list = new List<T>(result.Count);
        foreach (var item in result) {
            list.Add(item);
        }
        return list;
    }
    public static async Task<T[]> ToArrayAsync<T>(this IQueryExecutable<ResultSet<T>> query) {
        var result = await query.ExecuteAsync();
        var array = new T[result.Count];
        var index = 0;
        foreach (var item in result) {
            array[index++] = item;
        }
        return array;
    }
}