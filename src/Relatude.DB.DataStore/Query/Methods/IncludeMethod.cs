using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;

namespace Relatude.DB.Query.Methods;
public class IncludeMethod : IExpression {
    readonly IExpression _input;
    readonly LambdaExpression? _filter;
    readonly IncludeBranch? _filterTarget; // the leaf of the parsed path; an optional filter applies to it
    public IncludeBranch Branch { get; }
    public IncludeMethod(IExpression input, Datamodel dm, string relationPropertyBranch, LambdaExpression? filter = null) {
        _input = input;
        Branch = IncludeBranch.ParseOnePath(relationPropertyBranch);
        _filter = filter;
        if (filter != null) {
            var leaf = Branch;
            while (leaf.HasChildren()) leaf = leaf.Children.Single(); // a parsed path is a linear chain
            _filterTarget = leaf;
        }
    }
    public object? Evaluate(IVariables vars) {
        // filter lambdas may reference query parameters, so the filter is bound to the scope per evaluation:
        if (_filterTarget != null) _filterTarget.Filter = new BoundIncludeFilter(_filter!, vars);
        var result = _input.Evaluate(vars);
        if (result is IIncludeBranches nodesColl) nodesColl.IncludeBranch(Branch);
        return result;
    }
    public override string ToString() => throw new NotImplementedException();
}
