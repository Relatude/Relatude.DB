using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;
namespace Relatude.DB.Query.Methods;
public class WhereMethod(IExpression input, LambdaExpression lamda) : IExpression {
    public object Evaluate(IVariables vars) {
        var evaluatedInput = (ICollectionData)input.Evaluate(vars)!;
        if (evaluatedInput is IStoreNodeDataCollection sd) {
            // If the input is a store node data collection, we can try to evaluate expression using sets, and indexes:
            var scope = vars.CreateScope();
            var inputParamaterName = lamda.Parameters.Single();
            scope.DeclarerAndSet(inputParamaterName, sd);
            evaluatedInput = sd.FilterAsMuchAsPossibleUsingIndexes(scope, lamda.Body, out var remainingFilter);
            if (remainingFilter == null) return evaluatedInput; // if no remaining filter, we are done
            lamda.Body = remainingFilter; // if there is a remaining filter, we will evaluate the remaining filter using the evaluated input
        }
        // evaluating each row to a bool value ( where expression must return a bool value )
        // the mask is basically an array of bools, where each bool represents if the row should be included in the result or not
        var maskAsObjects = Helper.EvaluateLambdaOnCollection(vars, evaluatedInput, lamda);
        var mask = new bool[maskAsObjects.TotalCount];
        var i = 0;
        foreach (var o in maskAsObjects.Values) mask[i++] = (bool)o;
        // once the mask is created, we can filter the input collection using the mask
        return evaluatedInput.Filter(mask);
    }
    public override string ToString() => input + ".Where(" + lamda + ")";
}
