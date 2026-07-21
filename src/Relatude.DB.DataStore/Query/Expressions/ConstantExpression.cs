namespace Relatude.DB.Query.Expressions;
public class ConstantExpression(object? value) : IExpression {
    public readonly object? Value = value;
    public object? Evaluate(IVariables vars) => Value;
}
