using System.Globalization;
namespace Relatude.DB.Query.Expressions;

/// <summary>
/// Element matching shared by <see cref="ContainsExpression"/>'s row evaluation and the index
/// accelerated Contains filters in the local store, so both paths agree on what
/// "the array holds this value" means.
/// The value of a Contains expression does not always arrive as the element type: enums come boxed
/// while enum arrays are indexed by their underlying int, guids may arrive as strings, and number
/// literals in a query string arrive as strings (the parser types them lazily).
/// </summary>
public static class ArrayElementMatch {
    /// <summary>
    /// Coerces the value of a Contains expression to the element type of an array property.
    /// False when no conversion applies; callers treat that as "matches nothing" rather than as an
    /// error, the same way an unparsable facet selection matches nothing.
    /// </summary>
    public static bool TryCoerce<TElement>(object? value, out TElement element) where TElement : notnull {
        element = default!;
        if (value == null) return false;
        if (value is TElement direct) { element = direct; return true; }
        if (value is Enum e) { // enum arrays are indexed by the underlying integer
            try { value = Convert.ToInt64(e, CultureInfo.InvariantCulture); } catch (OverflowException) { return false; }
        }
        var t = typeof(TElement);
        if (t == typeof(Guid)) {
            if (value is string gs && Guid.TryParse(gs, out var guid)) { element = (TElement)(object)guid; return true; }
            return false;
        }
        if (t == typeof(string)) { // same rule as scalar comparison: when one side is a string, compare as strings
            element = (TElement)(object)(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
            return true;
        }
        if (value is not IConvertible) return false;
        try {
            var converted = Convert.ChangeType(value, t, CultureInfo.InvariantCulture);
            // reject lossy conversions to whole numbers: 2.5 must not match the element 2
            if (isIntegral(t) && Convert.ToDecimal(converted, CultureInfo.InvariantCulture) != Convert.ToDecimal(value, CultureInfo.InvariantCulture)) return false;
            element = (TElement)converted;
            return true;
        } catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException) {
            return false;
        }
    }
    static bool isIntegral(Type t) => t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort)
        || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong);

    /// <summary>
    /// True when one element of an array property equals the value of a Contains expression.
    /// The comparison is done on the element's own type, using the same coercion the index path
    /// applies to the value, so an indexed and a non indexed property answer identically.
    /// </summary>
    public static bool Matches(object? element, object? value) => element switch {
        null => value == null,
        string s => TryCoerce<string>(value, out var v) && string.Equals(s, v, StringComparison.Ordinal),
        Guid g => TryCoerce<Guid>(value, out var v) && g == v,
        int i => TryCoerce<int>(value, out var v) && i == v,
        long l => TryCoerce<long>(value, out var v) && l == v,
        float f => TryCoerce<float>(value, out var v) && f == v,
        double d => TryCoerce<double>(value, out var v) && d == v,
        decimal m => TryCoerce<decimal>(value, out var v) && m == v,
        byte b => TryCoerce<byte>(value, out var v) && b == v,
        bool bo => TryCoerce<bool>(value, out var v) && bo == v,
        _ => element.Equals(value),
    };
}
