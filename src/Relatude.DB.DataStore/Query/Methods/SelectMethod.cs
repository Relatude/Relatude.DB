using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;
namespace Relatude.DB.Query.Methods;
public class SelectMethod(IExpression input, LambdaExpression lamda) : IExpression {
    public virtual object? Evaluate(IVariables vars) => Helper.EvaluateLambdaOnCollection(vars, (ICollectionData)input.Evaluate(vars)!, lamda);
    public override string ToString() => input + ".Select(" + lamda + ")";
}
