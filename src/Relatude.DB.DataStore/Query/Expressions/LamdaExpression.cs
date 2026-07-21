namespace Relatude.DB.Query.Expressions;
public class LambdaExpression : IExpression {
    public readonly IExpression Body;
    public readonly string[] Parameters;
    public LambdaExpression(string[] paramaters, IExpression lambdaFunc) {
        Parameters = paramaters;
        if (lambdaFunc is OperatorExpression op)
            lambdaFunc = op.Simplify();
        Body = lambdaFunc;
    }
    public object? Evaluate(IVariables vars) => Body.Evaluate(vars);
    public override string ToString() {
        if (Parameters == null || Parameters.Length == 0) {
            return "() => " + Body;
        } else if (Parameters.Length == 1) {
            return Parameters[0] + " => " + Body;
        } else {
            return "(" + string.Join(", ", Parameters) + ") => " + Body;
        }
    }
}
