using Relatude.DB.Datamodels;
using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;
namespace Relatude.DB.Query.Methods;
public class WhereSearchMethod : IExpression {
    readonly IExpression _input;
    readonly double? _ratioSemantic;
    readonly float? _minimumVectorSimilarity;
    readonly bool? _orSearch;
    readonly string _searchText;
    readonly int _maxHitsEvaluated;
    readonly int _maxWordVariations;
    public WhereSearchMethod(IExpression input, string searchText, double? ratioSemantic, float? minimumVectorSimilarity, bool? orSearch, int maxHitsEvaluated, int maxWordVariations) {
        _input = input;
        _searchText = searchText;
        _ratioSemantic = ratioSemantic;
        _minimumVectorSimilarity = minimumVectorSimilarity;
        _orSearch = orSearch;
        _maxHitsEvaluated = maxHitsEvaluated;
        _maxWordVariations = maxWordVariations;
    }
    public object Evaluate(IVariables vars) {
        if (_input.Evaluate(vars) is not ISearchCollection searchCollection)
            throw new Exception("Search method can only be used on a collection that implements ISearchCollection");
        Guid searchProperty = NodeConstants.SystemTextIndexPropertyId;
        return searchCollection.FilterBySearch(_searchText, searchProperty, _ratioSemantic, _minimumVectorSimilarity, _orSearch, _maxHitsEvaluated, _maxWordVariations);
    }
}
