using System.Linq.Expressions;

namespace Relatude.DB.Nodes;

public static class NodeStoreExtensions {
    public static T First<T>(this NodeStore db) {
        var matches = db.Query<T>().Take(1).Execute().ToArray();
        if (matches.Length == 0) throw new InvalidOperationException("Sequence contains no elements");
        return matches[0];
    }
    public static T? FirstOrDefault<T>(this NodeStore db) {
        var matches = db.Query<T>().Take(1).Execute().ToArray();
        if (matches.Length == 0) return default;
        return matches[0];
    }
    public static T Single<T>(this NodeStore db) {
        var matches = db.Query<T>().Take(2).Execute().ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("Sequence contains more than one element");
        if (matches.Length == 0) throw new InvalidOperationException("Sequence contains no elements");
        return matches[0];
    }
    public static T? SingleOrDefault<T>(this NodeStore db) {
        var matches = db.Query<T>().Take(2).Execute().ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("Sequence contains more than one element");
        if (matches.Length == 0) return default;
        return matches[0];
    }
    public static T First<T>(this NodeStore db, Expression<Func<T, bool>> boolExpression) {
        var matches = db.Query<T>(boolExpression).Take(1).Execute().ToArray();
        if (matches.Length == 0) throw new InvalidOperationException("Sequence contains no elements");
        return matches[0];
    }
    public static T? FirstOrDefault<T>(this NodeStore db, Expression<Func<T, bool>> boolExpression) {
        var matches = db.Query<T>(boolExpression).Take(1).Execute().ToArray();
        if (matches.Length == 0) return default;
        return matches[0];
    }
    public static T Single<T>(this NodeStore db, Expression<Func<T, bool>> boolExpression) {
        var matches = db.Query<T>(boolExpression).Take(2).Execute().ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("Sequence contains more than one element");
        if (matches.Length == 0) throw new InvalidOperationException("Sequence contains no elements");
        return matches[0];
    }
    public static T? SingleOrDefault<T>(this NodeStore db, Expression<Func<T, bool>> boolExpression) {
        var matches = db.Query<T>(boolExpression).Take(2).Execute().ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("Sequence contains more than one element");
        if (matches.Length == 0) return default;
        return matches[0];
    }

}
