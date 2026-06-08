using Relatude.DB.Query;
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
    public static int UpdatePropertyWhere<T>(this NodeStore db, Expression<Func<T, object>> property, object value, Expression<Func<T, bool>> where, bool flushToDisk = false) {
        return db.UpdatePropertyWhere<T, object>(property, value, where, flushToDisk);
    }
    public static int UpdatePropertyWhere<T, V>(this NodeStore db, Expression<Func<T, V>> property, V value, Expression<Func<T, bool>> where, bool flushToDisk = false) {
        var matches = db.Query<T>(where).SelectId().Execute().ToArray();
        var transaction = db.CreateTransaction();
        foreach (var item in matches) {
            transaction.UpdateProperty(item, property, value);
        }
        db.Execute(transaction, flushToDisk);
        return matches.Length;
    }
    public static int DeleteWhere<T>(this NodeStore db, Expression<Func<T, bool>> where, bool flushToDisk = false) {
        var matches = db.Query<T>(where).SelectId().Execute().ToArray();
        var transaction = db.CreateTransaction();
        foreach (var item in matches) {
            transaction.Delete(item);
        }
        db.Execute(transaction, flushToDisk);
        return matches.Length;
    }
    public static V SingleProperty<T, V>(this NodeStore db, Expression<Func<T, bool>> where, Expression<Func<T, V>> property) {
        var matches = db.Query<T>().Where(where).Take(2).Select(property).Execute().ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("Sequence contains more than one element");
        if (matches.Length == 0) throw new InvalidOperationException("Sequence contains no elements");
        return matches[0];
    }
    public static V? SinglePropertyOrDefault<T, V>(this NodeStore db, Expression<Func<T, bool>> where, Expression<Func<T, V>> property) {
        var matches = db.Query<T>().Where(where).Take(2).Select(property).Execute().ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("Sequence contains more than one element");
        if (matches.Length == 0) return default;
        return matches[0];
    }
    public static bool Any<T>(this NodeStore db) {
        var matches = db.Query<T>().Take(1).SelectId().Execute().ToArray();
        return matches.Length > 0;
    }
    public static bool Any<T>(this NodeStore db, Expression<Func<T, bool>> where) {
        var matches = db.Query<T>().Where(where).Take(1).SelectId().Execute().ToArray();
        return matches.Length > 0;
    }
    public static bool One<T>(this NodeStore db) {
        var matches = db.Query<T>().Take(2).SelectId().Execute().ToArray();
        return matches.Length == 1;
    }
    public static bool One<T>(this NodeStore db, Expression<Func<T, bool>> where) {
        var matches = db.Query<T>().Where(where).Take(2).SelectId().Execute().ToArray();
        return matches.Length == 1;
    }
    public static bool None<T>(this NodeStore db) {
        var matches = db.Query<T>().Take(1).SelectId().Execute().ToArray();
        return matches.Length == 0;
    }
    public static bool None<T>(this NodeStore db, Expression<Func<T, bool>> where) {
        var matches = db.Query<T>().Where(where).Take(1).SelectId().Execute().ToArray();
        return matches.Length == 0;
    }
    public static int Count<T>(this NodeStore db) {
        return db.Query<T>().Count();
    }
    public static int Count<T>(this NodeStore db, Expression<Func<T, bool>> where) {
        return db.Query<T>().Where(where).Count();
    }
}
