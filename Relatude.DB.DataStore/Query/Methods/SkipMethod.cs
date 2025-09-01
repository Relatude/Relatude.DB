using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;
namespace Relatude.DB.Query.Methods;
public class SkipMethod : IExpression {
    readonly IExpression _input;
    public SkipMethod(IExpression input, int skip) {
        _input = input;
        _skip = skip;
    }
    readonly int _skip;
    public object Evaluate(IVariables vars) {
        var result = _input.Evaluate(vars);
        if (result is not ICollectionData collection) throw new Exception("Skip is not supported for " + _input + ". ");
        return collection.Skip(_skip);
    }
    public override string ToString() => _input + ".Skip(" + _skip + ")";
}
