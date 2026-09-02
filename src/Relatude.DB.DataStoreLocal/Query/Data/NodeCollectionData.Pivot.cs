using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Definitions.PropertyTypes;
using Relatude.DB.DataStores.Sets;
using System.Globalization;

namespace Relatude.DB.Query.Data;

internal partial class NodeCollectionData : IPivotSource {
    public PivotQueryResultData EvaluatePivot(PivotSpec spec, QueryContext ctx) => new PivotEvaluator(_def, _ids, spec, ctx).Evaluate();
}

/// <summary>
/// Computes a pivot over an id set with the facet primitives: every group level is bucketed by the
/// property the way a facet is (GetDefaultFacets), each bucket becomes an id set (FilterFacets), nested
/// levels and cells are set intersections, and measures are one pass over a cell's ids against the
/// measure property's index (Property.Aggregate). No node is ever read. Totals are aggregated over
/// their own sets, never added up from cells - that keeps averages right, and counts right when an
/// array-valued group property puts one node in several groups.
/// </summary>
internal sealed class PivotEvaluator {
    readonly Definition _def;
    readonly IdSet _source;
    readonly PivotSpec _spec;
    readonly QueryContext _ctx;
    readonly SetRegister _sets;
    // measures, resolved
    readonly List<Measure> _measures = [];
    readonly List<(Property prop, bool distinct)> _aggregated = []; // one index pass per distinct property, shared by its measures
    string[] _measureNames = [];
    bool _capped;

    sealed record Measure(string Name, PivotFunction Function, Property? Property, int AggregateIndex);
    sealed class Bucket(FacetValue value, IdSet set) {
        public FacetValue Value = value;
        public IdSet Set = set;
    }
    sealed class Level(PivotGroupSpec spec, Property property, PivotLevel info, List<Bucket> buckets) {
        public PivotGroupSpec Spec = spec;
        public Property Property = property;
        public PivotLevel Info = info;
        public List<Bucket> Buckets = buckets;
    }
    sealed class Group(FacetValue?[] path, IdSet set, bool isOther) {
        public FacetValue?[] Path = path; // one per level; null = the "(other)" group
        public IdSet Set = set;
        public bool IsOther = isOther;
        public List<Group>? Children;
        public double?[]? Total; // measures over Set, computed when needed
        public int Index = -1;   // leaf index in the final axis, -1 when not a leaf on the page
    }
    sealed class Axis(List<Level> levels) {
        public List<Level> Levels = levels;
        public List<Group> Roots = [];
        public List<Group> Leaves = [];
        public int TotalLeafCount;
        public int LeafCount; // produced so far, for the budget
    }

    public PivotEvaluator(Definition def, IdSet source, PivotSpec spec, QueryContext ctx) {
        _def = def;
        _source = source;
        _spec = spec;
        _ctx = ctx;
        _sets = def.Sets;
    }

