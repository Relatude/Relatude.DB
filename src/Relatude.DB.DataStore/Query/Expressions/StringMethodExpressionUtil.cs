using System.Globalization;

namespace Relatude.DB.Query.Expressions;

/// <summary>
/// Shared plumbing for the string methods usable in a query expression
/// (<see cref="StartsWithExpression"/> and the string form of <see cref="ContainsExpression"/>).
/// </summary>
static class StringMethodExpressionUtil {
    /// <summary>Splits "x.Name" into the lambda parameter and the property name.</summary>
    public static (VariableReferenceExpression source, string propertyName) SplitPath(string propertyPath, string method) {
        var parts = propertyPath.Split('.');
        if (parts.Length != 2) throw new NotSupportedException(method + " is only supported directly on a property of the queried node, like x.Name." + method + "(\"abc\"). Got: " + propertyPath + "." + method + "(..). ");
        return (new VariableReferenceExpression(parts[0]), parts[1]);
    }
    /// <summary>
    /// The argument as the string to search for. A char argument (x.Name.Contains('a')) and a number
    /// literal from a query string both arrive as something other than a string.
    /// </summary>
    public static string ToSearchString(object? value, string method) {
        if (value == null) throw new NotSupportedException(method + " does not accept null as the value to search for. ");
        if (value is string s) return s;
        return Convert.ToString(value, CultureInfo.InvariantCulture)
            ?? throw new NotSupportedException(method + " expects a string, got: " + value.GetType().Name + ". ");
    }
    /// <summary>The property value as the string to search in, during row evaluation.</summary>
    public static string ToPropertyString(object value, VariableReferenceExpression source, string propertyName, string method) {
        if (value is string s) return s;
        throw new NotSupportedException(method + " is only supported on string properties. " + source + "." + propertyName + " is of type " + value.GetType().Name + ". ");
    }
    /// <summary>
    /// Query expressions always compare ordinally, so an explicit StringComparison argument is only
    /// accepted when it asks for exactly that.
    /// </summary>
    public static void ValidateComparison(object? comparison, string method) {
        if (comparison == null) return;
        if (comparison is StringComparison sc && sc == StringComparison.Ordinal) return;
        if (comparison is int i && i == (int)StringComparison.Ordinal) return;
        if (comparison is string s && (s == nameof(StringComparison.Ordinal) || s == ((int)StringComparison.Ordinal).ToString())) return;
        throw new NotSupportedException(method + " with an explicit StringComparison of " + comparison + " is not supported. Query expressions always match ordinally, like string equality, so only StringComparison.Ordinal can be given. ");
    }
}
