using WAF.Common;
using WAF.Query.Data;
using WAF.Serialization;
using System.Linq.Expressions;
using System.Text;
using System.Globalization;
using System.Diagnostics;
using WAF.Nodes;

namespace WAF.Query;
public sealed class QueryOfFacets<T, TInclude> : IQueryExecutable<ResultSetFacets<T>> {
    readonly QueryOfNodes<T, TInclude> _query;
    readonly Dictionary<Guid, Facets> _given;
    readonly Dictionary<Guid, Facets> _set;
    int _pageIndex = 0;
    int _pageSize = 0;
    internal QueryOfFacets(QueryOfNodes<T, TInclude> query) {
        _query = query;
        _given = new();
        _set = new();
    }
    Guid getPropertyId<TChild>(Expression<Func<TChild, object>> expression) where TChild : T {
        return _query.Store.Mapper.GetProperty<TChild>(expression).Id;
    }
    Guid getPropertyId<TChild>(string propertyName) where TChild : T {
        return _query.Store.Mapper.GetProperty<TChild>(propertyName).Id;
    }
    public QueryOfFacets<T, TInclude> AddFacet(Expression<Func<T, object>> expression) => AddFacet(getPropertyId(expression));
    public QueryOfFacets<T, TInclude> AddFacet<TChild>(Expression<Func<TChild, object>> expression) where TChild : T => AddFacet(getPropertyId(expression));
    public QueryOfFacets<T, TInclude> AddFacet(string propertyName) => AddFacet(getPropertyId<T>(propertyName));
    public QueryOfFacets<T, TInclude> AddFacet<TChild>(string propertyName) where TChild : T => AddFacet(getPropertyId<TChild>(propertyName));
    public QueryOfFacets<T, TInclude> AddFacet(Guid propertyId) {
        var property = _query.Store.Datastore.Datamodel.Properties[propertyId];
        if (!_given.ContainsKey(propertyId)) _given.Add(propertyId, new(property));
        return this;
    }

    public QueryOfFacets<T, TInclude> AddValueFacet(Expression<Func<T, object>> expression) => AddValueFacet(getPropertyId(expression));
    public QueryOfFacets<T, TInclude> AddValueFacet<TChild>(Expression<Func<TChild, object>> expression) where TChild : T => AddValueFacet(getPropertyId(expression));
    public QueryOfFacets<T, TInclude> AddValueFacet(string propertyName) => AddValueFacet(getPropertyId<T>(propertyName));
    public QueryOfFacets<T, TInclude> AddValueFacet<TChild>(string propertyName) where TChild : T => AddValueFacet(getPropertyId<TChild>(propertyName));
    public QueryOfFacets<T, TInclude> AddValueFacet(Guid propertyId) {
        var property = _query.Store.Datastore.Datamodel.Properties[propertyId];
        if (!_given.ContainsKey(propertyId)) _given.Add(propertyId, new(property, false));
        return this;
    }

    public QueryOfFacets<T, TInclude> AddSingleRangeFacet(Expression<Func<T, object>> expression) => AddRangeFacet(getPropertyId(expression), 1, 0);
    public QueryOfFacets<T, TInclude> AddSingleRangeFacet(string propertyName) => AddRangeFacet(propertyName, 1, 0);
    public QueryOfFacets<T, TInclude> AddSingleRangeFacet(Guid propertyId) => AddRangeFacet(propertyId, 1, 0);