    public PivotQueryResultData Evaluate() {
        resolveMeasures();
        var rows = new Axis(resolveLevels(_spec.Rows, "row"));
        var columns = new Axis(resolveLevels(_spec.Columns, "column"));
        buildGroups(rows);
        buildGroups(columns);

        // row paging, then the cell budget: past it the row axis is cut (or the query fails)
        var rowLeaves = rows.Leaves;
        rows.TotalLeafCount = rowLeaves.Count;
        var pageSize = _spec.RowPageSize ?? 0;
        if (pageSize > 0) rowLeaves = rowLeaves.Skip(_spec.RowPageIndex * pageSize).Take(pageSize).ToList();
        var colLeaves = columns.Leaves;
        columns.TotalLeafCount = colLeaves.Count;
        if ((long)rowLeaves.Count * colLeaves.Count > _spec.MaxCells) {
            if (_spec.ThrowWhenExceeded) throw new Exception("The pivot has " + rowLeaves.Count + " x " + colLeaves.Count + " cells, more than the limit of " + _spec.MaxCells + ". Group by fewer values (SetRowOptions/SetColumnOptions maxGroups) or raise the limit with SetLimits. ");
            rowLeaves = rowLeaves.Take(Math.Max(1, _spec.MaxCells / Math.Max(1, colLeaves.Count))).ToList();
            _capped = true;
        }
        for (var i = 0; i < rowLeaves.Count; i++) rowLeaves[i].Index = i;
        for (var i = 0; i < colLeaves.Count; i++) colLeaves[i].Index = i;

        // cells
        var noRowLevels = rows.Levels.Count == 0;
        var noColLevels = columns.Levels.Count == 0;
        var cells = new List<PivotCell>();
        foreach (var row in rowLeaves) {
            foreach (var col in colLeaves) {
                var set = noColLevels ? row.Set : noRowLevels ? col.Set : _sets.Intersection(row.Set, col.Set);
                if (set.Count == 0) continue;
                cells.Add(cell(set, row.Index, col.Index));
            }
        }
        // totals
        var rowTotals = _spec.RowTotals ? rowLeaves.Select(g => cell(g.Set, g.Index, -1, g.Total)).ToArray() : [];
        var colTotals = _spec.ColumnTotals ? colLeaves.Select(g => cell(g.Set, -1, g.Index, g.Total)).ToArray() : [];
        var grandTotal = cell(_source, -1, -1);
        var rowSubTotals = _spec.SubTotals && rows.Levels.Count > 1 ? subTotals(rows, colLeaves, true) : [];
        var colSubTotals = _spec.SubTotals && columns.Levels.Count > 1 ? subTotals(columns, rowLeaves, false) : [];

        var result = new PivotResult(
            _measures.Select(m => new PivotMeasure(m.Name, m.Function, m.Property?.Id ?? Guid.Empty, m.Property?.CodeName)).ToArray(),
            new PivotAxisResult(rows.Levels.Select(l => l.Info).ToArray(), rowLeaves.Select(toGroup).ToArray(), rows.TotalLeafCount, pageSize > 0 ? _spec.RowPageIndex : 0, pageSize),
            new PivotAxisResult(columns.Levels.Select(l => l.Info).ToArray(), colLeaves.Select(toGroup).ToArray(), columns.TotalLeafCount, 0, 0),
            cells.ToArray(), rowTotals, colTotals, grandTotal, rowSubTotals, colSubTotals, _source.Count, _capped);
        return new PivotQueryResultData(result);
    }

