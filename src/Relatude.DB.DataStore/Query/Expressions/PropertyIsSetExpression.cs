using Relatude.DB.Common;
using Relatude.DB.Query.Data;
namespace Relatude.DB.Query.Expressions;
/// <summary>
/// x.Password.IsSet: whether a node has a password, the one question a query may ask of a password property.
/// Answered by reading the nodes, as a password has no index.
/// </summary>
public class PropertyIsSetExpression : IExpression {
    public const string MemberName = nameof(PasswordValue.IsSet);
    public readonly VariableReferenceExpression SourceObject;
    public readonly string PropertyName;
    public PropertyIsSetExpression(VariableReferenceExpression sourceObject, string propertyName) {
        SourceObject = sourceObject;
        PropertyName = propertyName;
    }
    public object? Evaluate(IVariables vars) {
        var input = SourceObject.Evaluate(vars);
        if (input is not IStoreNodeData node) throw new NotSupportedException(ToString() + " can only be asked of a node. ");
        return node.GetValue(PropertyName) switch {
            PasswordValue password => password.IsSet,
            _ => throw new NotSupportedException(MemberName + " can only be asked of a password property, which " + SourceObject + "." + PropertyName + " is not. "),
        };
    }
    public override string ToString() => SourceObject + "." + PropertyName + "." + MemberName;
}
