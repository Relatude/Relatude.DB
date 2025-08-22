using WAF.Query.Data;
using WAF.Query.Expressions;
namespace WAF.Query.Methods;
public class TakeMethod : IExpression {
    readonly IExpression _input;
    public TakeMethod(IExpression input, int take) {
        _input = input;
        _take = take;
    }
    readonly int _take;
    public object Evaluate(IVariables vars) {
        var result = _input.Evaluate(vars);
        if (result is not ICollectionData collection) throw new Exception("Take is not supported for " + _input + ". ");
        return collection.Take(_take);
    }
    public override string ToString() => _input + ".Take(" + _take + ")";
}
