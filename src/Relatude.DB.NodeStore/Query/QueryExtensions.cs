
namespace Relatude.DB.Query;
public static class QueryExtensions {
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
    public static bool InRange(this DateTimeOffset obj, DateTimeOffset from, DateTimeOffset to) {
        throw new NotImplementedException("Only for building query expressions. ");
    }
    /// <summary>
    /// True when this string property matches the search, using the property's own word index and,
    /// when it has one, its semantic index. This is the search of WhereSearch narrowed to one
    /// property instead of the node's combined text index, and unlike WhereSearch it is a predicate,
    /// so it composes with OR and NOT.
    /// The property must be declared with IndexedByWords or IndexedBySemantic: a search cannot be
    /// evaluated row by row, so there is no fallback for a property without one of those indexes.
    /// For a plain substring test use Contains instead, which needs no word index.
    /// </summary>
    public static bool MatchesSearch(this string? text, string search) {
        throw new NotImplementedException("Only for building query expressions. ");
    }
    /// <summary>
    /// <see cref="MatchesSearch(string, string)"/> with the search tuned, taking the same settings in
    /// the same order as WhereSearch. Pass null for any setting to keep its default.
    /// These are separate parameters rather than optional ones because C# forbids omitted optional
    /// arguments inside an expression tree, which is what a query predicate is compiled to.
    /// </summary>
    public static bool MatchesSearch(this string? text, string search, double? semanticRatio,
        float? minimumVectorSimilarity, bool? orSearch, int? maxWordsEvaluated) {
        throw new NotImplementedException("Only for building query expressions. ");
    }
}
