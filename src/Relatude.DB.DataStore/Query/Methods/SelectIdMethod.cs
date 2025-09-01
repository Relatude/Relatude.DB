using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;
namespace Relatude.DB.Query.Methods;
public class SelectIdMethod(IExpression input) : IExpression {
    public virtual object Evaluate(IVariables vars) {
        var nodesObject = input.Evaluate(vars);
        if (nodesObject is not IStoreNodeDataCollection collection) throw new Exception("SelectId() can only be used on a collection of nodes. ");
        var result = new ValueCollectionData();
        foreach (var guid in collection.NodeGuids) result.Add(guid); // optimization possible... ( avoid boxing etc.. )
        return result;
    }
    public override string ToString() => input + ".SelectId()";
}
