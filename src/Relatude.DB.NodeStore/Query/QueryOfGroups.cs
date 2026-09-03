using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Nodes;
using Relatude.DB.Query.Data;
using Relatude.DB.Serialization;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Relatude.DB.Query;

// ── public helper types ─────────────────────────────────────────────────────────

/// <summary>
/// A range bucket used as a GroupBy key: the key of a <see cref="Bucket.Ranges{T}(T, int)"/> level,
/// and of the calendar and range levels of the runtime <see cref="GroupKey"/> form. Label is the
/// engine's name for the bucket ("100 - 500", "2026-03", "(none)").
/// </summary>
public readonly record struct GroupRange<T>(T From, T To, string Label) {
    public override string ToString() => Label;
}

/// <summary>
/// Bucketing helpers for GroupBy key expressions, the way EF.Functions marks translation-only calls.
/// Ranges is translated, never run: it throws outside a GroupBy key. Interval floors a date to the
/// start of its calendar bucket and works in memory too.
/// </summary>
public static class Bucket {
    /// <summary>Auto-generated numeric or date ranges (0 = the property's default range count). Key type: GroupRange.</summary>
    public static GroupRange<T> Ranges<T>(T value, int bucketCount = 0) where T : struct
        => throw new NotSupportedException("Bucket.Ranges is only meaningful inside a GroupBy key expression, where it is translated to range buckets. ");
    /// <summary>
    /// Explicit consecutive ranges: boundaries [0, 100, 500] give the buckets 0-100 and 100-500. Values
    /// outside the outer boundaries fall in no group; a value exactly on an inner boundary is in both
    /// neighbouring buckets (the ranges are inclusive), so put boundaries between the values.
    /// </summary>
    public static GroupRange<T> Ranges<T>(T value, T[] boundaries) where T : struct
        => throw new NotSupportedException("Bucket.Ranges is only meaningful inside a GroupBy key expression, where it is translated to range buckets. ");
    /// <summary>The start of the calendar interval the date falls in: Bucket.Interval(o.Created, DateInterval.Quarter).</summary>
    public static DateTime Interval(DateTime value, DateInterval interval) => interval switch {
        DateInterval.None => value,
        DateInterval.Year => new DateTime(value.Year, 1, 1, 0, 0, 0, value.Kind),
        DateInterval.Quarter => new DateTime(value.Year, (value.Month - 1) / 3 * 3 + 1, 1, 0, 0, 0, value.Kind),
        DateInterval.Month => new DateTime(value.Year, value.Month, 1, 0, 0, 0, value.Kind),
        DateInterval.Week => value.Date.AddDays(-(((int)value.DayOfWeek + 6) % 7)), // ISO weeks start on Monday
        DateInterval.Day => value.Date,
        DateInterval.Hour => new DateTime(value.Year, value.Month, value.Day, value.Hour, 0, 0, value.Kind),
        _ => throw new NotSupportedException(interval.ToString()),
    };
    public static DateTimeOffset Interval(DateTimeOffset value, DateInterval interval) => new(Interval(value.DateTime, interval), value.Offset);
}

/// <summary>Aggregates on a group beyond what System.Linq has; usable in a GroupBy Select and in memory alike.</summary>
public static class GroupingExtensions {
    /// <summary>The number of distinct values of the selected property among the nodes of the group.</summary>
    public static int CountDistinct<TSource, TValue>(this IEnumerable<TSource> source, Func<TSource, TValue> selector)
        => source.Select(selector).Distinct().Count();
}

/// <summary>
/// A grouping level chosen at runtime, for GroupBy(params GroupKey[]) - the form for dynamic reports
/// and the admin UI, where the properties are not known when the code is written. The key of such a
/// query is an object?[] with one entry per level: the value, or a GroupRange&lt;object&gt; for a
/// calendar or range level.
/// </summary>
public sealed class GroupKey {
    GroupKey(Guid propertyId, bool isRange, DateInterval interval, int bucketCount) {
        PropertyId = propertyId;
        IsRange = isRange;
        DateInterval = interval;
        BucketCount = bucketCount;
    }
    public Guid PropertyId { get; }
    public bool IsRange { get; }
    public DateInterval DateInterval { get; }
    public int BucketCount { get; }
    /// <summary>One group per distinct value.</summary>
    public static GroupKey Values(Guid propertyId) => new(propertyId, false, DateInterval.None, 0);
    /// <summary>A date property by calendar interval.</summary>
    public static GroupKey Interval(Guid propertyId, DateInterval interval) {
        if (interval == DateInterval.None) throw new ArgumentException("Pick a calendar interval, or use GroupKey.Values for one group per value. ", nameof(interval));
        return new(propertyId, true, interval, 0);
    }
    /// <summary>Auto-generated numeric or date ranges (0 = the property's default range count).</summary>
    public static GroupKey Ranges(Guid propertyId, int bucketCount = 0) => new(propertyId, true, DateInterval.None, bucketCount);
}

/// <summary>
/// One group of a GroupBy that has no Select: its key, how many nodes it holds, and the engine's
/// label for it (a related node's display name, an enum name, "2026-03", "(none)"). The measures
/// added with Aggregate(...) are read by name: group["Price.Sum"].
/// </summary>
public sealed class NodeGroup<TKey> {
    public NodeGroup(TKey key, int count, string label, string[] labels, bool isMissing, string[] measureNames, double?[] measureValues) {
        Key = key;
        Count = count;
        Label = label;
        Labels = labels;
        IsMissing = isMissing;
        MeasureNames = measureNames;
        MeasureValues = measureValues;
    }
    public TKey Key { get; }
    public int Count { get; }
    /// <summary>The group's label; with several key properties the labels joined with " / ".</summary>
    public string Label { get; }
    /// <summary>One label per key property.</summary>
    public string[] Labels { get; }
    /// <summary>The group of the nodes that have no value for a key property.</summary>
    public bool IsMissing { get; }
    public string[] MeasureNames { get; }
    /// <summary>Aligned with MeasureNames; null = undefined (a sum, average, min or max over nodes without a value).</summary>
    public double?[] MeasureValues { get; }
    public double? this[string measureName] {
        get {
            for (var i = 0; i < MeasureNames.Length; i++) {
                if (string.Equals(MeasureNames[i], measureName, StringComparison.OrdinalIgnoreCase)) return MeasureValues[i];
            }
            throw new ArgumentException("Unknown measure \"" + measureName + "\". Measures: " + (MeasureNames.Length == 0 ? "(none - add them with Aggregate)" : string.Join(", ", MeasureNames)), nameof(measureName));
        }
    }
    public override string ToString() => Label + " (" + Count + ")";
}

