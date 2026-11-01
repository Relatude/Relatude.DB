using Relatude.DB.Query.Data;
namespace Relatude.DB.Query.Expressions; 
public class PropertyReferenceExpression : IExpression {
    public readonly VariableReferenceExpression SourceObject;
    public readonly string PropertyName;
    public PropertyReferenceExpression(VariableReferenceExpression sourceObject, string propertyName) {
        SourceObject = sourceObject;
        PropertyName = propertyName;
    }
    public object? Evaluate(IVariables vars) {
        var input = SourceObject.Evaluate(vars);
        if (input is ObjectData inputObj) {
            return inputObj.GetValue(PropertyName);
        } else if (input is IStoreNodeDataCollection storeObject) {
            return storeObject;
        } else if (input is IStoreNodeData nodeObject) {
            var value = nodeObject.GetValue(PropertyName);
            // a password is never compared, sorted, grouped or selected: the one question is whether it is set
            if (value is Relatude.DB.Common.PasswordValue) throw new NotSupportedException(ToString() + " is a password, which a query can only ask whether it is set: " + ToString() + "." + PropertyIsSetExpression.MemberName + ". ");
            //if (value == null) throw new NullReferenceException($"Property {PropertyName} is null");
            return value!;
        } else {
            throw new NotImplementedException();
        }
    }
    public override string ToString() => SourceObject + "." + PropertyName;
}
