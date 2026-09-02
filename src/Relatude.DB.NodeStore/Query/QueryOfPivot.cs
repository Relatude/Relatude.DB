using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Nodes;
using Relatude.DB.Query.Data;
using Relatude.DB.Serialization;
using System.Diagnostics.Contracts;
using System.Linq.Expressions;
using System.Text;

namespace Relatude.DB.Query;

/// <summary>
/// Maps relation-property group values from node data to typed node objects before the result is
/// handed to the user, the way relation facet buckets are. Idempotent; scalar values are untouched.
/// </summary>
internal static class PivotNodeValueMapper {
    internal static void MapNodeDataValues(PivotResult result, NodeStore store) {
        foreach (var g in result.Rows.Groups) map(g, store);
        foreach (var g in result.Columns.Groups) map(g, store);
        foreach (var s in result.RowSubTotals) map(s.Group, store);
        foreach (var s in result.ColumnSubTotals) map(s.Group, store);
    }
    static void map(PivotGroup group, NodeStore store) {
        for (var i = 0; i < group.Values.Length; i++) {
            if (group.Values[i] is not INodeDataExternal nodeData) continue;
            try {
                group.Values[i] = store.Get(nodeData);
            } catch {
                // no compiled mapper for the node type (e.g. json-only datamodel): keep the node data
            }
        }
    }
}

/// <summary>
/// A pivot query: rows and columns grouped by properties, measures computed per cell - like a
/// spreadsheet pivot table. Immutable like the node query it wraps: every Add/Set operator returns a
/// NEW pivot query, so the result must be used: pq = pq.AddRow(...). Opened with Pivot() on a node
/// query, or on a facet query to pivot the nodes the facet selection leaves.
/// </summary>
public sealed class QueryOfPivot<T, TInclude> : IQueryExecutable<PivotResult> {
    readonly NodeStore _store;
    readonly string _baseQuery; // the node (or facet) query this pivots, as a query string
    readonly List<Parameter> _parameters;
    readonly QueryContext? _ctx;
    readonly PivotSpec _spec;
    internal QueryOfPivot(QueryOfNodes<T, TInclude> query) {
        _store = query.Store;
        _baseQuery = query.ToString();
        _parameters = query._q._parameters;
        _ctx = query._q._ctx;
        _spec = new PivotSpec();
    }
    internal QueryOfPivot(QueryOfFacets<T, TInclude> facetQuery) {
        _store = facetQuery.Query.Store;
        _baseQuery = facetQuery.ToQueryString(includePaging: false); // the selection filters, the page does not
        _parameters = facetQuery.Query._q._parameters;
        _ctx = facetQuery.Query._q._ctx;
        _spec = new PivotSpec();
    }
    QueryOfPivot(QueryOfPivot<T, TInclude> source) { // copy for the immutable operators
        _store = source._store;
        _baseQuery = source._baseQuery;
        _parameters = source._parameters;
        _ctx = source._ctx;
        _spec = source._spec.Clone();
    }
    QueryOfPivot<T, TInclude> fork(Action<PivotSpec> change) {
        var c = new QueryOfPivot<T, TInclude>(this);
        change(c._spec);
        return c;
    }
    Datamodel dm => _store.Datastore.Datamodel;
    Guid getPropertyId<TChild>(Expression<Func<TChild, object?>> expression) where TChild : T => _store.Mapper.GetProperty<TChild>(expression).Id;
    Guid getPropertyId<TChild>(string propertyName) where TChild : T => _store.Mapper.GetProperty<TChild>(propertyName).Id;
    PropertyModel property(Guid propertyId) => dm.Properties.TryGetValue(propertyId, out var p) ? p : throw new ArgumentException("Unknown property " + propertyId + ". ", nameof(propertyId));
    List<PivotGroupSpec> axis(PivotSpec spec, bool rows) => rows ? spec.Rows : spec.Columns;

