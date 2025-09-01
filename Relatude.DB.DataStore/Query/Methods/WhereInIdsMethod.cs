using Relatude.DB.Datamodels;
using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;
namespace Relatude.DB.Query.Methods;
public class WhereInIdsMethod : IExpression {
    readonly IExpression _input;
    readonly List<Guid> _guidValues = [];
    public WhereInIdsMethod(IExpression input, string valueString) {
        _input = input;
        valueString = valueString.Trim();
        if (valueString.StartsWith('[') && valueString.EndsWith(']')) valueString = valueString[1..^1];
        foreach (var value in stringValues(valueString)) {
            if (Guid.TryParse(value, out var v)) {
                _guidValues.Add(v);
            } else {
                throw new Exception($"Unable to parse value '{value}' for Id property ");
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
        return nodesColl.WhereInIds(_guidValues!);
    }
    public override string ToString() => throw new NotImplementedException();
}

