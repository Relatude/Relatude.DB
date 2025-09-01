
namespace Relatude.DB.Query;
public static class ExpressionExtenstions {
    public static bool Is(this object obj, object value) {
        throw new NotImplementedException("Only for building query expressions. ");
    }
    public static bool Has(this object[] obj, object value) {
        throw new NotImplementedException("Only for building query expressions. ");
    }
    public static bool Has(this IEnumerable<object> obj, object value) {
        throw new NotImplementedException("Only for building query expressions. ");
    }
    public static bool Has(this ICollection<object> obj, object value) {
        throw new NotImplementedException("Only for building query expressions. ");
    }
    public static bool InRange(this DateTime obj, DateTime from, DateTime to) {
        throw new NotImplementedException("Only for building query expressions. ");
    }
}