    public QueryOfFacets<T, TInclude> AddRangeFacet(Expression<Func<T, object>> expression, object from, object to) => AddRangeFacet(getPropertyId(expression), from, to);
    public QueryOfFacets<T, TInclude> AddRangeFacet(Expression<Func<T, object>> expression) => AddRangeFacet(getPropertyId(expression));
    public QueryOfFacets<T, TInclude> AddRangeFacet<TChild>(Expression<Func<TChild, object>> expression) where TChild : T => AddRangeFacet(getPropertyId(expression));
    public QueryOfFacets<T, TInclude> AddRangeFacet(string propertyName) => AddRangeFacet(getPropertyId<T>(propertyName));
    public QueryOfFacets<T, TInclude> AddRangeFacet(string propertyName, object from, object to) => AddRangeFacet(getPropertyId<T>(propertyName), from, to);
    public QueryOfFacets<T, TInclude> AddRangeFacet<TChild>(string propertyName) where TChild : T => AddRangeFacet(getPropertyId<TChild>(propertyName));
    public QueryOfFacets<T, TInclude> AddRangeFacet(Guid propertyId, object from, object to) {
        AddRangeFacet(propertyId);
        _given[propertyId].Values.Add(new FacetValue(from, to, null));
        return this;
    }
    public QueryOfFacets<T, TInclude> AddRangeFacet(Guid propertyId) {
        var property = _query.Store.Datastore.Datamodel.Properties[propertyId];
        if (!_given.ContainsKey(propertyId)) _given.Add(propertyId, new(property, true));
        return this;
    }

    void setFacetValue(Guid propId, bool rangeValue, FacetValue facetValue) {
        if (_set.TryGetValue(propId, out var facets)) {
            facets.AddValue(facetValue);
        } else {
            var facetValues = new List<FacetValue>() { facetValue };
            var property = _query.Store.Datastore.Datamodel.Properties[propId];
            facets = new Facets(property, rangeValue, facetValues);
            _set.Add(propId, facets);
        }
    }

    public QueryOfFacets<T, TInclude> SetFacetValue(Expression<Func<T, object>> expression, object value, string? displayName = null) => SetFacetValue(getPropertyId(expression), value, displayName);
    public QueryOfFacets<T, TInclude> SetFacetValue<TChild>(Expression<Func<TChild, object>> expression, object value, string? displayName = null) where TChild : T => SetFacetValue(getPropertyId(expression), value, displayName);
    public QueryOfFacets<T, TInclude> SetFacetValue(string propertyName, object value, string? displayName = null) => SetFacetValue(getPropertyId<T>(propertyName), value, displayName);
    public QueryOfFacets<T, TInclude> SetFacetValue<TChild>(string propertyName, object value, string? displayName = null) where TChild : T => SetFacetValue(getPropertyId<TChild>(propertyName), value, displayName);
    public QueryOfFacets<T, TInclude> SetFacetValue(Guid propertyId, object value, string? displayName = null) {
        var fv = new FacetValue(value);
        if (displayName != null) fv.DisplayName = displayName;
        setFacetValue(propertyId, false, fv);
        return this;
    }

    public QueryOfFacets<T, TInclude> SetFacetRangeValue(Expression<Func<T, object>> expression, object from, object to, string? displayName = null) => SetFacetRangeValue(getPropertyId(expression), from, to, displayName);
    public QueryOfFacets<T, TInclude> SetFacetRangeValue<TChild>(Expression<Func<TChild, object>> expression, object from, object to, string? displayName = null) where TChild : T => SetFacetRangeValue(getPropertyId(expression), from, to, displayName);
    public QueryOfFacets<T, TInclude> SetFacetRangeValue(string propertyName, object from, object to, string? displayName = null) => SetFacetRangeValue(getPropertyId<T>(propertyName), from, to, displayName);
    public QueryOfFacets<T, TInclude> SetFacetRangeValue<TChild>(string propertyName, object from, object to, string? displayName = null) where TChild : T => SetFacetRangeValue(getPropertyId<TChild>(propertyName), from, to, displayName);
    public QueryOfFacets<T, TInclude> SetFacetRangeValue(Guid propertyId, object from, object to, string? displayName = null) {
        setFacetValue(propertyId, true, new FacetValue(from, to, displayName));
        return this;
    }

    public QueryOfFacets<T, TInclude> Page(int pageIndex, int pageSize) {
        if (pageIndex < 0) throw new ArgumentOutOfRangeException(nameof(pageIndex), "Page index must be greater than or equal to 0.");
        if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be greater than 0.");
        _pageIndex = pageIndex;
        _pageSize = pageSize;
        return this;
    }