    // ── grouping: rows ──
    /// <summary>Adds a row grouping level on a property; the engine picks value or range buckets (the AddFacet rule).</summary>
    [Pure] public QueryOfPivot<T, TInclude> AddRow(Expression<Func<T, object?>> property) => AddRow(getPropertyId(property));
    [Pure] public QueryOfPivot<T, TInclude> AddRow<TChild>(Expression<Func<TChild, object?>> property) where TChild : T => AddRow(getPropertyId(property));
    [Pure] public QueryOfPivot<T, TInclude> AddRow(string propertyName) => AddRow(getPropertyId<T>(propertyName));
    [Pure] public QueryOfPivot<T, TInclude> AddRow(Guid propertyId) => addGroup(true, propertyId, null, DateInterval.None, 0);
    /// <summary>Adds a row grouping level on a date property, bucketed by calendar interval.</summary>
    [Pure] public QueryOfPivot<T, TInclude> AddRow(Expression<Func<T, object?>> property, DateInterval interval) => AddRow(getPropertyId(property), interval);
    [Pure] public QueryOfPivot<T, TInclude> AddRow<TChild>(Expression<Func<TChild, object?>> property, DateInterval interval) where TChild : T => AddRow(getPropertyId(property), interval);
    [Pure] public QueryOfPivot<T, TInclude> AddRow(string propertyName, DateInterval interval) => AddRow(getPropertyId<T>(propertyName), interval);
    [Pure] public QueryOfPivot<T, TInclude> AddRow(Guid propertyId, DateInterval interval) => addGroup(true, propertyId, true, interval, 0);
    /// <summary>Adds a row grouping level with one group per distinct value.</summary>
    [Pure] public QueryOfPivot<T, TInclude> AddRowValues(Expression<Func<T, object?>> property) => AddRowValues(getPropertyId(property));
    [Pure] public QueryOfPivot<T, TInclude> AddRowValues<TChild>(Expression<Func<TChild, object?>> property) where TChild : T => AddRowValues(getPropertyId(property));
    [Pure] public QueryOfPivot<T, TInclude> AddRowValues(string propertyName) => AddRowValues(getPropertyId<T>(propertyName));
    [Pure] public QueryOfPivot<T, TInclude> AddRowValues(Guid propertyId) => addGroup(true, propertyId, false, DateInterval.None, 0);
    /// <summary>Adds a row grouping level with auto-generated numeric/date ranges (0 = the property's default count).</summary>
    [Pure] public QueryOfPivot<T, TInclude> AddRowRanges(Expression<Func<T, object?>> property, int bucketCount = 0) => AddRowRanges(getPropertyId(property), bucketCount);
    [Pure] public QueryOfPivot<T, TInclude> AddRowRanges<TChild>(Expression<Func<TChild, object?>> property, int bucketCount = 0) where TChild : T => AddRowRanges(getPropertyId(property), bucketCount);
    [Pure] public QueryOfPivot<T, TInclude> AddRowRanges(string propertyName, int bucketCount = 0) => AddRowRanges(getPropertyId<T>(propertyName), bucketCount);
    [Pure] public QueryOfPivot<T, TInclude> AddRowRanges(Guid propertyId, int bucketCount = 0) => addGroup(true, propertyId, true, DateInterval.None, bucketCount);
    /// <summary>Adds one explicit range bucket; consecutive ranges on the same property form one level.</summary>
    [Pure] public QueryOfPivot<T, TInclude> AddRowRange(Expression<Func<T, object?>> property, object from, object to, string? displayName = null) => AddRowRange(getPropertyId(property), from, to, displayName);
    [Pure] public QueryOfPivot<T, TInclude> AddRowRange<TChild>(Expression<Func<TChild, object?>> property, object from, object to, string? displayName = null) where TChild : T => AddRowRange(getPropertyId(property), from, to, displayName);
    [Pure] public QueryOfPivot<T, TInclude> AddRowRange(string propertyName, object from, object to, string? displayName = null) => AddRowRange(getPropertyId<T>(propertyName), from, to, displayName);
    [Pure] public QueryOfPivot<T, TInclude> AddRowRange(Guid propertyId, object from, object to, string? displayName = null) => addRange(true, propertyId, from, to, displayName);

