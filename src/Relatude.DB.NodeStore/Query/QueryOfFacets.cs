using Relatude.DB.Common;
using Relatude.DB.Query.Data;
using Relatude.DB.Serialization;
using System.Linq.Expressions;
using System.Text;
using System.Globalization;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using Relatude.DB.Nodes;

namespace Relatude.DB.Query;

/// <summary>
/// A facet query. Immutable like the node query it wraps: every Add/Set operator returns a NEW
/// facet query, so the result must be used: fq = fq.SetFacetValue(...).
/// </summary>
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
    QueryOfFacets(QueryOfFacets<T, TInclude> source) { // copy for the immutable operators
        _query = source._query; // node queries are immutable, so sharing is safe
        _given = source._given.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
        _set = source._set.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
        _pageIndex = source._pageIndex;
        _pageSize = source._pageSize;
    }
    internal QueryOfNodes<T, TInclude> Query => _query;
    /// <summary>
    /// Pivots the nodes the facet selection leaves (SetFacetValue / SetFacetRangeValue /
    /// SetFacetMissingValue): the drill-down of a filter sidebar, summarized as a table. The facet
    /// buckets are not counted and the page is ignored - only the selection filters apply.
    /// </summary>
    [Pure]
    public QueryOfPivot<T, TInclude> Pivot() => new(this);
    Guid getPropertyId<TChild>(Expression<Func<TChild, object?>> expression) where TChild : T {
        return _query.Store.Mapper.GetProperty<TChild>(expression).Id;
    }
    Guid getPropertyId<TChild>(string propertyName) where TChild : T {
        return _query.Store.Mapper.GetProperty<TChild>(propertyName).Id;
    }
    [Pure]
    public QueryOfFacets<T, TInclude> AddFacet(Expression<Func<T, object?>> expression) => AddFacet(getPropertyId(expression));
    [Pure]
    public QueryOfFacets<T, TInclude> AddFacet<TChild>(Expression<Func<TChild, object?>> expression) where TChild : T => AddFacet(getPropertyId(expression));
    [Pure]
    public QueryOfFacets<T, TInclude> AddFacet(string propertyName) => AddFacet(getPropertyId<T>(propertyName));
    [Pure]
    public QueryOfFacets<T, TInclude> AddFacet<TChild>(string propertyName) where TChild : T => AddFacet(getPropertyId<TChild>(propertyName));
    [Pure]
    public QueryOfFacets<T, TInclude> AddFacet(Guid propertyId) {
        var c = new QueryOfFacets<T, TInclude>(this);
        c.addFacet(propertyId);
        return c;
    }
    void addFacet(Guid propertyId) {
        var property = _query.Store.Datastore.Datamodel.Properties[propertyId];
        if (!_given.ContainsKey(propertyId)) _given.Add(propertyId, new(property));
    }

    [Pure]
    public QueryOfFacets<T, TInclude> AddValueFacet(Expression<Func<T, object?>> expression) => AddValueFacet(getPropertyId(expression));
    [Pure]
    public QueryOfFacets<T, TInclude> AddValueFacet<TChild>(Expression<Func<TChild, object?>> expression) where TChild : T => AddValueFacet(getPropertyId(expression));
    [Pure]
    public QueryOfFacets<T, TInclude> AddValueFacet(string propertyName) => AddValueFacet(getPropertyId<T>(propertyName));
    [Pure]
    public QueryOfFacets<T, TInclude> AddValueFacet<TChild>(string propertyName) where TChild : T => AddValueFacet(getPropertyId<TChild>(propertyName));
    [Pure]
    public QueryOfFacets<T, TInclude> AddValueFacet(Guid propertyId) {
        var c = new QueryOfFacets<T, TInclude>(this);
        var property = c._query.Store.Datastore.Datamodel.Properties[propertyId];
        if (!c._given.ContainsKey(propertyId)) c._given.Add(propertyId, new(property, false));
        return c;
    }

    [Pure]
    public QueryOfFacets<T, TInclude> AddSingleRangeFacet(Expression<Func<T, object?>> expression) => AddSingleRangeFacet(getPropertyId(expression));
    [Pure]
    public QueryOfFacets<T, TInclude> AddSingleRangeFacet(string propertyName) => AddSingleRangeFacet(getPropertyId<T>(propertyName));
    [Pure]
    public QueryOfFacets<T, TInclude> AddSingleRangeFacet(Guid propertyId) {
        var c = AddRangeFacet(propertyId);
        c._given[propertyId].RangeCount = 1; // one auto-generated range spanning min..max
        return c;
    }

    [Pure]
    public QueryOfFacets<T, TInclude> AddRangeFacet(Expression<Func<T, object?>> expression, object from, object to) => AddRangeFacet(getPropertyId(expression), from, to);
    [Pure]
    public QueryOfFacets<T, TInclude> AddRangeFacet(Expression<Func<T, object?>> expression) => AddRangeFacet(getPropertyId(expression));
    [Pure]
    public QueryOfFacets<T, TInclude> AddRangeFacet<TChild>(Expression<Func<TChild, object?>> expression) where TChild : T => AddRangeFacet(getPropertyId(expression));
    [Pure]
    public QueryOfFacets<T, TInclude> AddRangeFacet(string propertyName) => AddRangeFacet(getPropertyId<T>(propertyName));
    [Pure]
    public QueryOfFacets<T, TInclude> AddRangeFacet(string propertyName, object from, object to) => AddRangeFacet(getPropertyId<T>(propertyName), from, to);
    [Pure]
    public QueryOfFacets<T, TInclude> AddRangeFacet<TChild>(string propertyName) where TChild : T => AddRangeFacet(getPropertyId<TChild>(propertyName));
    [Pure]
    public QueryOfFacets<T, TInclude> AddRangeFacet(Guid propertyId, object from, object to) {
        var c = AddRangeFacet(propertyId);
        c._given[propertyId].Values.Add(new FacetValue(from, to, null));
        return c;
    }
    [Pure]
    public QueryOfFacets<T, TInclude> AddRangeFacet(Guid propertyId) {
        var c = new QueryOfFacets<T, TInclude>(this);
        var property = c._query.Store.Datastore.Datamodel.Properties[propertyId];
        if (!c._given.ContainsKey(propertyId)) c._given.Add(propertyId, new(property, true));
        return c;
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

    [Pure]
    public QueryOfFacets<T, TInclude> SetFacetValue(Expression<Func<T, object?>> expression, object value, string? displayName = null) => SetFacetValue(getPropertyId(expression), value, displayName);
    [Pure]
    public QueryOfFacets<T, TInclude> SetFacetValue<TChild>(Expression<Func<TChild, object?>> expression, object value, string? displayName = null) where TChild : T => SetFacetValue(getPropertyId(expression), value, displayName);
    [Pure]
    public QueryOfFacets<T, TInclude> SetFacetValue(string propertyName, object value, string? displayName = null) => SetFacetValue(getPropertyId<T>(propertyName), value, displayName);
    [Pure]
    public QueryOfFacets<T, TInclude> SetFacetValue<TChild>(string propertyName, object value, string? displayName = null) where TChild : T => SetFacetValue(getPropertyId<TChild>(propertyName), value, displayName);
    [Pure]
    public QueryOfFacets<T, TInclude> SetFacetValue(Guid propertyId, object value, string? displayName = null) {
        var fv = new FacetValue(value);
        if (displayName != null) fv.DisplayName = displayName;
        var c = new QueryOfFacets<T, TInclude>(this);
        c.setFacetValue(propertyId, false, fv);
        return c;
    }

    [Pure]
    public QueryOfFacets<T, TInclude> SetFacetRangeValue(Expression<Func<T, object?>> expression, object from, object to, string? displayName = null) => SetFacetRangeValue(getPropertyId(expression), from, to, displayName);
    [Pure]
    public QueryOfFacets<T, TInclude> SetFacetRangeValue<TChild>(Expression<Func<TChild, object?>> expression, object from, object to, string? displayName = null) where TChild : T => SetFacetRangeValue(getPropertyId(expression), from, to, displayName);
    [Pure]
    public QueryOfFacets<T, TInclude> SetFacetRangeValue(string propertyName, object from, object to, string? displayName = null) => SetFacetRangeValue(getPropertyId<T>(propertyName), from, to, displayName);
    [Pure]
    public QueryOfFacets<T, TInclude> SetFacetRangeValue<TChild>(string propertyName, object from, object to, string? displayName = null) where TChild : T => SetFacetRangeValue(getPropertyId<TChild>(propertyName), from, to, displayName);
    [Pure]
    public QueryOfFacets<T, TInclude> SetFacetRangeValue(Guid propertyId, object from, object to, string? displayName = null) {
        var c = new QueryOfFacets<T, TInclude>(this);
        c.setFacetValue(propertyId, true, new FacetValue(from, to, displayName));
        return c;
    }

    [Pure]
    public QueryOfFacets<T, TInclude> SetFacetMissingValue(Expression<Func<T, object?>> expression) => SetFacetMissingValue(getPropertyId(expression));
    [Pure]
    public QueryOfFacets<T, TInclude> SetFacetMissingValue(string propertyName) => SetFacetMissingValue(getPropertyId<T>(propertyName));
    [Pure]
    public QueryOfFacets<T, TInclude> SetFacetMissingValue<TChild>(string propertyName) where TChild : T => SetFacetMissingValue(getPropertyId<TChild>(propertyName));
    [Pure]
    public QueryOfFacets<T, TInclude> SetFacetMissingValue(Guid propertyId) { // selects the missing-value bucket (nodes without a value)
        var c = new QueryOfFacets<T, TInclude>(this);
        c.setFacetValue(propertyId, false, new FacetValue(null));
        return c;
    }

    [Pure]
    public QueryOfFacets<T, TInclude> SetFacetOptions(Expression<Func<T, object?>> expression, int maxValues = 0, int minCount = 0, bool includeMissing = false, bool sortByCount = false, int rangeCount = 0)
        => SetFacetOptions(getPropertyId(expression), maxValues, minCount, includeMissing, sortByCount, rangeCount);
    [Pure]
    public QueryOfFacets<T, TInclude> SetFacetOptions(string propertyName, int maxValues = 0, int minCount = 0, bool includeMissing = false, bool sortByCount = false, int rangeCount = 0)
        => SetFacetOptions(getPropertyId<T>(propertyName), maxValues, minCount, includeMissing, sortByCount, rangeCount);
    [Pure]
    public QueryOfFacets<T, TInclude> SetFacetOptions<TChild>(string propertyName, int maxValues = 0, int minCount = 0, bool includeMissing = false, bool sortByCount = false, int rangeCount = 0) where TChild : T
        => SetFacetOptions(getPropertyId<TChild>(propertyName), maxValues, minCount, includeMissing, sortByCount, rangeCount);
    [Pure]
    public QueryOfFacets<T, TInclude> SetFacetOptions(Guid propertyId, int maxValues = 0, int minCount = 0, bool includeMissing = false, bool sortByCount = false, int rangeCount = 0) {
        var c = new QueryOfFacets<T, TInclude>(this);
        c.addFacet(propertyId);
        var facets = c._given[propertyId];
        facets.MaxValues = maxValues;
        facets.MinCount = minCount;
        facets.IncludeMissing = includeMissing;
        facets.SortByCount = sortByCount;
        if (rangeCount > 0) facets.RangeCount = rangeCount;
        return c;
    }

    [Pure]
    public QueryOfFacets<T, TInclude> Page(int pageIndex, int pageSize) {
        if (pageIndex < 0) throw new ArgumentOutOfRangeException(nameof(pageIndex), "Page index must be greater than or equal to 0.");
        if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be greater than 0.");
        var c = new QueryOfFacets<T, TInclude>(this);
        c._pageIndex = pageIndex;
        c._pageSize = pageSize;
        return c;
    }

    public override string ToString() => ToQueryString(includePaging: true);
    // without paging: what a clause chained onto the facet query (Pivot) is built on
    internal string ToQueryString(bool includePaging) {
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
                    sb.Append(QueryOfFacets<T, TInclude>.ValueToString(facetValue.Value!));
                } else {
                    sb.Append("." + nameof(this.AddRangeFacet) + "(" + pn(facet.PropertyId) + ", ");
                    sb.Append(QueryOfFacets<T, TInclude>.ValueToString(facetValue.Value!));
                    sb.Append(", ");
                    sb.Append(QueryOfFacets<T, TInclude>.ValueToString(facetValue.Value2));
                }
                sb.Append(')');
            }
            // the typed API only travels as a query string, so options must be emitted too:
            var defaults = new Facets(dm.Properties[facet.PropertyId]);
            if (facet.MaxValues != 0 || facet.MinCount != 0 || facet.IncludeMissing || facet.SortByCount || facet.RangeCount != defaults.RangeCount) {
                sb.Append("." + nameof(this.SetFacetOptions) + "(" + pn(facet.PropertyId) + ", ");
                sb.Append(facet.MaxValues + ", " + facet.MinCount + ", " + (facet.IncludeMissing ? "true" : "false") + ", " + (facet.SortByCount ? "true" : "false"));
                if (facet.RangeCount != defaults.RangeCount) sb.Append(", " + facet.RangeCount);
                sb.Append(')');
            }
        }
        foreach (var facet in _set.Values) {
            foreach (var facetValue in facet.Values) {
                if (facetValue.Value == null) {
                    sb.Append("." + nameof(this.SetFacetMissingValue) + "(" + pn(facet.PropertyId) + ")");
                    continue;
                }
                if (facetValue.Value2 == null) {
                    sb.Append("." + nameof(this.SetFacetValue) + "(" + pn(facet.PropertyId) + ", ");
                    sb.Append(QueryOfFacets<T, TInclude>.ValueToString(facetValue.Value));
                } else {
                    sb.Append("." + nameof(this.SetFacetRangeValue) + "(" + pn(facet.PropertyId) + ", ");
                    sb.Append(QueryOfFacets<T, TInclude>.ValueToString(facetValue.Value));
                    sb.Append(", ");
                    sb.Append(QueryOfFacets<T, TInclude>.ValueToString(facetValue.Value2));
                }
                sb.Append(')');
            }
        }
        if (includePaging && (_pageIndex > 0 || _pageSize > 0)) {
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
    internal static string ValueToString(object? v) {
        if (v is int i) return i.ToString();
        if (v is Enum e) return Convert.ToInt32(e).ToString(); // before IFormattable, which would emit the NAME and never match an int bucket (same rule as QueryStringBuilder.writeValue)
        if (v is double d) return d.ToString(CultureInfo.InvariantCulture);
        if (v is DateTime dt) return dt.ToString("O").ToStringLiteral(); // round-trip format; default ToString is culture dependent and cannot be parsed back reliably
        if (v is DateTimeOffset dto) return dto.ToString("O").ToStringLiteral();
        if (v is IFormattable f) return f.ToString(null, CultureInfo.InvariantCulture).ToStringLiteral();
        return (v + "").ToStringLiteral();
    }
    // the query context of the base query applies to the facet query too, and has to be passed on
    // explicitly: without it the store reads with its default context
    public async Task<ResultSetFacets<T>> ExecuteAsync() => await _query.Store.Datastore.QueryAsync(ToString(), _query._q._parameters, _query._q._ctx).ContinueWith(t => _execute(t.Result));
    public ResultSetFacets<T> Execute(string query) => _execute(_query.Store.Datastore.Query(query, _query._q._parameters, _query._q._ctx));
    public ResultSetFacets<T> Execute() => _execute(_query.Store.Datastore.QueryAsync(ToString(), _query._q._parameters, _query._q._ctx).Result);
    // evaluate the FULL facet query (this.ToString()), not the base node query: the facet
    // clauses exist only in this class and would otherwise be silently dropped
    public object? EvaluateForJson() => new QueryStringEvaluater(_query.Store, ToString(), _query._q._parameters, _query._q._ctx).EvaluateForJsonAsync().Result;
    public async Task<object?> EvaluateForJsonAsync() => await new QueryStringEvaluater(_query.Store, ToString(), _query._q._parameters, _query._q._ctx).EvaluateForJsonAsync();
    ResultSetFacets<T> _execute(object? data) {
        if (data is not FacetQueryResultData facets)
            throw new NotSupportedException("Only results of type " + nameof(FacetQueryResultData) + " is supported. Type provided: " + data?.GetType().FullName);
        FacetNodeValueMapper.MapNodeDataValues(facets, _query.Store); // relation facet values: node data -> typed node objects
        var values = toEnumerable<T>(facets.Result);
        return new(values, facets);
    }
    IEnumerable<TCast> toEnumerable<TCast>(object data) {
        if (data is IStoreNodeDataCollection coll) {
            foreach (var nodeData in coll.NodeValues) {
                yield return _query.Store.Mapper.CreateObjectFromNodeData<TCast>(nodeData, null);
            }
        }
    }
}
