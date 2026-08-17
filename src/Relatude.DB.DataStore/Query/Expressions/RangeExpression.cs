
namespace Relatude.DB.Query.Expressions;
public class RangeExpression : IExpression {
    public readonly VariableReferenceExpression SourceObject;
    // the bounds keep the token's typed value (DateTime, DateTimeOffset, long ticks, or a literal
    // string): stringifying a typed value and parsing it back is culture dependent and loses precision
    public readonly object From;
    public readonly object To;
    public readonly string PropertyName;
    readonly PropertyReferenceExpression _propertyRef;
    public RangeExpression(string property, object from, object to) {
        var parts = property.Split('.');
        SourceObject = new VariableReferenceExpression(parts[0]);
        PropertyName = parts.Skip(1).Single();
        From = from;
        To = to;
        _propertyRef = new PropertyReferenceExpression(SourceObject, PropertyName);
    }
    public object Evaluate(IVariables vars) { // row evaluation (non-indexed fallback), inclusive both ends
        var v = _propertyRef.Evaluate(vars);
        if (v is DateTime dt) {
            var from = Datamodels.Properties.DateTimePropertyModel.ForceValueType(From, out _);
            var to = Datamodels.Properties.DateTimePropertyModel.ForceValueType(To, out _);
            return dt >= from && dt <= to;
        }
        if (v is DateTimeOffset dto) {
            var from = Datamodels.Properties.DateTimeOffsetPropertyModel.ForceValueType(From, out _);
            var to = Datamodels.Properties.DateTimeOffsetPropertyModel.ForceValueType(To, out _);
            return dto >= from && dto <= to;
        }
        throw new NotSupportedException("InRange is only supported on DateTime and DateTimeOffset properties. ");
    }
    public override string ToString() => SourceObject + "." + PropertyName + ".InRange(" + From + ", " + To + ")";
}