    // ── grouping: columns ──
    [Pure] public QueryOfPivot<T, TInclude> AddColumn(Expression<Func<T, object?>> property) => AddColumn(getPropertyId(property));
    [Pure] public QueryOfPivot<T, TInclude> AddColumn<TChild>(Expression<Func<TChild, object?>> property) where TChild : T => AddColumn(getPropertyId(property));
    [Pure] public QueryOfPivot<T, TInclude> AddColumn(string propertyName) => AddColumn(getPropertyId<T>(propertyName));
    [Pure] public QueryOfPivot<T, TInclude> AddColumn(Guid propertyId) => addGroup(false, propertyId, null, DateInterval.None, 0);
    [Pure] public QueryOfPivot<T, TInclude> AddColumn(Expression<Func<T, object?>> property, DateInterval interval) => AddColumn(getPropertyId(property), interval);
    [Pure] public QueryOfPivot<T, TInclude> AddColumn<TChild>(Expression<Func<TChild, object?>> property, DateInterval interval) where TChild : T => AddColumn(getPropertyId(property), interval);
    [Pure] public QueryOfPivot<T, TInclude> AddColumn(string propertyName, DateInterval interval) => AddColumn(getPropertyId<T>(propertyName), interval);
    [Pure] public QueryOfPivot<T, TInclude> AddColumn(Guid propertyId, DateInterval interval) => addGroup(false, propertyId, true, interval, 0);
    [Pure] public QueryOfPivot<T, TInclude> AddColumnValues(Expression<Func<T, object?>> property) => AddColumnValues(getPropertyId(property));
    [Pure] public QueryOfPivot<T, TInclude> AddColumnValues<TChild>(Expression<Func<TChild, object?>> property) where TChild : T => AddColumnValues(getPropertyId(property));
    [Pure] public QueryOfPivot<T, TInclude> AddColumnValues(string propertyName) => AddColumnValues(getPropertyId<T>(propertyName));
    [Pure] public QueryOfPivot<T, TInclude> AddColumnValues(Guid propertyId) => addGroup(false, propertyId, false, DateInterval.None, 0);
    [Pure] public QueryOfPivot<T, TInclude> AddColumnRanges(Expression<Func<T, object?>> property, int bucketCount = 0) => AddColumnRanges(getPropertyId(property), bucketCount);
    [Pure] public QueryOfPivot<T, TInclude> AddColumnRanges<TChild>(Expression<Func<TChild, object?>> property, int bucketCount = 0) where TChild : T => AddColumnRanges(getPropertyId(property), bucketCount);
    [Pure] public QueryOfPivot<T, TInclude> AddColumnRanges(string propertyName, int bucketCount = 0) => AddColumnRanges(getPropertyId<T>(propertyName), bucketCount);
    [Pure] public QueryOfPivot<T, TInclude> AddColumnRanges(Guid propertyId, int bucketCount = 0) => addGroup(false, propertyId, true, DateInterval.None, bucketCount);
    [Pure] public QueryOfPivot<T, TInclude> AddColumnRange(Expression<Func<T, object?>> property, object from, object to, string? displayName = null) => AddColumnRange(getPropertyId(property), from, to, displayName);
    [Pure] public QueryOfPivot<T, TInclude> AddColumnRange<TChild>(Expression<Func<TChild, object?>> property, object from, object to, string? displayName = null) where TChild : T => AddColumnRange(getPropertyId(property), from, to, displayName);
    [Pure] public QueryOfPivot<T, TInclude> AddColumnRange(string propertyName, object from, object to, string? displayName = null) => AddColumnRange(getPropertyId<T>(propertyName), from, to, displayName);
    [Pure] public QueryOfPivot<T, TInclude> AddColumnRange(Guid propertyId, object from, object to, string? displayName = null) => addRange(false, propertyId, from, to, displayName);