/// <summary>The groups of a GroupBy: a result set that also tells how many nodes were grouped.</summary>
public sealed class ResultSetGroups<T> : ResultSet<T> {
    public ResultSetGroups(IEnumerable<T> values, int count, int totalCount, int pageIndex, int? pageSize, double durationMs, int sourceCount)
        : base(values, count, totalCount, pageIndex, pageSize, durationMs, durationMs) {
        SourceCount = sourceCount;
    }
    /// <summary>The nodes the groups were computed over (a node in several groups of an array-valued key is counted once).</summary>
    public int SourceCount { get; }
}

// ── the queries ─────────────────────────────────────────────────────────────────

/// <summary>
/// The rows of a GroupBy after Select: one per group, shaped by the selector. Immutable like every
/// query: Where (or Having), OrderBy, Skip, Take and Page return a NEW query, so the result must be
/// used. Those operators run over the group rows - a group count is small next to a node count -
/// except that a sort by one measure and the paging behind it are pushed into the engine when they
/// can be. Travels to the store as a one-axis pivot query string (see ToString()).
/// </summary>
public class QueryOfGroupRows<TResult> : IQueryCollection<ResultSet<TResult>> {
    internal readonly GroupByPlan _plan;
    internal readonly RowProjection<TResult> _projection;
    readonly List<LambdaExpression> _filters = [];
    readonly List<(LambdaExpression key, bool descending)> _orders = [];
    int _skip;
    int? _take;
    internal QueryOfGroupRows(GroupByPlan plan, RowProjection<TResult> projection) {
        _plan = plan;
        _projection = projection;
    }
    /// <summary>The copy the immutable operators change before handing it out.</summary>
    internal QueryOfGroupRows(QueryOfGroupRows<TResult> source, GroupByPlan? plan = null) {
        _plan = plan ?? source._plan;
        _projection = source._projection;
        _filters.AddRange(source._filters);
        _orders.AddRange(source._orders);
        _skip = source._skip;
        _take = source._take;
    }
    QueryOfGroupRows<TResult> fork(Action<QueryOfGroupRows<TResult>> change) {
        var c = new QueryOfGroupRows<TResult>(this);
        change(c);
        return c;
    }

    // ── operators over the group rows ──
    /// <summary>Keeps the groups the predicate accepts - SQL's HAVING, written the way EF Core has it.</summary>
    [Pure] public QueryOfGroupRows<TResult> Where(Expression<Func<TResult, bool>> predicate) => fork(c => c._filters.Add(predicate));
    /// <summary>The same as Where, for those who think in SQL.</summary>
    [Pure] public QueryOfGroupRows<TResult> Having(Expression<Func<TResult, bool>> predicate) => Where(predicate);
    /// <summary>Orders the groups. A sort by a single measure (r => r.Revenue, g => g.Count, g => g["Price.Sum"]) is done by the engine; anything else in memory.</summary>
    [Pure] public QueryOfGroupRows<TResult> OrderBy<TKey>(Expression<Func<TResult, TKey>> keySelector) => fork(c => { c._orders.Clear(); c._orders.Add((keySelector, false)); });
    [Pure] public QueryOfGroupRows<TResult> OrderByDescending<TKey>(Expression<Func<TResult, TKey>> keySelector) => fork(c => { c._orders.Clear(); c._orders.Add((keySelector, true)); });
    [Pure] public QueryOfGroupRows<TResult> ThenBy<TKey>(Expression<Func<TResult, TKey>> keySelector) => fork(c => c._orders.Add((keySelector, false)));
    [Pure] public QueryOfGroupRows<TResult> ThenByDescending<TKey>(Expression<Func<TResult, TKey>> keySelector) => fork(c => c._orders.Add((keySelector, true)));
    [Pure] public QueryOfGroupRows<TResult> Skip(int count) {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        return fork(c => c._skip = count);
    }
    [Pure] public QueryOfGroupRows<TResult> Take(int count) {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count), "Take needs a count greater than 0. ");
        return fork(c => c._take = count);
    }
    [Pure] public QueryOfGroupRows<TResult> Page(int pageIndex, int pageSize) {
        if (pageIndex < 0) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be greater than 0. ");
        return fork(c => { c._skip = pageIndex * pageSize; c._take = pageSize; });
    }

    // ── execution ──
    public ResultSetGroups<TResult> Execute() {
        var (spec, engineSorted, enginePaged) = buildSpec();
        var data = _plan.Store.Datastore.Query(GroupByQueryText.Render(_plan.BaseQuery, spec, _plan.Datamodel), _plan.Parameters, _plan.Ctx);
        return materialize(data, engineSorted, enginePaged);
    }
    public async Task<ResultSetGroups<TResult>> ExecuteAsync() {
        var (spec, engineSorted, enginePaged) = buildSpec();
        var data = await _plan.Store.Datastore.QueryAsync(GroupByQueryText.Render(_plan.BaseQuery, spec, _plan.Datamodel), _plan.Parameters, _plan.Ctx);
        return materialize(data, engineSorted, enginePaged);
    }
    ResultSet<TResult> IQueryExecutable<ResultSet<TResult>>.Execute() => Execute();
    async Task<ResultSet<TResult>> IQueryExecutable<ResultSet<TResult>>.ExecuteAsync() => await ExecuteAsync();
    public ResultSet<TResult> Execute(out int totalCount) {
        var result = Execute();
        totalCount = result.TotalCount;
        return result;
    }
    /// <summary>The number of groups (after Where), whatever the paging.</summary>
    public int Count() => Execute().TotalCount;
    public async Task<int> CountAsync() => (await ExecuteAsync()).TotalCount;
    public object? EvaluateForJson() => Execute();
    public async Task<object?> EvaluateForJsonAsync() => await ExecuteAsync();

    (PivotSpec spec, bool engineSorted, bool enginePaged) buildSpec() {
        var spec = new PivotSpec { RowTotals = true, ColumnTotals = false, SubTotals = false, ThrowWhenExceeded = true };
        foreach (var level in _plan.Levels) spec.Rows.Add(level.ToSpec(_plan.IncludeMissing));
        foreach (var m in _plan.Measures) spec.Measures.Add(m.ToSpec());
        // one sort by one measure on a single key: the engine orders the groups (a sort within nested
        // levels would be hierarchical, not flat, so those sort here). No sort at all is the natural
        // bucket order, which the engine gives.
        var engineSorted = _orders.Count == 0;
        if (_orders.Count == 1 && _plan.Levels.Count == 1 && _projection.MeasureOf(_orders[0].key, _plan) is { } measureName) {
            spec.Rows[0].SortByMeasure = measureName;
            spec.Rows[0].Descending = _orders[0].descending;
            engineSorted = true;
        }
        // paging follows the engine's order only when nothing filters between them
        var enginePaged = engineSorted && _filters.Count == 0 && _take is int take && _skip % take == 0;
        if (enginePaged) {
            spec.RowPageIndex = _skip / _take!.Value;
            spec.RowPageSize = _take;
        }
        return (spec, engineSorted, enginePaged);
    }
    ResultSetGroups<TResult> materialize(object? data, bool engineSorted, bool enginePaged) {
        if (data is not PivotQueryResultData pivot)
            throw new NotSupportedException("Only results of type " + nameof(PivotQueryResultData) + " is supported. Type provided: " + data?.GetType().FullName);
        var r = pivot.Result;
        PivotNodeValueMapper.MapNodeDataValues(r, _plan.Store); // relation group values: node data -> typed node objects
        var measureNames = r.Measures.Select(m => m.Name).ToArray();
        var rows = new List<GroupRow>(r.Rows.Groups.Length);
        for (var i = 0; i < r.Rows.Groups.Length; i++) {
            var g = r.Rows.Groups[i];
            rows.Add(new GroupRow {
                Values = g.Values,
                Values2 = g.Values2,
                Labels = g.DisplayNames,
                Label = g.DisplayName,
                Count = g.Count,
                Measures = r.RowTotals[i].Values,
                MeasureNames = measureNames,
                IsMissing = Array.IndexOf(g.Values, null) >= 0,
            });
        }
        var project = _projection.Compiled;
        IEnumerable<TResult> items = rows.Select(project);
        foreach (var filter in _filters) items = items.Where(((Expression<Func<TResult, bool>>)filter).Compile());
        if (!engineSorted && _orders.Count > 0) {
            var keys = _orders.Select(o => compileKey(o.key)).ToArray();
            var ordered = _orders[0].descending ? items.OrderByDescending(keys[0], Comparer<object?>.Default) : items.OrderBy(keys[0], Comparer<object?>.Default);
            for (var i = 1; i < keys.Length; i++) ordered = _orders[i].descending ? ordered.ThenByDescending(keys[i], Comparer<object?>.Default) : ordered.ThenBy(keys[i], Comparer<object?>.Default);
            items = ordered;
        }
        var list = items.ToList();
        var total = enginePaged ? r.Rows.TotalGroupCount : list.Count;
        if (!enginePaged) {
            if (_skip > 0) list = list.Skip(_skip).ToList();
            if (_take is int take) list = list.Take(take).ToList();
        }
        var pageIndex = _take is int size ? _skip / size : 0;
        return new ResultSetGroups<TResult>(list, list.Count, total, pageIndex, _take, r.DurationMs, r.SourceCount);
    }
    static Func<TResult, object?> compileKey(LambdaExpression key)
        => Expression.Lambda<Func<TResult, object?>>(Expression.Convert(key.Body, typeof(object)), key.Parameters).Compile();

    /// <summary>The query string the store runs: a pivot with one axis, in its GroupBy spelling.</summary>
    public override string ToString() => GroupByQueryText.Render(_plan.BaseQuery, buildSpec().spec, _plan.Datamodel);
}

