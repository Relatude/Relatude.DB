using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;

namespace Relatude.DB.Query.Methods;

/// <summary>
/// The store-side pivot clause. Built up by the parsed method chain (Pivot().AddRow(...).AddSum(...)...)
/// exactly like <see cref="FacetMethod"/>, then evaluated against the node collection it was chained
/// onto. Chained onto a facet clause it pivots the nodes the facet SELECTION leaves - the buckets
/// of that facet clause are not counted, only its filter is applied.
/// </summary>
public class PivotMethod : IExpression {
    readonly IExpression _input;
    readonly Datamodel _dm;
    public PivotSpec Spec { get; } = new();
    public PivotMethod(IExpression input, Datamodel dm) {
        _input = input;
        _dm = dm;
    }
    Guid propertyId(string idString) {
        var id = _dm.GetPropertyGuid(idString);
        if (!_dm.Properties.ContainsKey(id)) throw new Exception("Unknown property \"" + idString + "\". ");
        return id;
    }
    List<PivotGroupSpec> axis(bool rows) => rows ? Spec.Rows : Spec.Columns;
    static string axisName(bool rows) => rows ? "row" : "column";

    // ── grouping ──
    public void AddGroup(bool rows, string property, bool? isRange, DateInterval interval, int bucketCount) {
        var id = propertyId(property);
        if (interval != DateInterval.None) {
            var type = _dm.Properties[id].PropertyType;
            if (type != Datamodels.Properties.PropertyType.DateTime && type != Datamodels.Properties.PropertyType.DateTimeOffset)
                throw new Exception("A date interval can only be used on a DateTime or DateTimeOffset property. \"" + _dm.Properties[id].CodeName + "\" is " + type + ". ");
            isRange = true;
        }
        axis(rows).Add(new PivotGroupSpec(id) { IsRange = isRange, Interval = interval, BucketCount = bucketCount });
    }
    /// <summary>
    /// Adds one explicit range bucket. Consecutive ranges on the same property build one level;
    /// a range on a property that has no level on the axis yet opens a new level.
    /// </summary>
    public void AddRange(bool rows, string property, object from, object to, string? displayName) {
        var id = propertyId(property);
        var level = axis(rows).LastOrDefault(l => l.PropertyId == id && l.IsRange == true && l.Interval == DateInterval.None);
        if (level == null) {
            level = new PivotGroupSpec(id) { IsRange = true };
            axis(rows).Add(level);
        }
        level.Values.Add(new FacetValue(from, to, string.IsNullOrEmpty(displayName) ? null : displayName));
    }
    public void SetOptions(bool rows, string property, int maxGroups, int minCount, bool includeMissing, string? sortByMeasure, bool descending, bool otherGroup) {
        var id = propertyId(property);
        var level = axis(rows).LastOrDefault(l => l.PropertyId == id)
            ?? throw new Exception("No " + axisName(rows) + " group on property \"" + _dm.Properties[id].CodeName + "\" to set options for. Add the group first. ");
        level.MaxGroups = maxGroups;
        level.MinCount = minCount;
        level.IncludeMissing = includeMissing;
        level.SortByMeasure = string.IsNullOrEmpty(sortByMeasure) ? null : sortByMeasure;
        level.Descending = descending;
        level.OtherGroup = otherGroup;
    }

    // ── measures ──
    public void AddMeasure(PivotFunction function, string? property, string? name) {
        Guid id = Guid.Empty;
        if (function != PivotFunction.Count) {
            if (string.IsNullOrEmpty(property)) throw new Exception(function + " needs a property. ");
            id = propertyId(property);
        }
        Spec.Measures.Add(new PivotMeasureSpec(function, id, string.IsNullOrEmpty(name) ? null : name));
    }

    // ── whole-pivot options ──
    public void SetTotals(bool rows, bool columns, bool subTotals) {
        Spec.RowTotals = rows;
        Spec.ColumnTotals = columns;
        Spec.SubTotals = subTotals;
    }
    public void SetLimits(int maxCells, bool throwWhenExceeded) {
        if (maxCells <= 0) throw new ArgumentOutOfRangeException(nameof(maxCells), "Max cells must be greater than 0. ");
        Spec.MaxCells = maxCells;
        Spec.ThrowWhenExceeded = throwWhenExceeded;
    }
    public void SetRowPaging(int pageIndex, int pageSize) {
        if (pageIndex < 0) throw new ArgumentOutOfRangeException(nameof(pageIndex), "Page index must be greater than or equal to 0.");
        if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be greater than 0.");
        Spec.RowPageIndex = pageIndex;
        Spec.RowPageSize = pageSize;
    }

    public object Evaluate(IVariables vars) {
        // a facet clause as input contributes its selection filter only; counting its buckets would
        // be wasted work (and, with no facets named, would count every automatic facet)
        var set = _input is FacetMethod facets ? facets.EvaluateSelection(vars) : _input.Evaluate(vars);
        if (set is FacetQueryResultData fq) set = fq.Result;
        if (set is not IPivotSource source) throw new Exception("Pivot is only supported on a collection of nodes. ");
        return source.EvaluatePivot(Spec, vars.Context);
    }
    override public string ToString() => _input + ".Pivot(...)";
}