    QueryOfPivot<T, TInclude> addGroup(bool rows, Guid propertyId, bool? isRange, DateInterval interval, int bucketCount) {
        var p = property(propertyId);
        if (interval != DateInterval.None && p.PropertyType != PropertyType.DateTime && p.PropertyType != PropertyType.DateTimeOffset)
            throw new ArgumentException("A date interval can only be used on a DateTime or DateTimeOffset property. \"" + p.CodeName + "\" is " + p.PropertyType + ". ");
        return fork(s => axis(s, rows).Add(new PivotGroupSpec(propertyId) { IsRange = isRange, Interval = interval, BucketCount = bucketCount }));
    }
    QueryOfPivot<T, TInclude> addRange(bool rows, Guid propertyId, object from, object to, string? displayName) {
        property(propertyId);
        return fork(s => {
            var level = axis(s, rows).LastOrDefault(l => l.PropertyId == propertyId && l.IsRange == true && l.Interval == DateInterval.None);
            if (level == null) axis(s, rows).Add(level = new PivotGroupSpec(propertyId) { IsRange = true });
            level.Values.Add(new FacetValue(from, to, displayName));
        });
    }

    // ── options per level ──
    /// <summary>
    /// Options for the row level grouping on a property: maxGroups keeps the first N groups (after sorting),
    /// minCount drops smaller groups, includeMissing adds a group for nodes without a value, sortByMeasure
    /// orders the groups by a measure name (or "Count"), otherGroup collects what was trimmed into one group.
    /// </summary>
    [Pure] public QueryOfPivot<T, TInclude> SetRowOptions(Expression<Func<T, object?>> property, int maxGroups = 0, int minCount = 0, bool includeMissing = false, string? sortByMeasure = null, bool descending = true, bool otherGroup = false)
        => SetRowOptions(getPropertyId(property), maxGroups, minCount, includeMissing, sortByMeasure, descending, otherGroup);
    [Pure] public QueryOfPivot<T, TInclude> SetRowOptions<TChild>(Expression<Func<TChild, object?>> property, int maxGroups = 0, int minCount = 0, bool includeMissing = false, string? sortByMeasure = null, bool descending = true, bool otherGroup = false) where TChild : T
        => SetRowOptions(getPropertyId(property), maxGroups, minCount, includeMissing, sortByMeasure, descending, otherGroup);
    [Pure] public QueryOfPivot<T, TInclude> SetRowOptions(string propertyName, int maxGroups = 0, int minCount = 0, bool includeMissing = false, string? sortByMeasure = null, bool descending = true, bool otherGroup = false)
        => SetRowOptions(getPropertyId<T>(propertyName), maxGroups, minCount, includeMissing, sortByMeasure, descending, otherGroup);
    [Pure] public QueryOfPivot<T, TInclude> SetRowOptions(Guid propertyId, int maxGroups = 0, int minCount = 0, bool includeMissing = false, string? sortByMeasure = null, bool descending = true, bool otherGroup = false)
        => setOptions(true, propertyId, maxGroups, minCount, includeMissing, sortByMeasure, descending, otherGroup);
    [Pure] public QueryOfPivot<T, TInclude> SetColumnOptions(Expression<Func<T, object?>> property, int maxGroups = 0, int minCount = 0, bool includeMissing = false, string? sortByMeasure = null, bool descending = true, bool otherGroup = false)
        => SetColumnOptions(getPropertyId(property), maxGroups, minCount, includeMissing, sortByMeasure, descending, otherGroup);
    [Pure] public QueryOfPivot<T, TInclude> SetColumnOptions<TChild>(Expression<Func<TChild, object?>> property, int maxGroups = 0, int minCount = 0, bool includeMissing = false, string? sortByMeasure = null, bool descending = true, bool otherGroup = false) where TChild : T
        => SetColumnOptions(getPropertyId(property), maxGroups, minCount, includeMissing, sortByMeasure, descending, otherGroup);
    [Pure] public QueryOfPivot<T, TInclude> SetColumnOptions(string propertyName, int maxGroups = 0, int minCount = 0, bool includeMissing = false, string? sortByMeasure = null, bool descending = true, bool otherGroup = false)
        => SetColumnOptions(getPropertyId<T>(propertyName), maxGroups, minCount, includeMissing, sortByMeasure, descending, otherGroup);
    [Pure] public QueryOfPivot<T, TInclude> SetColumnOptions(Guid propertyId, int maxGroups = 0, int minCount = 0, bool includeMissing = false, string? sortByMeasure = null, bool descending = true, bool otherGroup = false)
        => setOptions(false, propertyId, maxGroups, minCount, includeMissing, sortByMeasure, descending, otherGroup);
    QueryOfPivot<T, TInclude> setOptions(bool rows, Guid propertyId, int maxGroups, int minCount, bool includeMissing, string? sortByMeasure, bool descending, bool otherGroup) {
        var p = property(propertyId);
        return fork(s => {
            var level = axis(s, rows).LastOrDefault(l => l.PropertyId == propertyId)
                ?? throw new ArgumentException("No " + (rows ? "row" : "column") + " group on \"" + p.CodeName + "\" to set options for. Add the group first. ");
            level.MaxGroups = maxGroups;
            level.MinCount = minCount;
            level.IncludeMissing = includeMissing;
            level.SortByMeasure = string.IsNullOrEmpty(sortByMeasure) ? null : sortByMeasure;
            level.Descending = descending;
            level.OtherGroup = otherGroup;
        });
    }