/// <summary>
/// A GroupBy before (or without) a Select: the groups themselves, as <see cref="NodeGroup{TKey}"/>
/// with the key, the node count and a label. Select(g => new { g.Key, Total = g.Sum(x => x.Price) })
/// shapes the groups the way EF Core does; Aggregate(...) adds measures read by name instead, for
/// code that does not know the properties at compile time.
/// </summary>
public sealed class QueryOfGroups<T, TInclude, TKey> : QueryOfGroupRows<NodeGroup<TKey>> {
    readonly Expression _keyBody;         // the key over a GroupRow
    readonly ParameterExpression _row;
    internal QueryOfGroups(GroupByPlan plan, Expression keyBody, ParameterExpression row) : base(plan, nodeGroupProjection(keyBody, row)) {
        _keyBody = keyBody;
        _row = row;
    }
    QueryOfGroups(QueryOfGroups<T, TInclude, TKey> source, GroupByPlan plan) : base(source, plan) {
        _keyBody = source._keyBody;
        _row = source._row;
    }
    internal static QueryOfGroups<T, TInclude, TKey> Create(QueryOfNodes<T, TInclude> query, Expression<Func<T, TKey>> keySelector) {
        var plan = new GroupByPlan(query.Store, query.ToString(), query._q._parameters, query._q._ctx);
        var row = Expression.Parameter(typeof(GroupRow), "row");
        return new(plan, KeyTranslator.Translate(plan, keySelector, row), row);
    }
    internal static QueryOfGroups<T, TInclude, TKey> Create(QueryOfFacets<T, TInclude> facetQuery, Expression<Func<T, TKey>> keySelector) {
        var q = facetQuery.Query;
        var plan = new GroupByPlan(q.Store, facetQuery.ToQueryString(includePaging: false), q._q._parameters, q._q._ctx); // the selection filters, the page does not
        var row = Expression.Parameter(typeof(GroupRow), "row");
        return new(plan, KeyTranslator.Translate(plan, keySelector, row), row);
    }
    internal static QueryOfGroups<T, TInclude, object?[]> Create(QueryOfNodes<T, TInclude> query, GroupKey[] keys) {
        var plan = new GroupByPlan(query.Store, query.ToString(), query._q._parameters, query._q._ctx);
        var row = Expression.Parameter(typeof(GroupRow), "row");
        return new(plan, KeyTranslator.Translate(plan, keys, row), row);
    }
    internal static QueryOfGroups<T, TInclude, object?[]> Create(QueryOfFacets<T, TInclude> facetQuery, GroupKey[] keys) {
        var q = facetQuery.Query;
        var plan = new GroupByPlan(q.Store, facetQuery.ToQueryString(includePaging: false), q._q._parameters, q._q._ctx);
        var row = Expression.Parameter(typeof(GroupRow), "row");
        return new(plan, KeyTranslator.Translate(plan, keys, row), row);
    }
    // NodeGroup rows are built the way an anonymous type is - a constructor call with members - so a
    // later OrderBy(g => g.Count) can be traced back to the count the same way a Select member is
    static RowProjection<NodeGroup<TKey>> nodeGroupProjection(Expression keyBody, ParameterExpression row) {
        var markers = new Dictionary<Expression, string>(ReferenceEqualityComparer.Instance);
        var count = Expression.Field(row, nameof(GroupRow.Count));
        markers[count] = "Count";
        var type = typeof(NodeGroup<TKey>);
        var body = Expression.New(type.GetConstructors()[0],
            [keyBody, count, Expression.Field(row, nameof(GroupRow.Label)), Expression.Field(row, nameof(GroupRow.Labels)), Expression.Field(row, nameof(GroupRow.IsMissing)),
             Expression.Field(row, nameof(GroupRow.MeasureNames)), Expression.Field(row, nameof(GroupRow.Measures))],
            [type.GetProperty(nameof(NodeGroup<TKey>.Key))!, type.GetProperty(nameof(NodeGroup<TKey>.Count))!, type.GetProperty(nameof(NodeGroup<TKey>.Label))!, type.GetProperty(nameof(NodeGroup<TKey>.Labels))!,
             type.GetProperty(nameof(NodeGroup<TKey>.IsMissing))!, type.GetProperty(nameof(NodeGroup<TKey>.MeasureNames))!, type.GetProperty(nameof(NodeGroup<TKey>.MeasureValues))!]);
        return new RowProjection<NodeGroup<TKey>>(row, body, markers);
    }

