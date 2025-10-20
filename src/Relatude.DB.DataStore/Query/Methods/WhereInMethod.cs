using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;
namespace Relatude.DB.Query.Methods;
public class WhereInMethod : IExpression {
    readonly IExpression _input;
    readonly Guid _propertyGuid;
    readonly object[] _values;
    public WhereInMethod(IExpression input, Guid propertyId, object[] values) {
        _input = input;
        _propertyGuid = propertyId;
        _values = values;
    }
    public object Evaluate(IVariables vars) {
        var result = _input.Evaluate(vars);
        if (result is not IStoreNodeDataCollection nodesColl) throw new Exception("Unable to link Relates to previous expression.");
        return nodesColl.WhereIn(_propertyGuid, _values);
    }
    public override string ToString() => throw new NotImplementedException();
}

