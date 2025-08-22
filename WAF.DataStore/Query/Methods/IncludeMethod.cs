using WAF.Datamodels;
using WAF.Datamodels.Properties;
using WAF.Query.Data;
using WAF.Query.Expressions;

namespace WAF.Query.Methods;
public class IncludeMethod : IExpression {
    readonly IExpression _input;
    public IncludeBranch Branch { get; }
    public IncludeMethod(IExpression input, Datamodel dm, string relationPropertyBranch) {
        _input = input;
        Branch = IncludeBranch.ParseOnePath(relationPropertyBranch);
    }
    public object Evaluate(IVariables vars) {
        var result = _input.Evaluate(vars);
        if (result is IIncludeBranches nodesColl) nodesColl.IncludeBranch(Branch);
        return result;
    }
    public override string ToString() => throw new NotImplementedException();
}