    /// <summary>
    /// Shapes every group the way EF Core does: g.Key (or g.Key.Member) and the aggregates g.Count(),
    /// g.Sum(x => x.P), g.Average, g.Min, g.Max and g.CountDistinct(x => x.P) - each selector a property
    /// of the node. The nodes of a group cannot be enumerated; anything else on g is rejected when the
    /// query is built. The rest of the expression (arithmetic, formatting) runs on the result rows.
    /// </summary>
    [Pure] public QueryOfGroupRows<TResult> Select<TResult>(Expression<Func<IGrouping<TKey, T>, TResult>> selector) {
        var plan = _plan.Clone();
        return new QueryOfGroupRows<TResult>(plan, SelectTranslator.Translate<TResult>(plan, selector, _keyBody, _row));
    }
    /// <summary>Adds a measure the groups carry by name: group["Price.Sum"] (Count needs no measure: group.Count).</summary>
    [Pure] public QueryOfGroups<T, TInclude, TKey> Aggregate(PivotFunction function, Expression<Func<T, object?>> property) => Aggregate(function, _plan.Store.Mapper.GetProperty<T>(property).Id);
    [Pure] public QueryOfGroups<T, TInclude, TKey> Aggregate(PivotFunction function, string propertyName) => Aggregate(function, _plan.Store.Mapper.GetProperty<T>(propertyName).Id);
    [Pure] public QueryOfGroups<T, TInclude, TKey> Aggregate(PivotFunction function, Guid propertyId) {
        if (function == PivotFunction.Count) return this; // always there, as Count
        var plan = _plan.Clone();
        var property = plan.Property(propertyId);
        GroupByPlan.EnsureAggregatable(property, function);
        plan.MeasureIndex(function, property);
        return new(this, plan);
    }
    /// <summary>
    /// Whether the nodes without a value for a key property form a group of their own (default true,
    /// as in SQL and LINQ: null is a key). Its Key is null / default, its Label "(none)".
    /// </summary>
    [Pure] public QueryOfGroups<T, TInclude, TKey> IncludeMissing(bool include) {
        var plan = _plan.Clone();
        plan.IncludeMissing = include;
        return new(this, plan);
    }
}

// ── internals ───────────────────────────────────────────────────────────────────

/// <summary>One group as it comes back from the engine, before the projection shapes it.</summary>
internal sealed class GroupRow {
    public object?[] Values = [];
    public object?[] Values2 = [];
    public string[] Labels = [];
    public string Label = "";
    public int Count;
    public double?[] Measures = [];
    public string[] MeasureNames = [];
    public bool IsMissing;

    internal static readonly MethodInfo CoerceMethod = typeof(GroupRow).GetMethod(nameof(Coerce))!;
    internal static readonly MethodInfo MeasureMethod = typeof(GroupRow).GetMethod(nameof(Measure))!;
    /// <summary>A bucket value as the type the key expression declares: enum ints to enums, nulls to defaults, a single array element to a one-element array.</summary>
    public static object? Coerce(object? value, Type target) {
        if (value == null) return target.IsValueType && Nullable.GetUnderlyingType(target) == null ? Activator.CreateInstance(target) : null;
        var t = Nullable.GetUnderlyingType(target) ?? target;
        if (t.IsInstanceOfType(value)) return value;
        if (t.IsEnum) return Enum.ToObject(t, Convert.ChangeType(value, Enum.GetUnderlyingType(t), CultureInfo.InvariantCulture));
        if (t.IsArray) { // an array-valued key property: the bucket is ONE element of it
            var elementType = t.GetElementType()!;
            var array = Array.CreateInstance(elementType, 1);
            array.SetValue(Coerce(value, elementType), 0);
            return array;
        }
        if (t == typeof(DateTime) && value is DateTimeOffset dto) return dto.DateTime;
        if (t == typeof(DateTimeOffset) && value is DateTime dt) return new DateTimeOffset(dt);
        if (t == typeof(string)) return value.ToString();
        if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(t)) return Convert.ChangeType(value, t, CultureInfo.InvariantCulture);
        throw new InvalidCastException("A group value of type " + value.GetType().Name + " cannot be read as " + target.Name + ". ");
    }
    /// <summary>A measure as the type the aggregate call has: null (no node has a value) becomes 0 for a non-nullable result, as LINQ's Sum over nothing.</summary>
    public static object? Measure(double? value, Type target) {
        var underlying = Nullable.GetUnderlyingType(target);
        if (value == null) return underlying != null ? null : Convert.ChangeType(0, target, CultureInfo.InvariantCulture);
        return Convert.ChangeType(value.Value, underlying ?? target, CultureInfo.InvariantCulture);
    }
}

