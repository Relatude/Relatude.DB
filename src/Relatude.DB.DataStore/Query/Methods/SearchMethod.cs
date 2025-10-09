using Relatude.DB.Datamodels;
using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;
namespace Relatude.DB.Query.Methods;
public class SearchMethod : IExpression {
    readonly IExpression _input;
    readonly string _searchText;
    readonly double? _ratioSemantic = null;
    readonly float? _minimumVectorSimilarity = null;
    readonly bool? _orSearch = null;
    readonly int _maxHitsEvaluated;
    readonly int _maxWordVariations;
    int _pageIndex = 0;
    int _pageSize = 0;
    public SearchMethod(IExpression input, string searchText, double? ratioSemantic, float? minimumVectorSimilarity, bool? orSearch, int maxHitsEvaluated, int maxWordVariations) {
        _input = input;
        _ratioSemantic = ratioSemantic;
        _searchText = searchText;
        _pageIndex = 0;
        _pageSize = 100; // default page size
        _maxHitsEvaluated = maxHitsEvaluated;
        _maxWordVariations = maxWordVariations;
    }
    public object Evaluate(IVariables vars) {
        if (_input.Evaluate(vars) is not ISearchCollection searchCollection)
            throw new Exception("Search method can only be used on a collection that implements ISearchCollection");
        Guid searchProperty = NodeConstants.SystemTextIndexPropertyId;
        return searchCollection.Search(_searchText, searchProperty, _ratioSemantic, _minimumVectorSimilarity, _orSearch, _pageIndex, _pageSize, _maxHitsEvaluated, _maxWordVariations);
    }
    internal void SetPaging(int pageIndex, int pageSize) {
        _pageIndex = pageIndex;
        _pageSize = pageSize;
    }
}
