using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Query.Data;

namespace Relatude.DB.Query.Expressions;
public class AnonymousObjectExpression : IExpression {
    public readonly KeyValuePair<string, PropertyType>[] Properties;
    public readonly List<IExpression> ValueExpressions;
    public AnonymousObjectExpression(KeyValuePair<string, PropertyType>[] properties, List<IExpression> valueExpressions) {
        Properties = properties;
        ValueExpressions = valueExpressions;
    }
    public object Evaluate(IVariables vars) {
        var values = new object[ValueExpressions.Count];
        for (var n = 0; n < ValueExpressions.Count; n++) values[n] = ValueExpressions[n].Evaluate(vars);
        var result = new ObjectData(Properties, values);
        return result;
    }
}
