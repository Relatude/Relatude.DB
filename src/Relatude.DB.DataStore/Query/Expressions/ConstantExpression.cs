namespace Relatude.DB.Query.Expressions;
public class ConstantExpression(object? value) : IExpression {
    public object? Value => value;
    public object? Evaluate(IVariables vars) => Value;
}
