using Relatude.DB.Datamodels;
using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;
namespace Relatude.DB.Query.Methods;
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
