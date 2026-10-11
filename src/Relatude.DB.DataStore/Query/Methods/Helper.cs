using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;
namespace Relatude.DB.Query.Methods;
internal class Helper {
    public static ICollectionData EvaluateLambdaOnCollection(IVariables vars, ICollectionData coll, LambdaExpression lambda) {
        if (lambda.Body == null) return coll;
        var lambdaParamaterName = lambda.Parameters.Single();
        var rowVars = vars.CreateScope();
        rowVars.Declare(lambdaParamaterName);
        var results = new ObjectCollection();
        var cancel = vars.Context.CancellationToken; // a row filter or projection over a large set is where a query spends its time
        var row = 0;
        foreach (var o in coll.Values) {
            if ((row++ & 255) == 0) cancel.ThrowIfCancellationRequested();
            rowVars.Set(lambdaParamaterName, o);
            var r = lambda.Evaluate(rowVars);
            results.Add(r);
        }
        return results;
    }
}
