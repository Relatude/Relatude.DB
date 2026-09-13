using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;
namespace Relatude.DB.Query.Methods;
public class SelectIdMethod(IExpression input) : IExpression {
    public virtual object Evaluate(IVariables vars) {
        // on a facet clause: the ids of its selection, with no bucket counted
        var collection = TerminalSource.Nodes(input, vars, "SelectId");
        var result = new ValueCollectionData();
        foreach (var guid in collection.NodeGuids) result.Add(guid); // optimization possible... ( avoid boxing etc.. )
        return result;
    }
    public override string ToString() => input + ".SelectId()";
}