    // ── measures ──
    void resolveMeasures() {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in _spec.Measures) {
            Property? prop = null;
            var aggregateIndex = -1;
            if (m.Function != PivotFunction.Count) {
                if (!_def.Properties.TryGetValue(m.PropertyId, out prop)) throw new Exception("Unknown measure property " + m.PropertyId + ". ");
                if (!prop.CanAggregate) throw new Exception("The property \"" + prop.CodeName + "\" cannot be used as a pivot measure: only indexed scalar value properties can be aggregated" + (prop.Indexed ? "" : ", and it is not indexed") + ". ");
                if (m.Function != PivotFunction.CountDistinct && !prop.IsNumeric) throw new Exception(m.Function + " needs a numeric property (int, long, double, float, decimal, byte). \"" + prop.CodeName + "\" is " + prop.PropertyType + ". Use CountDistinct on it, or Sum/Average/Min/Max on a numeric property. ");
                var distinct = m.Function == PivotFunction.CountDistinct;
                aggregateIndex = _aggregated.FindIndex(a => a.prop == prop && a.distinct == distinct);
                if (aggregateIndex < 0) { _aggregated.Add((prop, distinct)); aggregateIndex = _aggregated.Count - 1; }
            }
            var name = m.Name ?? (prop == null ? "Count" : prop.CodeName + "." + m.Function);
            if (!names.Add(name)) throw new Exception("Two pivot measures are named \"" + name + "\". Give one of them another name. ");
            _measures.Add(new Measure(name, m.Function, prop, aggregateIndex));
        }
        _measureNames = _measures.Select(m => m.Name).ToArray();
    }
    double?[] aggregate(IdSet set) {
        var values = new double?[_measures.Count];
        PivotAggregate[]? aggs = null;
        if (_aggregated.Count > 0) {
            aggs = new PivotAggregate[_aggregated.Count];
            for (var i = 0; i < aggs.Length; i++) aggs[i] = _aggregated[i].prop.Aggregate(set, _ctx, _aggregated[i].distinct);
        }
        for (var i = 0; i < _measures.Count; i++) {
            var m = _measures[i];
            if (m.Function == PivotFunction.Count) { values[i] = set.Count; continue; }
            var a = aggs![m.AggregateIndex];
            values[i] = m.Function switch {
                PivotFunction.CountDistinct => a.DistinctCount,
                _ when a.CountWithValue == 0 => null, // no node in the cell has a value: undefined, not 0
                PivotFunction.Sum => a.Sum,
                PivotFunction.Average => a.Sum / a.CountWithValue,
                PivotFunction.Min => a.Min,
                PivotFunction.Max => a.Max,
                _ => throw new NotSupportedException(m.Function.ToString()),
            };
        }
        return values;
    }
    PivotCell cell(IdSet set, int row, int column, double?[]? precomputed = null) => new(row, column, set.Count, precomputed ?? aggregate(set), _measureNames);

    // ── levels and buckets ──
    List<Level> resolveLevels(List<PivotGroupSpec> specs, string axisName) {
        var levels = new List<Level>(specs.Count);
        foreach (var g in specs) {
            if (!_def.Properties.TryGetValue(g.PropertyId, out var prop)) throw new Exception("Unknown " + axisName + " group property " + g.PropertyId + ". ");
            if (!prop.CanBeFacet()) {
                var why = prop is RelationProperty ? "relation properties must opt in with [RelationProperty(Facet = true)]"
                    : !prop.Indexed ? "it is not indexed"
                    : prop.Model.NotFacet ? "it is marked NotFacet" : "values of type " + prop.PropertyType + " cannot be bucketed";
                throw new Exception("The property \"" + prop.CodeName + "\" cannot be used as a pivot " + axisName + " group: " + why + ". ");
            }
            var facets = g.Interval != DateInterval.None ? intervalBuckets(prop, g) : defaultBuckets(prop, g);
            var isRange = facets.IsRangeFacet == true;
            var buckets = new List<Bucket>(facets.Values.Count);
            foreach (var fv in facets.Values) {
                var selected = fv.Clone();
                selected.Selected = true;
                var set = prop.FilterFacets(new Facets(prop.Model, isRange, [selected]), _source, _ctx);
                if (set.Count == 0) continue; // empty buckets are not groups
                buckets.Add(new Bucket(fv, set));
            }
            levels.Add(new Level(g, prop, new PivotLevel(prop.Id, prop.CodeName, prop.PropertyType, isRange, g.Interval), buckets));
        }
        return levels;
    }
    Facets defaultBuckets(Property prop, PivotGroupSpec g) {
        var given = new Facets(prop.Model, g.IsRange);
        given.IncludeMissing = g.IncludeMissing;
        if (g.BucketCount > 0) given.RangeCount = g.BucketCount;
        foreach (var v in g.Values) given.AddValue(v.Clone());
        var facets = prop.GetDefaultFacets(given, _ctx);
        facets.Sort(); // the natural bucket order (ranges keep their generated order); a measure sort is applied later
        return facets;
    }
    // calendar buckets [start, next) from the first interval holding the index minimum to the one
    // holding its maximum; the ones no node of the source falls in are dropped by the caller
    Facets intervalBuckets(Property prop, PivotGroupSpec g) {
        var facets = new Facets(prop.Model, true);
        facets.IncludeMissing = g.IncludeMissing;
        if (prop.TryGetMinMax(_ctx, out var minValue, out var maxValue)) {
            TimeSpan? offset = null;
            DateTime min, max;
            if (minValue is DateTimeOffset dtoMin && maxValue is DateTimeOffset dtoMax) { offset = dtoMin.Offset; min = dtoMin.DateTime; max = dtoMax.ToOffset(dtoMin.Offset).DateTime; }
            else if (minValue is DateTime dtMin && maxValue is DateTime dtMax) { min = dtMin; max = dtMax; }
            else throw new Exception("A date interval can only be used on a DateTime or DateTimeOffset property. \"" + prop.CodeName + "\" is " + prop.PropertyType + ". ");
            var start = floor(min, g.Interval);
            while (start <= max) {
                DateTime next;
                try { next = step(start, g.Interval); } catch (ArgumentOutOfRangeException) { next = DateTime.MaxValue; }
                // explicit boxing: without it the conditional's common type is DateTimeOffset and the
                // DateTime boundaries would silently pick up the local offset
                object from = offset.HasValue ? new DateTimeOffset(start, offset.Value) : (object)start;
                object to = offset.HasValue ? new DateTimeOffset(next, offset.Value) : (object)next;
                facets.AddValue(new FacetValue(from, to, label(start, g.Interval)) { FromInclusive = true, ToInclusive = next == DateTime.MaxValue });
                if (next == DateTime.MaxValue) break;
                start = next;
            }
        }
        if (g.IncludeMissing) facets.AddValue(new FacetValue(null));
        return facets;
    }
    static DateTime floor(DateTime d, DateInterval interval) => interval switch {
        DateInterval.Year => new DateTime(d.Year, 1, 1, 0, 0, 0, d.Kind),
        DateInterval.Quarter => new DateTime(d.Year, (d.Month - 1) / 3 * 3 + 1, 1, 0, 0, 0, d.Kind),
        DateInterval.Month => new DateTime(d.Year, d.Month, 1, 0, 0, 0, d.Kind),
        DateInterval.Week => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7)), // ISO weeks start on Monday
        DateInterval.Day => d.Date,
        DateInterval.Hour => new DateTime(d.Year, d.Month, d.Day, d.Hour, 0, 0, d.Kind),
        _ => throw new NotSupportedException(interval.ToString()),
    };
    static DateTime step(DateTime start, DateInterval interval) => interval switch {
        DateInterval.Year => start.AddYears(1),
        DateInterval.Quarter => start.AddMonths(3),
        DateInterval.Month => start.AddMonths(1),
        DateInterval.Week => start.AddDays(7),
        DateInterval.Day => start.AddDays(1),
        DateInterval.Hour => start.AddHours(1),
        _ => throw new NotSupportedException(interval.ToString()),
    };
    static string label(DateTime start, DateInterval interval) => interval switch {
        DateInterval.Year => start.ToString("yyyy", CultureInfo.InvariantCulture),
        DateInterval.Quarter => start.ToString("yyyy", CultureInfo.InvariantCulture) + " Q" + ((start.Month - 1) / 3 + 1),
        DateInterval.Month => start.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        DateInterval.Week => ISOWeek.GetYear(start).ToString(CultureInfo.InvariantCulture) + "-W" + ISOWeek.GetWeekOfYear(start).ToString("00", CultureInfo.InvariantCulture),
        DateInterval.Day => start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateInterval.Hour => start.ToString("yyyy-MM-dd HH:00", CultureInfo.InvariantCulture),
        _ => throw new NotSupportedException(interval.ToString()),
    };

    // ── groups ──
    void buildGroups(Axis axis) {
        if (axis.Levels.Count == 0) { // no grouping: one group holding everything, so cells and totals have a place
            var all = new Group([], _source, false);
            axis.Roots.Add(all);
            axis.Leaves.Add(all);
            return;
        }
        axis.Roots = expand(axis, _source, [], 0);
        collectLeaves(axis.Roots, axis.Leaves);
    }
    List<Group> expand(Axis axis, IdSet parentSet, FacetValue?[] prefix, int levelIndex) {
        var level = axis.Levels[levelIndex];
        var isLeafLevel = levelIndex == axis.Levels.Count - 1;
        var groups = new List<Group>();
        foreach (var bucket in level.Buckets) {
            // the bucket sets were cut against the source, so the first level needs no intersection
            var set = levelIndex == 0 ? bucket.Set : _sets.Intersection(parentSet, bucket.Set);
            if (set.Count == 0) continue;
            groups.Add(new Group([.. prefix, bucket.Value], set, false));
        }
        applyOptions(groups, level.Spec, prefix);
        if (isLeafLevel) {
            // the leaf budget: an axis never grows past the cell limit, whatever the other axis holds
            var room = _spec.MaxCells - axis.LeafCount;
            if (groups.Count > room) {
                if (_spec.ThrowWhenExceeded) throw new Exception("The pivot has more than " + _spec.MaxCells + " groups on one axis. Group by fewer values (SetRowOptions/SetColumnOptions maxGroups) or raise the limit with SetLimits. ");
                groups = groups.Take(Math.Max(0, room)).ToList();
                _capped = true;
            }
            axis.LeafCount += groups.Count;
        } else {
            foreach (var g in groups) {
                if (axis.LeafCount >= _spec.MaxCells) { g.Children = []; continue; }
                g.Children = expand(axis, g.Set, g.Path, levelIndex + 1);
            }
        }
        return groups;
    }
    // MinCount / MaxGroups / SortByMeasure / OtherGroup, within one parent
    void applyOptions(List<Group> groups, PivotGroupSpec spec, FacetValue?[] prefix) {
        if (spec.SortByMeasure != null) {
            var byCount = string.Equals(spec.SortByMeasure, "Count", StringComparison.OrdinalIgnoreCase) && _measures.All(m => !string.Equals(m.Name, spec.SortByMeasure, StringComparison.OrdinalIgnoreCase));
            var index = byCount ? -1 : Array.FindIndex(_measureNames, n => string.Equals(n, spec.SortByMeasure, StringComparison.OrdinalIgnoreCase));
            if (!byCount && index < 0) throw new Exception("Cannot sort groups by \"" + spec.SortByMeasure + "\": there is no such measure. Measures: " + string.Join(", ", _measureNames) + (_measureNames.Length == 0 ? "(none)" : "") + ". \"Count\" always works. ");
            double? key(Group g) {
                if (byCount) return g.Set.Count;
                g.Total ??= aggregate(g.Set);
                return g.Total[index];
            }
            // stable, with undefined values last whatever the direction
            var ordered = groups.OrderBy(g => key(g).HasValue ? 0 : 1);
            ordered = spec.Descending ? ordered.ThenByDescending(g => key(g) ?? 0) : ordered.ThenBy(g => key(g) ?? 0);
            var sorted = ordered.ToList();
            groups.Clear();
            groups.AddRange(sorted);
        }
        if (spec.MinCount <= 0 && spec.MaxGroups <= 0) return;
        var dropped = new List<Group>();
        if (spec.MinCount > 0) {
            dropped.AddRange(groups.Where(g => g.Set.Count < spec.MinCount));
            groups.RemoveAll(g => g.Set.Count < spec.MinCount);
        }
        if (spec.MaxGroups > 0 && groups.Count > spec.MaxGroups) {
            dropped.AddRange(groups.Skip(spec.MaxGroups));
            groups.RemoveRange(spec.MaxGroups, groups.Count - spec.MaxGroups);
        }
        if (dropped.Count > 0 && spec.OtherGroup) {
            var otherSet = _sets.Union(dropped.Select(d => d.Set).ToList());
            if (otherSet.Count > 0) groups.Add(new Group([.. prefix, null], otherSet, true));
        }
    }
    static void collectLeaves(List<Group> groups, List<Group> leaves) {
        foreach (var g in groups) {
            if (g.Children == null) leaves.Add(g);
            else collectLeaves(g.Children, leaves);
        }
    }
    PivotSubTotal[] subTotals(Axis axis, List<Group> otherLeaves, bool isRowAxis) {
        var result = new List<PivotSubTotal>();
        collect(axis.Roots);
        return result.ToArray();
        // a sub-total for every group above the leaf level that has a leaf on the page below it
        bool collect(List<Group> groups) {
            var any = false;
            foreach (var g in groups) {
                if (g.Children == null) { any |= g.Index >= 0; continue; }
                if (!collect(g.Children)) continue;
                any = true;
                var cells = new PivotCell?[otherLeaves.Count];
                for (var i = 0; i < otherLeaves.Count; i++) {
                    var set = otherLeaves[i].Path.Length == 0 ? g.Set : _sets.Intersection(g.Set, otherLeaves[i].Set);
                    if (set.Count == 0) continue;
                    cells[i] = isRowAxis ? cell(set, -1, otherLeaves[i].Index) : cell(set, otherLeaves[i].Index, -1);
                }
                result.Add(new PivotSubTotal(toGroup(g), cells, cell(g.Set, -1, -1, g.Total)));
            }
            return any;
        }
    }
    static PivotGroup toGroup(Group g) {
        var values = new object?[g.Path.Length];
        var values2 = new object?[g.Path.Length];
        var names = new string[g.Path.Length];
        for (var i = 0; i < g.Path.Length; i++) {
            var fv = g.Path[i];
            values[i] = fv?.Value;
            values2[i] = fv?.Value2;
            names[i] = fv == null ? "(other)" : displayName(fv);
        }
        return new PivotGroup(values, values2, names, g.Set.Count, g.IsOther);
    }
    // a bucket's label: the name the property gave it (enum names, related node names, interval and
    // custom range labels), else its value written the same way everywhere (FacetValue.ToString is
    // for facet lists and pads value buckets with a trailing space)
    static string displayName(FacetValue fv) {
        if (fv.ExplicitDisplayName != null) return fv.ExplicitDisplayName;
        if (fv.Value == null) return "(none)";
        if (fv.Value2 == null) return format(fv.Value);
        return format(fv.Value) + " - " + (fv.ToInclusive ? "" : "<") + format(fv.Value2);
    }
    static string format(object v) => v switch {
        DateTime dt => dt.ToString(dt.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString(dto.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };
}