    // ── measures ──
    /// <summary>The number of nodes in each cell. Named "Count" unless a name is given.</summary>
    [Pure] public QueryOfPivot<T, TInclude> AddCount(string? name = null) => fork(s => s.Measures.Add(new PivotMeasureSpec(PivotFunction.Count, Guid.Empty, name)));
    [Pure] public QueryOfPivot<T, TInclude> AddCountDistinct(Expression<Func<T, object?>> property, string? name = null) => AddMeasure(PivotFunction.CountDistinct, getPropertyId(property), name);
    [Pure] public QueryOfPivot<T, TInclude> AddCountDistinct<TChild>(Expression<Func<TChild, object?>> property, string? name = null) where TChild : T => AddMeasure(PivotFunction.CountDistinct, getPropertyId(property), name);
    [Pure] public QueryOfPivot<T, TInclude> AddCountDistinct(string propertyName, string? name = null) => AddMeasure(PivotFunction.CountDistinct, getPropertyId<T>(propertyName), name);
    [Pure] public QueryOfPivot<T, TInclude> AddSum(Expression<Func<T, object?>> property, string? name = null) => AddMeasure(PivotFunction.Sum, getPropertyId(property), name);
    [Pure] public QueryOfPivot<T, TInclude> AddSum<TChild>(Expression<Func<TChild, object?>> property, string? name = null) where TChild : T => AddMeasure(PivotFunction.Sum, getPropertyId(property), name);
    [Pure] public QueryOfPivot<T, TInclude> AddSum(string propertyName, string? name = null) => AddMeasure(PivotFunction.Sum, getPropertyId<T>(propertyName), name);
    [Pure] public QueryOfPivot<T, TInclude> AddAverage(Expression<Func<T, object?>> property, string? name = null) => AddMeasure(PivotFunction.Average, getPropertyId(property), name);
    [Pure] public QueryOfPivot<T, TInclude> AddAverage<TChild>(Expression<Func<TChild, object?>> property, string? name = null) where TChild : T => AddMeasure(PivotFunction.Average, getPropertyId(property), name);
    [Pure] public QueryOfPivot<T, TInclude> AddAverage(string propertyName, string? name = null) => AddMeasure(PivotFunction.Average, getPropertyId<T>(propertyName), name);
    [Pure] public QueryOfPivot<T, TInclude> AddMin(Expression<Func<T, object?>> property, string? name = null) => AddMeasure(PivotFunction.Min, getPropertyId(property), name);
    [Pure] public QueryOfPivot<T, TInclude> AddMin<TChild>(Expression<Func<TChild, object?>> property, string? name = null) where TChild : T => AddMeasure(PivotFunction.Min, getPropertyId(property), name);
    [Pure] public QueryOfPivot<T, TInclude> AddMin(string propertyName, string? name = null) => AddMeasure(PivotFunction.Min, getPropertyId<T>(propertyName), name);
    [Pure] public QueryOfPivot<T, TInclude> AddMax(Expression<Func<T, object?>> property, string? name = null) => AddMeasure(PivotFunction.Max, getPropertyId(property), name);
    [Pure] public QueryOfPivot<T, TInclude> AddMax<TChild>(Expression<Func<TChild, object?>> property, string? name = null) where TChild : T => AddMeasure(PivotFunction.Max, getPropertyId(property), name);
    [Pure] public QueryOfPivot<T, TInclude> AddMax(string propertyName, string? name = null) => AddMeasure(PivotFunction.Max, getPropertyId<T>(propertyName), name);
    /// <summary>Adds a measure by function. Sum/Average/Min/Max need a numeric property; CountDistinct any indexed scalar one.</summary>
    [Pure] public QueryOfPivot<T, TInclude> AddMeasure(PivotFunction function, Expression<Func<T, object?>> property, string? name = null) => AddMeasure(function, getPropertyId(property), name);
    [Pure] public QueryOfPivot<T, TInclude> AddMeasure(PivotFunction function, string propertyName, string? name = null) => AddMeasure(function, getPropertyId<T>(propertyName), name);
    [Pure] public QueryOfPivot<T, TInclude> AddMeasure(PivotFunction function, Guid propertyId, string? name = null) {
        if (function == PivotFunction.Count) return AddCount(name);
        property(propertyId);
        return fork(s => s.Measures.Add(new PivotMeasureSpec(function, propertyId, name)));
    }

