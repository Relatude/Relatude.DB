using Relatude.DB.Common;
using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;

namespace Relatude.DB.Query.Methods;

public class WhereCultureFallbackMethod(IExpression input, string includeFallbacks) : IExpression {
    public object Evaluate(IVariables vars) {
        vars.Context = vars.Context.CultureFallbacks(includeFallbacks?.ToLower() == "true");
        return input.Evaluate(vars)!;
    }
    public override string ToString() => input + ".WhereCultureFallback(" + includeFallbacks.ToStringLiteral() + ")";
}
