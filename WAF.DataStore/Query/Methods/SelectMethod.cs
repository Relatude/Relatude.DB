using WAF.Query.Data;
using WAF.Query.Expressions;
namespace WAF.Query.Methods;
public class SelectMethod(IExpression input, LambdaExpression lamda) : IExpression {
    public virtual object Evaluate(IVariables vars) => Helper.EvaluateLambdaOnCollection(vars, (ICollectionData)input.Evaluate(vars), lamda);
    public override string ToString() => input + ".Select(" + lamda + ")";
}
