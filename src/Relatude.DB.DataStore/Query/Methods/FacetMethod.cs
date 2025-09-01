using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;

namespace Relatude.DB.Query.Methods;
public class FacetMethod : IExpression {
    readonly IExpression _input;
    readonly Dictionary<Guid, Facets> _given;
    readonly Dictionary<Guid, Facets> _selected;
    readonly Datamodel _dm;
    public FacetMethod(IExpression input, IEnumerable<string> propertyNameOrId, Datamodel dm) {
        _input = input;
        _given = new();
        _selected = new();
        _dm = dm;
        foreach (var idString in propertyNameOrId) {
            AddFacet(idString);
        }
    }
    public void AddFacet(string idString) {
        AddFacet(_dm.GetPropertyGuid(idString));
    }
    public void AddFacet(Guid propertyId) {
        if (!_given.ContainsKey(propertyId) && _dm.Properties.TryGetValue(propertyId, out var property)) {
            _given.Add(propertyId, new Facets(property));
        }
    }
    public void AddValueFacet(string idString) {
        AddValueFacet(_dm.GetPropertyGuid(idString));
    }
    public void AddValueFacet(string idString, object value) {
        AddValueFacet(_dm.GetPropertyGuid(idString), value);
    }
    public void AddValueFacet(Guid propertyId, object value) {
        AddValueFacet(propertyId);
        _given[propertyId].Values.Add(new FacetValue(value));
    }
    public void AddValueFacet(Guid propertyId) {
        if (!_given.ContainsKey(propertyId) && _dm.Properties.TryGetValue(propertyId, out var property)) {
            _given.Add(propertyId, new Facets(property, false));
        }
    }
    public void AddRangeFacet(string idString) {
        AddRangeFacet(_dm.GetPropertyGuid(idString));
    }
    public void AddRangeFacet(string idString, object from, object to) {
        AddRangeFacet(_dm.GetPropertyGuid(idString), from, to);
    }
    public void AddRangeFacet(Guid propertyId, object from, object to) {
        AddRangeFacet(propertyId);
        _given[propertyId].Values.Add(new FacetValue(from, to, null));
    }
    public void AddRangeFacet(Guid propertyId) {
        if (!_given.ContainsKey(propertyId) && _dm.Properties.TryGetValue(propertyId, out var property)) {
            _given.Add(propertyId, new Facets(property, true));
        }
    }
    void setFacetValue(Guid propId, bool rangeValue, FacetValue facetValue) {
        if (_selected.TryGetValue(propId, out var facets)) {
            facets.AddValue(facetValue);
        } else {
            var facetValues = new List<FacetValue>() { facetValue };
            var property = _dm.Properties[propId];
            facets = new Facets(property, rangeValue, facetValues);
            _selected.Add(propId, facets);
        }
    }
    public void SetFacetValue(Guid propertyId, object value) {
        var fv = new FacetValue(value);
        setFacetValue(propertyId, false, fv);
    }
    public void SetFacetValue(string propertyIdString, object value) {
        SetFacetValue(_dm.GetPropertyGuid(propertyIdString), value);
    }
    public void SetFacetRangeValue(Guid propertyId, object from, object to) {
        var fv = new FacetValue(from, to, null);
        setFacetValue(propertyId, false, fv);
    }
    public void SetFacetRangeValue(string propertyIdString, object from, object to) {
        SetFacetRangeValue(_dm.GetPropertyGuid(propertyIdString), from, to);
    }
    int _pageIndex = 0;
    int? _pageSize = 0;
    public void SetPaging(int pageIndex, int pageSize) {
        _pageIndex = pageIndex;
        _pageSize = pageSize;
    }
    public object Evaluate(IVariables vars) {
        var set = _input.Evaluate(vars);
        if (set is not IFacetSource facetSource) throw new Exception("Collection does not implement " + nameof(IFacetSource));
        var facets = facetSource.EvaluateFacetsAndFilter(_given, _selected, out var newSource, _pageIndex, _pageSize);
        Facets.SetSelected(facets, _selected);
        return new FacetQueryResultData(facets, facetSource.TotalCount, newSource, _dm);
    }
    override public string ToString() => _input + ".Facets(...)";
}