    // ── whole-pivot options ──
    /// <summary>Which totals to compute. Sub-totals are the totals of every group above the leaf level, on axes with several levels.</summary>
    [Pure] public QueryOfPivot<T, TInclude> SetTotals(bool rows = true, bool columns = true, bool subTotals = false) => fork(s => { s.RowTotals = rows; s.ColumnTotals = columns; s.SubTotals = subTotals; });
    /// <summary>The most cells (row groups x column groups) a pivot may have. Past it the row axis is truncated and the result marked Capped, or the query throws.</summary>
    [Pure] public QueryOfPivot<T, TInclude> SetLimits(int maxCells = PivotSpec.DefaultMaxCells, bool throwWhenExceeded = false) {
        if (maxCells <= 0) throw new ArgumentOutOfRangeException(nameof(maxCells), "Max cells must be greater than 0.");
        return fork(s => { s.MaxCells = maxCells; s.ThrowWhenExceeded = throwWhenExceeded; });
    }
    /// <summary>Pages the row groups (after sorting and trimming). Rows.TotalGroupCount tells how many there are in all.</summary>
    [Pure] public QueryOfPivot<T, TInclude> SetRowPaging(int pageIndex, int pageSize) {
        if (pageIndex < 0) throw new ArgumentOutOfRangeException(nameof(pageIndex), "Page index must be greater than or equal to 0.");
        if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be greater than 0.");
        return fork(s => { s.RowPageIndex = pageIndex; s.RowPageSize = pageSize; });
    }