/// <summary>What one key property is bucketed by.</summary>
internal sealed class GroupLevel(PropertyModel property) {
    public PropertyModel Property = property;
    public bool IsRange;             // false: one group per distinct value (SQL's GROUP BY)
    public DateInterval Interval;
    public int BucketCount;
    public object[]? Boundaries;     // explicit consecutive ranges
    public bool SameAs(GroupLevel o) => Property.Id == o.Property.Id && IsRange == o.IsRange && Interval == o.Interval && BucketCount == o.BucketCount
        && (Boundaries == null ? o.Boundaries == null : o.Boundaries != null && Boundaries.SequenceEqual(o.Boundaries));
    public PivotGroupSpec ToSpec(bool includeMissing) {
        var spec = new PivotGroupSpec(Property.Id) { IsRange = IsRange, Interval = Interval, BucketCount = BucketCount, IncludeMissing = includeMissing };
        if (Boundaries != null) for (var i = 0; i < Boundaries.Length - 1; i++) spec.Values.Add(new FacetValue(Boundaries[i], Boundaries[i + 1], null));
        return spec;
    }
}
internal sealed class GroupMeasure(PivotFunction function, PropertyModel? property) {
    public PivotFunction Function = function;
    public PropertyModel? Property = property;
    public string Name => Property == null ? "Count" : Property.CodeName + "." + Function; // the engine's default name
    public PivotMeasureSpec ToSpec() => new(Function, Property?.Id ?? Guid.Empty, null);
}
/// <summary>Everything a GroupBy sends to the store, minus the ordering and paging the rows query adds.</summary>
internal sealed class GroupByPlan(NodeStore store, string baseQuery, List<Parameter> parameters, QueryContext? ctx) {
    public readonly NodeStore Store = store;
    public readonly string BaseQuery = baseQuery;
    public readonly List<Parameter> Parameters = parameters;
    public readonly QueryContext? Ctx = ctx;
    public readonly List<GroupLevel> Levels = [];
    public readonly List<GroupMeasure> Measures = [];
    public bool IncludeMissing = true;
    public Datamodel Datamodel => Store.Datastore.Datamodel;
    public GroupByPlan Clone() {
        var c = new GroupByPlan(Store, BaseQuery, Parameters, Ctx) { IncludeMissing = IncludeMissing };
        c.Levels.AddRange(Levels);
        c.Measures.AddRange(Measures);
        return c;
    }
    /// <summary>The index of the level, added when new. The same property bucketed the same way is one level however often the key names it.</summary>
    public int LevelIndex(GroupLevel level) {
        var i = Levels.FindIndex(l => l.SameAs(level));
        if (i >= 0) return i;
        Levels.Add(level);
        return Levels.Count - 1;
    }
    public int MeasureIndex(PivotFunction function, PropertyModel? property) {
        var i = Measures.FindIndex(m => m.Function == function && m.Property?.Id == property?.Id);
        if (i >= 0) return i;
        Measures.Add(new GroupMeasure(function, property));
        return Measures.Count - 1;
    }
    public PropertyModel Property(Type nodeType, string name) {
        var typeId = Store.Mapper.GetNodeTypeId(nodeType);
        var type = Datamodel.NodeTypes[typeId];
        if (!type.AllPropertiesByName.TryGetValue(name, out var property))
            throw new ArgumentException("\"" + name + "\" is not a property of " + type.CodeName + " in the datamodel; only node properties can be grouped or aggregated. ");
        return property;
    }
    public PropertyModel Property(Guid propertyId) => Datamodel.Properties.TryGetValue(propertyId, out var p) ? p : throw new ArgumentException("Unknown property " + propertyId + ". ");
    public static bool IsDate(PropertyModel p) => p.PropertyType is PropertyType.DateTime or PropertyType.DateTimeOffset;
    /// <summary>The facet rules decide what can be grouped, so the error names the fix at build time rather than at the store.</summary>
    public static void EnsureGroupable(PropertyModel p) {
        if (p is RelationPropertyModel relation) {
            if (!relation.Facet) throw new NotSupportedException("Cannot group by the relation \"" + p.CodeName + "\": relation properties opt in with [RelationProperty(Facet = true)]. ");
            return;
        }
        if (!p.Indexed) throw new NotSupportedException("Cannot group by \"" + p.CodeName + "\": it is not indexed. Grouping and aggregation are answered from the value indexes; add Indexed = true to its property attribute, or load the nodes and group them in memory. ");
        if (p.NotFacet) throw new NotSupportedException("Cannot group by \"" + p.CodeName + "\": it is marked NotFacet. ");
    }
    public static void EnsureAggregatable(PropertyModel p, PivotFunction function) {
        if (p is RelationPropertyModel) throw new NotSupportedException("Cannot compute " + function + " of the relation \"" + p.CodeName + "\": aggregates need a scalar value property. ");
        if (!p.Indexed) throw new NotSupportedException("Cannot compute " + function + " of \"" + p.CodeName + "\": it is not indexed. Aggregates are answered from the value indexes; add Indexed = true to its property attribute, or load the nodes and aggregate them in memory. ");
    }
}

/// <summary>A compiled shape of one group row, remembering which of its parts are plain measure reads (for the engine sort).</summary>
internal sealed class RowProjection<TResult>(ParameterExpression row, Expression body, Dictionary<Expression, string> measureMarkers) {
    Func<GroupRow, TResult>? _compiled;
    public Expression Body => body;
    public Func<GroupRow, TResult> Compiled => _compiled ??= Expression.Lambda<Func<GroupRow, TResult>>(body.Type == typeof(TResult) ? body : Expression.Convert(body, typeof(TResult)), row).Compile();
    /// <summary>The measure an order key reads, when it is nothing but that: r => r.Member assigned from one aggregate, g => g.Count, g => g["name"].</summary>
    public string? MeasureOf(LambdaExpression orderKey, GroupByPlan plan) {
        var parameter = orderKey.Parameters[0];
        var e = ExpressionUtil.Strip(orderKey.Body);
        if (e is MemberExpression m && ExpressionUtil.Strip(m.Expression) == parameter) {
            if (body is NewExpression ne && ne.Members != null) {
                for (var i = 0; i < ne.Members.Count; i++) {
                    if (ne.Members[i].Name == m.Member.Name && measureMarkers.TryGetValue(ne.Arguments[i], out var name)) return name;
                }
            }
            return null;
        }
        if (e is MethodCallExpression call && call.Method.Name == "get_Item" && ExpressionUtil.Strip(call.Object) == parameter
            && call.Arguments.Count == 1 && call.Arguments[0].Type == typeof(string) && !ExpressionUtil.References(call.Arguments[0], parameter)) {
            var name = (string?)Expression.Lambda(call.Arguments[0]).Compile().DynamicInvoke();
            if (name == null) return null;
            if (string.Equals(name, "Count", StringComparison.OrdinalIgnoreCase)) return "Count";
            return plan.Measures.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))?.Name; // unknown: the in-memory sort reports it
        }
        return null;
    }
}

