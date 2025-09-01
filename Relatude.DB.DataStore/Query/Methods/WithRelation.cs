using Relatude.DB.Datamodels;
using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;
namespace Relatude.DB.Query.Methods;
public class RelatesAnyMethod : IExpression {
    readonly IExpression _input;
    readonly Datamodel _dm;
    readonly Guid _propertyGuid;
    readonly Guid[] _toNodeGuids;
    public RelatesAnyMethod(IExpression input, Datamodel dm, string propertyIdString, string toNodeIdParam) {
        _input = input;
        _dm = dm;
        _propertyGuid = _dm.GetPropertyGuid(propertyIdString); // name, guid or id
        toNodeIdParam = toNodeIdParam.Trim();
        if (toNodeIdParam.StartsWith('[') && toNodeIdParam.EndsWith(']')) {
            toNodeIdParam = toNodeIdParam.Substring(1, toNodeIdParam.Length - 2);
            var toNodeIds = toNodeIdParam.Split(',', StringSplitOptions.RemoveEmptyEntries);
            _toNodeGuids = new Guid[toNodeIds.Length];
            for (int i = 0; i < toNodeIds.Length; i++) {
                _toNodeGuids[i] = Guid.Parse(toNodeIds[i]);
            }
        } else {
            _toNodeGuids = [Guid.Parse(toNodeIdParam)];
        }
    }
    public object Evaluate(IVariables vars) {
        var result = _input.Evaluate(vars);
        if (result is IStoreNodeDataCollection nodesColl) {
            return nodesColl.RelatesAny(_propertyGuid, _toNodeGuids);
        } else {
            throw new Exception("Unable to link Relates to previous expression.");
        }
    }
    public override string ToString() => throw new NotImplementedException();
}
public class RelatesMethod : IExpression {
    readonly IExpression _input;
    readonly Datamodel _dm;
    readonly Guid _propertyGuid;
    readonly Guid _toNodeGuid;
    public RelatesMethod(IExpression input, Datamodel dm, string propertyIdString, string toNodeIdParam) {
        _input = input;
        _dm = dm;
        _propertyGuid = _dm.GetPropertyGuid(propertyIdString); // name, guid or id
        if (toNodeIdParam.StartsWith('[') && toNodeIdParam.EndsWith(']')) {
            throw new Exception("Relates method can only accept one toNodeId");
        } else {
            _toNodeGuid = Guid.Parse(toNodeIdParam);
        }
    }
    public object Evaluate(IVariables vars) {
        var result = _input.Evaluate(vars);
        if (result is IStoreNodeDataCollection nodesColl) {
            return nodesColl.Relates(_propertyGuid, _toNodeGuid);
        } else {
            throw new Exception("Unable to link Relates to previous expression.");
        }
    }
    public override string ToString() => throw new NotImplementedException();
}
public class RelatesNotMethod : IExpression {
    readonly IExpression _input;
    readonly Datamodel _dm;
    readonly Guid _propertyGuid;
    readonly Guid _toNodeGuid;
    public RelatesNotMethod(IExpression input, Datamodel dm, string propertyIdString, string toNodeIdParam) {
        _input = input;
        _dm = dm;
        _propertyGuid = _dm.GetPropertyGuid(propertyIdString); // name, guid or id
        if (toNodeIdParam.StartsWith('[') && toNodeIdParam.EndsWith(']')) {
            toNodeIdParam = toNodeIdParam.Substring(1, toNodeIdParam.Length - 2);
            var toNodeIds = toNodeIdParam.Split(',');
            if (toNodeIds.Length > 1) throw new Exception("RelatesNot method can only accept one toNodeId");
            _toNodeGuid = Guid.Parse(toNodeIds.First());
        } else {
            _toNodeGuid = Guid.Parse(toNodeIdParam);
        }
    }
    public object Evaluate(IVariables vars) {
        var result = _input.Evaluate(vars);
        if (result is IStoreNodeDataCollection nodesColl) {
            return nodesColl.RelatesNot(_propertyGuid, _toNodeGuid);
        } else {
            throw new Exception("Unable to link Relates to previous expression.");
        }
    }
    public override string ToString() => throw new NotImplementedException();
}
