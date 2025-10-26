using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;

namespace Relatude.DB.Query.Methods;
public class WhereTypesMethod(IExpression input, Guid[] types) : IExpression {
    public object Evaluate(IVariables vars) {
        var evaluatedInput = (IStoreNodeDataCollection)input.Evaluate(vars)!;
        return evaluatedInput.FilterByTypes(types);
    }
    public override string ToString() => input + ".Where(" + ")";
}