    public override string ToString() {
        var dm = _query.Store.Datastore.Datamodel;
        var sb = new StringBuilder();
        sb.Append(_query.ToString());
        sb.Append("." + nameof(_query.Facets) + "()");
        foreach (var facet in _given.Values) {
            if (facet.IsRangeFacet == null) {
                sb.Append("." + nameof(this.AddFacet) + "(");
                sb.Append(pn(facet.PropertyId) + ")");
            } else if (facet.IsRangeFacet.Value) {
                sb.Append("." + nameof(this.AddRangeFacet) + "(" + pn(facet.PropertyId) + ")");
            } else {
                sb.Append("." + nameof(this.AddValueFacet) + "(" + pn(facet.PropertyId) + ")");
            }
            foreach (var facetValue in facet.Values) {
                if (facetValue.Value2 == null) {
                    sb.Append("." + nameof(this.AddValueFacet) + "(" + pn(facet.PropertyId) + ", ");
                    sb.Append(QueryOfFacets<T, TInclude>.valueToString(facetValue.Value));
                } else {
                    sb.Append("." + nameof(this.AddRangeFacet) + "(" + pn(facet.PropertyId) + ", ");
                    sb.Append(QueryOfFacets<T, TInclude>.valueToString(facetValue.Value));
                    sb.Append(", ");
                    sb.Append(QueryOfFacets<T, TInclude>.valueToString(facetValue.Value2));
                }
                sb.Append(')');
            }
        }
        foreach (var facet in _set.Values) {
            foreach (var facetValue in facet.Values) {
                if (facetValue.Value2 == null) {
                    sb.Append("." + nameof(this.SetFacetValue) + "(" + pn(facet.PropertyId) + ", ");
                    sb.Append(QueryOfFacets<T, TInclude>.valueToString(facetValue.Value));
                } else {
                    sb.Append("." + nameof(this.SetFacetRangeValue) + "(" + pn(facet.PropertyId) + ", ");
                    sb.Append(QueryOfFacets<T, TInclude>.valueToString(facetValue.Value));
                    sb.Append(", ");
                    sb.Append(QueryOfFacets<T, TInclude>.valueToString(facetValue.Value2));
                }
                sb.Append(')');
            }
        }
        if (_pageIndex > 0 || _pageSize > 0) {
            sb.Append("." + nameof(this.Page) + "(");
            sb.Append(_pageIndex);
            sb.Append(", ");
            sb.Append(_pageSize);
            sb.Append(')');
        }

        return sb.ToString();
    }
    string pn(Guid propertyId) {
        var dm = _query.Store.Datastore.Datamodel;
        return "\"" + propertyId + "|" + dm.Properties[propertyId].CodeName + "\"";
    }
    static string valueToString(object v) {
        if (v is int i) {
            return i.ToString();
        } else if (v is double d) {
            return d.ToString(CultureInfo.InvariantCulture);
        } else {
            return (v + "").ToStringLiteral();
        }
    }
    public async Task<ResultSetFacets<T>> ExecuteAsync() => await _query.Store.Datastore.QueryAsync(ToString(), _query._q._parameters).ContinueWith(t => _execute(t.Result));
    public ResultSetFacets<T> Execute(string query) => _execute(_query.Store.Datastore.Query(query, _query._q._parameters));
    public ResultSetFacets<T> Execute() => _execute(_query.Store.Datastore.QueryAsync(ToString(), _query._q._parameters).Result);
    ResultSetFacets<T> _execute(object? data) {
        if (data is not FacetQueryResultData facets)
            throw new NotSupportedException("Only results of type " + nameof(FacetQueryResultData) + " is supported. Type provided: " + data?.GetType().FullName);
        var values = toEnumerable<T>(facets.Result);
        return new(values, facets);
    }
    IEnumerable<TCast> toEnumerable<TCast>(object data) {
        if (data is IStoreNodeDataCollection coll) {
            foreach (var nodeData in coll.NodeValues) {
                yield return _query.Store.Mapper.CreateObjectFromNodeData<TCast>(nodeData);
            }
        }
    }
}
