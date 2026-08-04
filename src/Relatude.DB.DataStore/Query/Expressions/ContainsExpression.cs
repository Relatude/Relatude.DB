using System.Collections;
using System.Globalization;
using Relatude.DB.Common;

namespace Relatude.DB.Query.Expressions;

/// <summary>
/// x.Tags.Contains(value), meaning what it means in C#, so the property decides which:
/// <list type="bullet">
/// <item>on an array property: true when the array holds an element equal to value. Index-accelerated
/// for indexed arrays that keep a per element index - string, enum and guid arrays - and evaluated
/// per row otherwise, which is also the only path for float and byte arrays.</item>
/// <item>on a string property: true when the string holds value as an ordinal substring. Answered
/// from the unique values of an indexed string property, and per row otherwise. A substring is not
/// a range, so this never narrows to a subrange of the index the way StartsWith does - prefer
/// <see cref="StartsWithExpression"/> or WhereSearch when either fits.</item>
/// </list>
/// The property type is only known once the query runs, so the distinction is made by the planner
/// and by <see cref="Evaluate"/>, not while parsing.
/// </summary>
public class ContainsExpression : IExpression {
    public readonly VariableReferenceExpression SourceObject;
    public readonly string PropertyName;
    public readonly object? Value;
    readonly PropertyReferenceExpression _propertyRef;
    public ContainsExpression(string propertyPath, object? value) {
        (SourceObject, PropertyName) = StringMethodExpressionUtil.SplitPath(propertyPath, "Contains");
        _propertyRef = new PropertyReferenceExpression(SourceObject, PropertyName);
        Value = value;
    }
    /// <summary>The value as the substring to search for, when the property turns out to be a string.</summary>
    public string SubstringValue => StringMethodExpressionUtil.ToSearchString(Value, "Contains");
    public object Evaluate(IVariables vars) { // row evaluation (non-indexed fallback)
        var v = _propertyRef.Evaluate(vars);
        if (v == null) return false; // no value, so it contains nothing
        if (v is string s) return s.Contains(SubstringValue, StringComparison.Ordinal);
        if (v is not IEnumerable elements)
            throw new NotSupportedException("Contains is only supported on string and array properties. " + SourceObject + "." + PropertyName + " is of type " + v.GetType().Name + ". ");
        foreach (var e in elements) if (ArrayElementMatch.Matches(e, Value)) return true;
        return false;
    }
    public override string ToString() => SourceObject + "." + PropertyName + ".Contains(" + ValueToString(Value) + ")";
    /// <summary>The value as it would be written in a query string, so ToString round-trips.</summary>
    public static string ValueToString(object? value) => value switch {
        null => "null",
        string s => s.ToStringLiteral(),
        Guid g => g.ToStringLiteral(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}
