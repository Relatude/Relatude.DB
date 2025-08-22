using WAF.Query.Data;
using WAF.Query.Expressions;

namespace WAF.Query.Methods;
public class WhereTypesMethod(IExpression input, Guid[] types) : IExpression {
    public object Evaluate(IVariables vars) {
        var evaluatedInput = (IStoreNodeDataCollection)input.Evaluate(vars);
        return evaluatedInput.FilterByTypes(types);
    }
    public override string ToString() => input + ".Where(" + ")";
}
