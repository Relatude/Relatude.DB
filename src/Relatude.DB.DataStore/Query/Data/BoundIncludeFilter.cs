using Relatude.DB.Datamodels;
using Relatude.DB.Query.Expressions;

namespace Relatude.DB.Query.Data;
/// <summary>
/// An include filter lambda bound to the variable scope of the executing query.
/// Binding at evaluation time is required because filter lambdas may reference query parameters.
/// </summary>
public sealed class BoundIncludeFilter : IIncludeFilter {
    public BoundIncludeFilter(LambdaExpression lambda, IVariables vars) {
        Lambda = lambda;
        Vars = vars;
    }
    public LambdaExpression Lambda { get; }
    public IVariables Vars { get; }
}
