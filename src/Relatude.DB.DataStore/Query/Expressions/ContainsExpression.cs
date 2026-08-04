using System.Collections;
using System.Globalization;
using Relatude.DB.Common;

namespace Relatude.DB.Query.Expressions;

/// <summary>
/// x.Tags.Contains(value): true when the array property holds an element equal to value.
/// Index-accelerated for indexed array properties that keep a per element index - string, enum and
/// guid arrays - (see the native expression in the local store); evaluates per row otherwise, which
/// is also the only path for float and byte arrays as those have no per element index.
/// </summary>
public class ContainsExpression : IExpression {
    public readonly VariableReferenceExpression SourceObject;
    public readonly string PropertyName;
    public readonly object? Value;
    readonly PropertyReferenceExpression _propertyRef;
    public ContainsExpression(string propertyPath, object? value) {
        var parts = propertyPath.Split('.');
        if (parts.Length != 2) throw new NotSupportedException("Contains is only supported directly on an array property of the queried node, like x.Tags.Contains(\"red\"). Got: " + propertyPath + ".Contains(..). ");
        SourceObject = new VariableReferenceExpression(parts[0]);
        PropertyName = parts[1];
        _propertyRef = new PropertyReferenceExpression(SourceObject, PropertyName);
        Value = value;
    }
    public object Evaluate(IVariables vars) { // row evaluation (non-indexed fallback)
        var v = _propertyRef.Evaluate(vars);
        if (v == null) return false; // no value, so it contains nothing
        if (v is string || v is not IEnumerable elements)
            throw new NotSupportedException("Contains is only supported on array properties. " + SourceObject + "." + PropertyName + " is of type " + v.GetType().Name + ". ");
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
