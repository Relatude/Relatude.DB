using Relatude.DB.Datamodels;
using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;
namespace Relatude.DB.Query.Methods;
public class WhereInIdsMethod : IExpression {
    readonly IExpression _input;
    readonly Guid[] _guidValues;
    public WhereInIdsMethod(IExpression input, Guid[] ids) {
        _input = input;
        _guidValues = ids;
    }
    public object Evaluate(IVariables vars) {
        var result = _input.Evaluate(vars);
        if (result is not IStoreNodeDataCollection nodesColl) throw new Exception("Unable to link Relates to previous expression.");
        return nodesColl.WhereInIds(_guidValues!);
    }
    public override string ToString() => throw new NotImplementedException();
}