internal static class ExpressionUtil {
    public static Expression? Strip(Expression? e) {
        while (e is UnaryExpression u && u.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs or ExpressionType.Quote) e = u.Operand;
        return e;
    }
    public static bool References(Expression e, ParameterExpression parameter) {
        var finder = new ParameterFinder(parameter);
        finder.Visit(e);
        return finder.Found;
    }
    sealed class ParameterFinder(ParameterExpression parameter) : ExpressionVisitor {
        public bool Found;
        protected override Expression VisitParameter(ParameterExpression node) {
            if (node == parameter) Found = true;
            return node;
        }
    }
    public static Expression ValueAt(ParameterExpression row, string field, int index) => Expression.ArrayIndex(Expression.Field(row, field), Expression.Constant(index));
    public static Expression Coerced(ParameterExpression row, string field, int index, Type type)
        => Expression.Convert(Expression.Call(GroupRow.CoerceMethod, ValueAt(row, field, index), Expression.Constant(type, typeof(Type))), type);
    public static Expression Range(ParameterExpression row, int index, Type valueType) {
        var rangeType = typeof(GroupRange<>).MakeGenericType(valueType);
        return Expression.New(rangeType.GetConstructors()[0],
            Coerced(row, nameof(GroupRow.Values), index, valueType), Coerced(row, nameof(GroupRow.Values2), index, valueType), ValueAt(row, nameof(GroupRow.Labels), index));
    }
    /// <summary>A constant or closure argument, evaluated now.</summary>
    public static object? Evaluate(Expression e) => e is ConstantExpression c ? c.Value : Expression.Lambda(e).Compile().DynamicInvoke();
}

/// <summary>
/// Turns the key selector into the grouping levels and an expression that rebuilds the key from a
/// group row. Only exact forms are accepted - a node property, a date part of one (x.Created.Year),
/// a Bucket call, and anonymous types / constructors of those - because anything computed from a
/// property (x.Price / 100) would group by the stored value and then show duplicate keys.
/// </summary>
internal sealed class KeyTranslator(GroupByPlan plan, ParameterExpression node, ParameterExpression row) {
    public static Expression Translate(GroupByPlan plan, LambdaExpression keySelector, ParameterExpression row)
        => new KeyTranslator(plan, keySelector.Parameters[0], row).translate(keySelector.Body);
    public static Expression Translate(GroupByPlan plan, GroupKey[] keys, ParameterExpression row) {
        if (keys == null || keys.Length == 0) throw new ArgumentException("GroupBy needs at least one key. ", nameof(keys));
        var parts = new List<Expression>(keys.Length);
        foreach (var key in keys) {
            var property = plan.Property(key.PropertyId);
            GroupByPlan.EnsureGroupable(property);
            if (key.DateInterval != DateInterval.None && !GroupByPlan.IsDate(property))
                throw new ArgumentException("A calendar interval needs a DateTime or DateTimeOffset property; \"" + property.CodeName + "\" is " + property.PropertyType + ". ");
            var index = plan.LevelIndex(new GroupLevel(property) { IsRange = key.IsRange, Interval = key.DateInterval, BucketCount = key.BucketCount });
            parts.Add(key.IsRange ? ExpressionUtil.Range(row, index, typeof(object)) : ExpressionUtil.ValueAt(row, nameof(GroupRow.Values), index));
        }
        return Expression.NewArrayInit(typeof(object), parts.Select(p => p.Type == typeof(object) ? p : Expression.Convert(p, typeof(object))));
    }

