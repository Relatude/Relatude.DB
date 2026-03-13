using Relatude.DB.Common;
using Relatude.DB.Query.Expressions;

namespace Relatude.DB.Query.Methods;

public class WhereHiddenMethod(IExpression input, string includeHidden) : IExpression {
    public object Evaluate(IVariables vars) {
        vars.Context = vars.Context.Hidden(includeHidden?.ToLower() == "true");
        return input.Evaluate(vars)!;
    }
    public override string ToString() => input + ".WhereHidden(" + includeHidden.ToStringLiteral() + ")";
}
