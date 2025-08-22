using WAF.Datamodels;
using WAF.Query.Data;
using WAF.Query.Expressions;
namespace WAF.Query.Methods;
public class WhereSearchMethod : IExpression {
    readonly IExpression _input;
    readonly double? _ratioSemantic;
    readonly string _searchText;
    public WhereSearchMethod(IExpression input, string searchText, double? ratioSemantic) {
        _input = input;
        _ratioSemantic = ratioSemantic;
        _searchText = searchText;
    }
    public object Evaluate(IVariables vars) {
        if (_input.Evaluate(vars) is not ISearchCollection searchCollection)
            throw new Exception("Search method can only be used on a collection that implements ISearchCollection");
        Guid searchProperty = NodeConstants.SystemTextIndexPropertyId;
        return searchCollection.FilterBySearch(_searchText, searchProperty, _ratioSemantic);
    }
}