    Expression translate(Expression e) {
        switch (e) {
            case UnaryExpression u when u.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked && ExpressionUtil.Strip(u.Operand) != node:
                return Expression.Convert(translate(u.Operand), u.Type);
            case NewExpression ne:
                return ne.Update(ne.Arguments.Select(translate));
            case MemberInitExpression mi:
                return mi.Update((NewExpression)translate(mi.NewExpression), mi.Bindings.Select(b => b is MemberAssignment a ? a.Update(translate(a.Expression))
                    : throw new NotSupportedException("Only member assignments are supported in a GroupBy key initializer: " + b)));
            case MemberExpression m when tryNodeProperty(m, out var property): { // x.Brand: one group per value
                GroupByPlan.EnsureGroupable(property);
                var index = plan.LevelIndex(new GroupLevel(property));
                return ExpressionUtil.Coerced(row, nameof(GroupRow.Values), index, m.Type);
            }
            case MemberExpression { Expression: MemberExpression inner } m when tryNodeProperty(inner, out var dateProperty) && GroupByPlan.IsDate(dateProperty) && datePart(m.Member.Name) is { } interval: {
                // x.Created.Year: a calendar level, and the part read off the bucket's start
                GroupByPlan.EnsureGroupable(dateProperty);
                var index = plan.LevelIndex(new GroupLevel(dateProperty) { IsRange = true, Interval = interval });
                return Expression.MakeMemberAccess(ExpressionUtil.Coerced(row, nameof(GroupRow.Values), index, inner.Type), m.Member);
            }
            case MethodCallExpression call when call.Method.DeclaringType == typeof(Bucket):
                return bucket(call);
            default:
                if (!ExpressionUtil.References(e, node)) return e; // a constant, or something of the caller's own
                throw new NotSupportedException("A GroupBy key must be built from properties of the node - x => x.Brand, x => new { x.Brand, x.Created.Year }, x => Bucket.Ranges(x.Price, 5) - so every group is one stored value. \"" + e + "\" cannot be translated; compute it from the key on the result instead. ");
        }
    }
    Expression bucket(MethodCallExpression call) {
        if (call.Arguments[0] is not MemberExpression m || !tryNodeProperty(m, out var property))
            throw new NotSupportedException("The first argument of Bucket." + call.Method.Name + " must be a property of the node, like Bucket.Ranges(x.Price, 5). ");
        GroupByPlan.EnsureGroupable(property);
        switch (call.Method.Name) {
            case nameof(Bucket.Interval): {
                var interval = (DateInterval)ExpressionUtil.Evaluate(call.Arguments[1])!;
                if (!GroupByPlan.IsDate(property)) throw new NotSupportedException("Bucket.Interval needs a DateTime or DateTimeOffset property; \"" + property.CodeName + "\" is " + property.PropertyType + ". ");
                if (interval == DateInterval.None) throw new ArgumentException("Bucket.Interval needs a calendar interval. ");
                var index = plan.LevelIndex(new GroupLevel(property) { IsRange = true, Interval = interval });
                // floored again on the way out: right even when the same property is bucketed finer elsewhere in the key
                return Expression.Call(call.Method, ExpressionUtil.Coerced(row, nameof(GroupRow.Values), index, m.Type), Expression.Constant(interval));
            }
            case nameof(Bucket.Ranges): {
                var valueType = call.Method.GetGenericArguments()[0];
                var argument = ExpressionUtil.Evaluate(call.Arguments[1]);
                GroupLevel level;
                if (argument is int bucketCount) {
                    if (bucketCount < 0) throw new ArgumentOutOfRangeException("bucketCount", "Bucket.Ranges needs a bucket count of 0 (the property's default) or more. ");
                    level = new GroupLevel(property) { IsRange = true, BucketCount = bucketCount };
                } else if (argument is Array boundaries) {
                    if (boundaries.Length < 2) throw new ArgumentException("Bucket.Ranges needs at least two boundaries. ");
                    level = new GroupLevel(property) { IsRange = true, Boundaries = boundaries.Cast<object>().ToArray() };
                } else throw new NotSupportedException("Bucket.Ranges takes a bucket count or an array of boundaries. ");
                return ExpressionUtil.Range(row, plan.LevelIndex(level), valueType);
            }
            default:
                throw new NotSupportedException("Bucket." + call.Method.Name + " is not supported in a GroupBy key. ");
        }
    }
    // x.Prop, or ((Sub)x).Prop: a property of the node, resolved on the type it is read from
    bool tryNodeProperty(MemberExpression m, out PropertyModel property) {
        property = null!;
        if (m.Expression == null || ExpressionUtil.Strip(m.Expression) != node) return false;
        property = plan.Property(m.Expression.Type, m.Member.Name);
        return true;
    }
    static DateInterval? datePart(string member) => member switch {
        nameof(DateTime.Year) => DateInterval.Year,
        nameof(DateTime.Month) => DateInterval.Month,
        nameof(DateTime.Day) or nameof(DateTime.Date) or nameof(DateTime.DayOfWeek) or nameof(DateTime.DayOfYear) => DateInterval.Day,
        nameof(DateTime.Hour) => DateInterval.Hour,
        _ => null,
    };
}

/// <summary>
/// Rewrites the Select lambda over IGrouping into one over a group row: g.Key becomes the key
/// expression, each aggregate call a measure of the plan read from the row, and any other use of g
/// an error. What is left of the lambda runs on the result rows as written.
/// </summary>
internal sealed class SelectTranslator(GroupByPlan plan, ParameterExpression grouping, ParameterExpression row, Expression keyBody) : ExpressionVisitor {
    readonly Dictionary<Expression, string> _markers = new(ReferenceEqualityComparer.Instance);
    public static RowProjection<TResult> Translate<TResult>(GroupByPlan plan, LambdaExpression selector, Expression keyBody, ParameterExpression row) {
        var translator = new SelectTranslator(plan, selector.Parameters[0], row, keyBody);
        var body = translator.Visit(selector.Body);
        return new RowProjection<TResult>(row, body, translator._markers);
    }
    bool isKey(Expression? e) => e is MemberExpression m && m.Member.Name == nameof(IGrouping<int, int>.Key) && ExpressionUtil.Strip(m.Expression) == grouping;
    protected override Expression VisitMember(MemberExpression node) {
        // g.Key.Brand on an anonymous key: straight to that part of the key, no key object built
        if (isKey(node.Expression) && keyBody is NewExpression ne && ne.Members != null) {
            for (var i = 0; i < ne.Members.Count; i++) if (ne.Members[i].Name == node.Member.Name) return ne.Arguments[i];
        }
        if (isKey(node)) return keyBody;
        return base.VisitMember(node);
    }
    protected override Expression VisitMethodCall(MethodCallExpression node) {
        var declaring = node.Method.DeclaringType;
        if ((declaring == typeof(Enumerable) || declaring == typeof(GroupingExtensions)) && node.Arguments.Count >= 1) {
            var source = ExpressionUtil.Strip(node.Arguments[0]);
            if (source == grouping) {
                switch (node.Method.Name) {
                    case nameof(Enumerable.Count):
                    case nameof(Enumerable.LongCount):
                        if (node.Arguments.Count > 1) throw new NotSupportedException("A filtered count (g.Count(x => ...)) is not supported: filter the nodes with Where before GroupBy, or group by the condition. ");
                        return count(node.Type);
                    case nameof(Enumerable.Sum): return measure(PivotFunction.Sum, node);
                    case nameof(Enumerable.Average): return measure(PivotFunction.Average, node);
                    case nameof(Enumerable.Min): return measure(PivotFunction.Min, node);
                    case nameof(Enumerable.Max): return measure(PivotFunction.Max, node);
                    case nameof(GroupingExtensions.CountDistinct): return measure(PivotFunction.CountDistinct, node);
                }
            }
            // g.Select(x => x.P).Distinct().Count(), the LINQ spelling of a distinct count
            if (node.Method.Name == nameof(Enumerable.Count) && node.Arguments.Count == 1
                && source is MethodCallExpression distinct && distinct.Method.Name == nameof(Enumerable.Distinct) && distinct.Method.DeclaringType == typeof(Enumerable) && distinct.Arguments.Count == 1
                && ExpressionUtil.Strip(distinct.Arguments[0]) is MethodCallExpression select && select.Method.Name == nameof(Enumerable.Select) && select.Method.DeclaringType == typeof(Enumerable)
                && ExpressionUtil.Strip(select.Arguments[0]) == grouping && select.Arguments[1] is LambdaExpression selector) {
                return measure(PivotFunction.CountDistinct, propertyOf(selector, "Select"), node.Type);
            }
        }
        return base.VisitMethodCall(node);
    }
    protected override Expression VisitParameter(ParameterExpression node) {
        if (node == grouping)
            throw new NotSupportedException("Inside a GroupBy Select only g.Key and the aggregates g.Count(), g.Sum(x => x.P), g.Average(x => x.P), g.Min(x => x.P), g.Max(x => x.P) and g.CountDistinct(x => x.P) are supported; the nodes of a group cannot be enumerated. Load the nodes with a normal query if you need them. ");
        return base.VisitParameter(node);
    }
    Expression count(Type type) {
        Expression e = Expression.Field(row, nameof(GroupRow.Count));
        if (type != typeof(int)) e = Expression.Convert(e, type);
        _markers[e] = "Count";
        return e;
    }
    Expression measure(PivotFunction function, MethodCallExpression call) {
        if (call.Arguments.Count != 2 || call.Arguments[1] is not LambdaExpression selector)
            throw new NotSupportedException("g." + call.Method.Name + " needs a property selector, like g." + call.Method.Name + "(x => x.Price). ");
        return measure(function, propertyOf(selector, call.Method.Name), call.Type);
    }
    Expression measure(PivotFunction function, PropertyModel property, Type type) {
        GroupByPlan.EnsureAggregatable(property, function);
        var index = plan.MeasureIndex(function, property);
        var e = Expression.Convert(Expression.Call(GroupRow.MeasureMethod, ExpressionUtil.ValueAt(row, nameof(GroupRow.Measures), index), Expression.Constant(type, typeof(Type))), type);
        _markers[e] = plan.Measures[index].Name;
        return e;
    }
    PropertyModel propertyOf(LambdaExpression selector, string method) {
        var parameter = selector.Parameters[0];
        if (ExpressionUtil.Strip(selector.Body) is MemberExpression m && m.Expression != null && ExpressionUtil.Strip(m.Expression) == parameter)
            return plan.Property(m.Expression.Type, m.Member.Name);
        throw new NotSupportedException("The selector of g." + method + " must be a property of the node, like x => x.Price; \"" + selector.Body + "\" cannot be aggregated by the engine. ");
    }
}