    // ── the query string: the only form the store understands ──
    public override string ToString() {
        var sb = new StringBuilder(_baseQuery);
        sb.Append(".Pivot()");
        appendAxis(sb, _spec.Rows, "Row");
        appendAxis(sb, _spec.Columns, "Column");
        foreach (var m in _spec.Measures) {
            switch (m.Function) {
                case PivotFunction.Count:
                    sb.Append(".AddCount(");
                    if (m.Name != null) sb.Append(m.Name.ToStringLiteral());
                    sb.Append(')');
                    break;
                default:
                    var method = m.Function switch {
                        PivotFunction.CountDistinct => "AddCountDistinct",
                        PivotFunction.Sum => "AddSum",
                        PivotFunction.Average => "AddAverage",
                        PivotFunction.Min => "AddMin",
                        PivotFunction.Max => "AddMax",
                        _ => throw new NotSupportedException(m.Function.ToString()),
                    };
                    sb.Append('.').Append(method).Append('(').Append(pn(m.PropertyId));
                    if (m.Name != null) sb.Append(", ").Append(m.Name.ToStringLiteral());
                    sb.Append(')');
                    break;
            }
        }
        if (!_spec.RowTotals || !_spec.ColumnTotals || _spec.SubTotals)
            sb.Append(".SetTotals(").Append(b(_spec.RowTotals)).Append(", ").Append(b(_spec.ColumnTotals)).Append(", ").Append(b(_spec.SubTotals)).Append(')');
        if (_spec.MaxCells != PivotSpec.DefaultMaxCells || _spec.ThrowWhenExceeded)
            sb.Append(".SetLimits(").Append(_spec.MaxCells).Append(", ").Append(b(_spec.ThrowWhenExceeded)).Append(')');
        if (_spec.RowPageSize.HasValue)
            sb.Append(".SetRowPaging(").Append(_spec.RowPageIndex).Append(", ").Append(_spec.RowPageSize.Value).Append(')');
        return sb.ToString();
    }
    void appendAxis(StringBuilder sb, List<PivotGroupSpec> levels, string axis) {
        foreach (var l in levels) {
            if (l.Interval != DateInterval.None) {
                sb.Append(".Add").Append(axis).Append('(').Append(pn(l.PropertyId)).Append(", ").Append(l.Interval.ToString().ToStringLiteral()).Append(')');
            } else if (l.Values.Count > 0) {
                foreach (var v in l.Values) {
                    sb.Append(".Add").Append(axis).Append("Range(").Append(pn(l.PropertyId)).Append(", ");
                    sb.Append(QueryOfFacets<T, TInclude>.ValueToString(v.Value!)).Append(", ").Append(QueryOfFacets<T, TInclude>.ValueToString(v.Value2!));
                    if (v.ExplicitDisplayName != null) sb.Append(", ").Append(v.ExplicitDisplayName.ToStringLiteral());
                    sb.Append(')');
                }
            } else if (l.IsRange == true) {
                sb.Append(".Add").Append(axis).Append("Ranges(").Append(pn(l.PropertyId));
                if (l.BucketCount > 0) sb.Append(", ").Append(l.BucketCount);
                sb.Append(')');
            } else if (l.IsRange == false) {
                sb.Append(".Add").Append(axis).Append("Values(").Append(pn(l.PropertyId)).Append(')');
            } else {
                sb.Append(".Add").Append(axis).Append('(').Append(pn(l.PropertyId)).Append(')');
            }
            if (l.HasOptions) {
                sb.Append(".Set").Append(axis).Append("Options(").Append(pn(l.PropertyId)).Append(", ");
                sb.Append(l.MaxGroups).Append(", ").Append(l.MinCount).Append(", ").Append(b(l.IncludeMissing)).Append(", ");
                sb.Append((l.SortByMeasure ?? "").ToStringLiteral()).Append(", ").Append(b(l.Descending)).Append(", ").Append(b(l.OtherGroup)).Append(')');
            }
        }
    }
    static string b(bool v) => v ? "true" : "false";
    string pn(Guid propertyId) => "\"" + propertyId + "|" + dm.Properties[propertyId].CodeName + "\"";

    // ── execution ──
    public PivotResult Execute() => _execute(_store.Datastore.Query(ToString(), _parameters, _ctx));
    public async Task<PivotResult> ExecuteAsync() => _execute(await _store.Datastore.QueryAsync(ToString(), _parameters, _ctx));
    public object? EvaluateForJson() => new QueryStringEvaluater(_store, ToString(), _parameters, _ctx).EvaluateForJsonAsync().Result;
    public async Task<object?> EvaluateForJsonAsync() => await new QueryStringEvaluater(_store, ToString(), _parameters, _ctx).EvaluateForJsonAsync();
    PivotResult _execute(object? data) {
        if (data is not PivotQueryResultData pivot)
            throw new NotSupportedException("Only results of type " + nameof(PivotQueryResultData) + " is supported. Type provided: " + data?.GetType().FullName);
        PivotNodeValueMapper.MapNodeDataValues(pivot.Result, _store); // relation group values: node data -> typed node objects
        return pivot.Result;
    }
}
