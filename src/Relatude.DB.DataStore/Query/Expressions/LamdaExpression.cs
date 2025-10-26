namespace Relatude.DB.Query.Expressions;
public class LambdaExpression : IExpression {
    public IExpression Body;
    public List<string> Parameters { get; }
    public LambdaExpression(List<string> paramaters, IExpression lambdaFunc) {
        Parameters = paramaters;
        if (lambdaFunc is OperatorExpression op)
            lambdaFunc = op.Simplify();
        Body = lambdaFunc;
    }
    public object? Evaluate(IVariables vars) => Body.Evaluate(vars);
    public override string ToString() {
        if (Parameters == null || Parameters.Count == 0) {
            return "() => " + Body;
        } else if (Parameters.Count == 1) {
            return Parameters[0] + " => " + Body;
        } else {
            return "(" + string.Join(", ", Parameters) + ") => " + Body;
        }
    }
}
