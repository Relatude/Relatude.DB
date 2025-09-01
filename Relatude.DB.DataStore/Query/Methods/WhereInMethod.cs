using Relatude.DB.Datamodels;
using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;
namespace Relatude.DB.Query.Methods;
public class WhereInMethod : IExpression {
    readonly IExpression _input;
    readonly Datamodel _dm;
    readonly Guid _propertyGuid;
    readonly List<object> _values = [];
    public WhereInMethod(IExpression input, Datamodel dm, string propertyIdString, string valueString) {
        _input = input;
        _dm = dm;
        valueString = valueString.Trim();
        if (valueString.StartsWith('[') && valueString.EndsWith(']')) valueString = valueString[1..^1];
        _propertyGuid = _dm.GetPropertyGuid(propertyIdString); // name, guid or id
        var property = _dm.Properties[_propertyGuid];
        foreach (var value in stringValues(valueString)) {
            if (property.TryParse(value, out var v)) {
                _values.Add(v);
            } else {
                throw new Exception($"Unable to parse value '{value}' for property '{property.CodeName}'");
            }
        }
    }
    IEnumerable<string> stringValues(string s) {
        // parse values, and remove quotes thay may surround them. using yield return
        var i = 0;
        while (i < s.Length) {
            var start = i;
            while (i < s.Length && s[i] != ',') i++;
            var value = s[start..i].Trim();
            if (value.StartsWith('\'') && value.EndsWith('\'')) value = value[1..^1];
            if (value.StartsWith('\"') && value.EndsWith('\"')) value = value[1..^1];
            yield return value;
            i++;
        }
    }
    public object Evaluate(IVariables vars) {
        var result = _input.Evaluate(vars);
        if (result is not IStoreNodeDataCollection nodesColl) throw new Exception("Unable to link Relates to previous expression.");
        return nodesColl.WhereIn(_propertyGuid, _values!);
    }
    public override string ToString() => throw new NotImplementedException();
}