/// <summary>
/// The query string of a GroupBy: the pivot clauses the store understands, in the GroupBy spelling
/// the parser also accepts. GroupBy(p1, p2) opens a one-axis pivot with one value level per property
/// (each with a missing-value group), row totals only and a hard cell limit; levels bucketed
/// otherwise follow as AddRow clauses, then the measures, then whatever differs from those defaults.
/// </summary>
internal static class GroupByQueryText {
    public static string Render(string baseQuery, PivotSpec spec, Datamodel dm) {
        string pn(Guid propertyId) => "\"" + propertyId + "|" + dm.Properties[propertyId].CodeName + "\"";
        static string b(bool v) => v ? "true" : "false";
        var sb = new StringBuilder(baseQuery).Append(".GroupBy(");
        var plain = 0;
        for (; plain < spec.Rows.Count && isPlainValues(spec.Rows[plain]); plain++) {
            if (plain > 0) sb.Append(", ");
            sb.Append(pn(spec.Rows[plain].PropertyId));
        }
        sb.Append(')');
        for (var i = 0; i < plain; i++) appendOptions(sb, spec.Rows[i], defaultIncludeMissing: true, pn);
        for (var i = plain; i < spec.Rows.Count; i++) {
            var l = spec.Rows[i];
            if (l.Interval != DateInterval.None) {
                sb.Append(".AddRow(").Append(pn(l.PropertyId)).Append(", ").Append(l.Interval.ToString().ToStringLiteral()).Append(')');
            } else if (l.Values.Count > 0) {
                foreach (var v in l.Values) {
                    sb.Append(".AddRowRange(").Append(pn(l.PropertyId)).Append(", ").Append(QueryOfFacets<object, object>.ValueToString(v.Value!)).Append(", ").Append(QueryOfFacets<object, object>.ValueToString(v.Value2!)).Append(')');
                }
            } else if (l.IsRange == true) {
                sb.Append(".AddRowRanges(").Append(pn(l.PropertyId));
                if (l.BucketCount > 0) sb.Append(", ").Append(l.BucketCount);
                sb.Append(')');
            } else {
                sb.Append(".AddRowValues(").Append(pn(l.PropertyId)).Append(')');
            }
            appendOptions(sb, l, defaultIncludeMissing: false, pn); // right after its level: the options bind to the last level on the property
        }
        foreach (var m in spec.Measures) {
            if (m.Function == PivotFunction.Count) {
                sb.Append(".AddCount(");
                if (m.Name != null) sb.Append(m.Name.ToStringLiteral());
                sb.Append(')');
                continue;
            }
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
        }
        if (!spec.RowTotals || spec.ColumnTotals || spec.SubTotals)
            sb.Append(".SetTotals(").Append(b(spec.RowTotals)).Append(", ").Append(b(spec.ColumnTotals)).Append(", ").Append(b(spec.SubTotals)).Append(')');
        if (spec.MaxCells != PivotSpec.DefaultMaxCells || !spec.ThrowWhenExceeded)
            sb.Append(".SetLimits(").Append(spec.MaxCells).Append(", ").Append(b(spec.ThrowWhenExceeded)).Append(')');
        if (spec.RowPageSize.HasValue)
            sb.Append(".SetRowPaging(").Append(spec.RowPageIndex).Append(", ").Append(spec.RowPageSize.Value).Append(')');
        return sb.ToString();
    }
    static bool isPlainValues(PivotGroupSpec l) => l.IsRange == false && l.Interval == DateInterval.None && l.Values.Count == 0;
    static void appendOptions(StringBuilder sb, PivotGroupSpec l, bool defaultIncludeMissing, Func<Guid, string> pn) {
        if (l.IncludeMissing == defaultIncludeMissing && l.SortByMeasure == null && l.MaxGroups == 0 && l.MinCount == 0 && !l.OtherGroup) return;
        sb.Append(".SetRowOptions(").Append(pn(l.PropertyId)).Append(", ").Append(l.MaxGroups).Append(", ").Append(l.MinCount).Append(", ").Append(l.IncludeMissing ? "true" : "false").Append(", ");
        sb.Append((l.SortByMeasure ?? "").ToStringLiteral()).Append(", ").Append(l.Descending ? "true" : "false").Append(", ").Append(l.OtherGroup ? "true" : "false").Append(')');
    }
}
