using Relatude.DB.Common;

namespace Relatude.DB.Query.Expressions;

/// <summary>
/// x.Name.StartsWith("Hello"): true when the string property starts with the given prefix.
/// Matching is ordinal, like string equality in query expressions. Index-accelerated for indexed
/// string properties (the prefixed values form one range in the index, see the native expression in
/// the local store); evaluates per row otherwise.
/// </summary>
public class StartsWithExpression : IExpression {
    public readonly VariableReferenceExpression SourceObject;
    public readonly string PropertyName;
    public readonly string Prefix;
    readonly PropertyReferenceExpression _propertyRef;
    public StartsWithExpression(string propertyPath, object? prefix) {
        (SourceObject, PropertyName) = StringMethodExpressionUtil.SplitPath(propertyPath, "StartsWith");
        _propertyRef = new PropertyReferenceExpression(SourceObject, PropertyName);
        Prefix = StringMethodExpressionUtil.ToSearchString(prefix, "StartsWith");
    }
    public object Evaluate(IVariables vars) { // row evaluation (non-indexed fallback)
        var v = _propertyRef.Evaluate(vars);
        if (v == null) return false;
        return StringMethodExpressionUtil.ToPropertyString(v, SourceObject, PropertyName, "StartsWith")
            .StartsWith(Prefix, StringComparison.Ordinal);
    }
    public override string ToString() => SourceObject + "." + PropertyName + ".StartsWith(" + Prefix.ToStringLiteral() + ")";
}
