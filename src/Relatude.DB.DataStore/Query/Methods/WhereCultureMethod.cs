using Relatude.DB.Common;
using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;

namespace Relatude.DB.Query.Methods;

public class WhereCultureMethod(IExpression input, string cultureCodeOrId) : IExpression {
    public object Evaluate(IVariables vars) {
        if (Guid.TryParse(cultureCodeOrId, out var cultureId)) {
            vars.Context = vars.Context.Culture(cultureId);
        } else {
            vars.Context = vars.Context.Culture(cultureCodeOrId);
        }
        return input.Evaluate(vars)!;
    }
    public override string ToString() => input + ".WhereCulture(" + cultureCodeOrId.ToStringLiteral() + ")";
}
