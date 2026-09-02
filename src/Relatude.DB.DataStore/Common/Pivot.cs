using Relatude.DB.Datamodels.Properties;
using System.Globalization;

namespace Relatude.DB.Common;

/// <summary>The aggregate a pivot measure computes over the nodes of a cell.</summary>
public enum PivotFunction {
    Count = 0,          // nodes in the cell; needs no property
    CountDistinct = 1,  // distinct values of the property among the nodes in the cell
    Sum = 2,
    Average = 3,        // over the nodes that HAVE a value; nodes without one are not in the denominator
    Min = 4,
    Max = 5,
}

/// <summary>Calendar bucketing of a DateTime / DateTimeOffset group property.</summary>
public enum DateInterval {
    None = 0,
    Year = 1,
    Quarter = 2,
    Month = 3,
    Week = 4,   // ISO 8601 weeks, Monday first
    Day = 5,
    Hour = 6,
}

/// <summary>
/// One nesting level on a pivot axis: the property to group by and how its buckets are formed.
/// IsRange null lets the engine choose value vs range buckets (the same rule as AddFacet), false
/// forces one bucket per distinct value, true forces ranges (auto-generated unless Values holds
/// explicit ranges). Interval bucketing (dates) is a range mode with calendar boundaries.
/// </summary>
public sealed class PivotGroupSpec {
    public PivotGroupSpec(Guid propertyId) { PropertyId = propertyId; }
    public Guid PropertyId { get; }
    public bool? IsRange { get; set; }
    public DateInterval Interval { get; set; }
    public int BucketCount { get; set; } // 0 = the property's default range count
    public List<FacetValue> Values { get; } = []; // explicit ranges (Value..Value2), in bucket order
    // options (the SetRowOptions / SetColumnOptions analogue of SetFacetOptions):
    public int MaxGroups { get; set; }           // 0 = unlimited
    public int MinCount { get; set; }            // groups with fewer nodes are dropped (into Other when OtherGroup)
    public bool IncludeMissing { get; set; }     // a bucket for nodes without a value
    public string? SortByMeasure { get; set; }   // measure name (or "Count"); null = natural bucket order
    public bool Descending { get; set; } = true; // direction of SortByMeasure
    public bool OtherGroup { get; set; }         // collect the groups trimmed by MaxGroups/MinCount into one "(other)" group
    public bool HasOptions => MaxGroups != 0 || MinCount != 0 || IncludeMissing || SortByMeasure != null || !Descending || OtherGroup;
    public PivotGroupSpec Clone() {
        var c = new PivotGroupSpec(PropertyId) {
            IsRange = IsRange, Interval = Interval, BucketCount = BucketCount,
            MaxGroups = MaxGroups, MinCount = MinCount, IncludeMissing = IncludeMissing,
            SortByMeasure = SortByMeasure, Descending = Descending, OtherGroup = OtherGroup,
        };
        foreach (var v in Values) c.Values.Add(v.Clone());
        return c;
    }
}

/// <summary>A value computed per cell. PropertyId is Guid.Empty for Count.</summary>
public sealed class PivotMeasureSpec {
    public PivotMeasureSpec(PivotFunction function, Guid propertyId, string? name) {
        Function = function;
        PropertyId = propertyId;
        Name = name;
    }
    public PivotFunction Function { get; }
    public Guid PropertyId { get; }
    public string? Name { get; set; } // null = default name, "Count" or "<CodeName>.<Function>"
    public PivotMeasureSpec Clone() => new(Function, PropertyId, Name);
}

/// <summary>The whole pivot definition the query string carries to the store.</summary>
public sealed class PivotSpec {
    public const int DefaultMaxCells = 250_000;
    public List<PivotGroupSpec> Rows { get; } = [];
    public List<PivotGroupSpec> Columns { get; } = [];
    public List<PivotMeasureSpec> Measures { get; } = [];
    public bool RowTotals { get; set; } = true;
    public bool ColumnTotals { get; set; } = true;
    public bool SubTotals { get; set; } = false;
    public int MaxCells { get; set; } = DefaultMaxCells; // rows x columns; above it the row axis is truncated (Capped) or the query throws
    public bool ThrowWhenExceeded { get; set; } = false;
    public int RowPageIndex { get; set; }
    public int? RowPageSize { get; set; } // null = every row group
    public PivotSpec Clone() {
        var c = new PivotSpec {
            RowTotals = RowTotals, ColumnTotals = ColumnTotals, SubTotals = SubTotals,
            MaxCells = MaxCells, ThrowWhenExceeded = ThrowWhenExceeded,
            RowPageIndex = RowPageIndex, RowPageSize = RowPageSize,
        };
        foreach (var r in Rows) c.Rows.Add(r.Clone());
        foreach (var col in Columns) c.Columns.Add(col.Clone());
        foreach (var m in Measures) c.Measures.Add(m.Clone());
        return c;
    }
}

// ── result ──────────────────────────────────────────────────────────────────────

/// <summary>A measure as it appears in the result, in the order of the cells' value arrays.</summary>
public sealed class PivotMeasure {
    public PivotMeasure(string name, PivotFunction function, Guid propertyId, string? propertyName) {
        Name = name;
        Function = function;
        PropertyId = propertyId;
        PropertyName = propertyName;
    }
    public string Name { get; }
    public PivotFunction Function { get; }
    public Guid PropertyId { get; }
    public string? PropertyName { get; }
    public override string ToString() => Name;
}

/// <summary>One nesting level of an axis, as it was resolved against the datamodel.</summary>
public sealed class PivotLevel {
    public PivotLevel(Guid propertyId, string codeName, PropertyType valueType, bool isRange, DateInterval interval) {
        PropertyId = propertyId;
        CodeName = codeName;
        ValueType = valueType;
        IsRange = isRange;
        Interval = interval;
    }
    public Guid PropertyId { get; }
    public string CodeName { get; }
    public string DisplayName => CodeName;
    public PropertyType ValueType { get; }
    public bool IsRange { get; }
    public DateInterval Interval { get; }
    public override string ToString() => CodeName;
}

/// <summary>
/// One group (bucket path) on an axis. Values has one entry per level; a null value is the
/// missing-value bucket ("(none)") or the trimmed-groups bucket when IsOther. Values2 holds range
/// upper bounds, null for value buckets. Relation-property buckets carry the related node.
/// A sub-total group has fewer values than the axis has levels.
/// </summary>
public sealed class PivotGroup {
    public PivotGroup(object?[] values, object?[] values2, string[] displayNames, int count, bool isOther) {
        Values = values;
        Values2 = values2;
        DisplayNames = displayNames;
        Count = count;
        IsOther = isOther;
    }
    public object?[] Values { get; }
    public object?[] Values2 { get; }
    public string[] DisplayNames { get; }
    public int Depth => Values.Length;
    public string DisplayName => Depth == 0 ? "(all)" : string.Join(" / ", DisplayNames);
    public int Count { get; } // nodes in the group, across the whole opposite axis
    public bool IsOther { get; }
    public override string ToString() => DisplayName + " (" + Count + ")";
}

/// <summary>
/// The aggregates of one cell (or total). Values is aligned with <see cref="PivotResult.Measures"/>;
/// every value is a double so numeric properties of any type read the same way. A value is null
/// when it is undefined for the cell: Sum/Average/Min/Max over nodes that have no value.
/// </summary>
public sealed class PivotCell {
    readonly string[] _measureNames; // not a property: stays out of JSON, the names are on the result
    public PivotCell(int row, int column, int count, double?[] values, string[] measureNames) {
        Row = row;
        Column = column;
        Count = count;
        Values = values;
        _measureNames = measureNames;
    }
    public int Row { get; }     // index into Rows.Groups; -1 for a column total, the grand total or a column sub-total
    public int Column { get; }  // index into Columns.Groups; -1 for a row total, the grand total or a row sub-total
    public int Count { get; }
    public double?[] Values { get; }
    public double? Get(int measureIndex) => Values[measureIndex];
    public double? Get(string measureName) {
        for (var i = 0; i < _measureNames.Length; i++) {
            if (string.Equals(_measureNames[i], measureName, StringComparison.OrdinalIgnoreCase)) return Values[i];
        }
        throw new ArgumentException("Unknown measure \"" + measureName + "\". Measures: " + string.Join(", ", _measureNames), nameof(measureName));
    }
    public override string ToString() {
        var parts = new List<string> { "n=" + Count };
        for (var i = 0; i < Values.Length; i++) parts.Add(_measureNames[i] + "=" + (Values[i].HasValue ? Values[i]!.Value.ToString(CultureInfo.InvariantCulture) : "-"));
        return string.Join(" ", parts);
    }
}

/// <summary>An axis of the result: its levels and its leaf groups in display order.</summary>
public sealed class PivotAxisResult {
    public PivotAxisResult(PivotLevel[] levels, PivotGroup[] groups, int totalGroupCount, int pageIndex, int pageSize) {
        Levels = levels;
        Groups = groups;
        TotalGroupCount = totalGroupCount;
        PageIndex = pageIndex;
        PageSize = pageSize;
    }
    public PivotLevel[] Levels { get; }
    public PivotGroup[] Groups { get; }      // leaves: sorted, trimmed and (for rows) paged
    public int TotalGroupCount { get; }      // leaves before paging
    public int PageIndex { get; }
    public int PageSize { get; }             // 0 = not paged
}

/// <summary>A sub-total: a non-leaf group of one axis, with its cells against the leaves of the other axis.</summary>
public sealed class PivotSubTotal {
    public PivotSubTotal(PivotGroup group, PivotCell?[] cells, PivotCell total) {
        Group = group;
        Cells = cells;
        Total = total;
    }
    public PivotGroup Group { get; }
    public PivotCell?[] Cells { get; } // aligned with the OTHER axis' Groups; null = empty
    public PivotCell Total { get; }
}

/// <summary>One row of the table, for rendering: the group, its cells per column and its total.</summary>
public sealed class PivotRow {
    public PivotRow(int index, PivotGroup group, PivotCell?[] cells, PivotCell? total) {
        Index = index;
        Group = group;
        Cells = cells;
        Total = total;
    }
    public int Index { get; }
    public PivotGroup Group { get; }
    public PivotCell?[] Cells { get; } // aligned with Columns.Groups; null = empty cell
    public PivotCell? Total { get; }   // null when row totals were switched off
}

/// <summary>A flat table view: one row per (row group x column group) cell, plus a total row per row group.</summary>
public sealed class PivotTable {
    public PivotTable(string[] columns, List<object?[]> rows) {
        Columns = columns;
        Rows = rows;
    }
    public string[] Columns { get; }
    public List<object?[]> Rows { get; }
}

/// <summary>
/// The result of a pivot query. Cells is sparse: a (row, column) pair with no nodes has no cell.
/// Totals are computed over the union of nodes, never by adding cells, so they are right for
/// averages and for array-valued group properties (where one node sits in several groups).
/// </summary>
public sealed class PivotResult {
    Dictionary<(int, int), PivotCell>? _cellIndex;
    public PivotResult(PivotMeasure[] measures, PivotAxisResult rows, PivotAxisResult columns, PivotCell[] cells,
        PivotCell[] rowTotals, PivotCell[] columnTotals, PivotCell grandTotal,
        PivotSubTotal[] rowSubTotals, PivotSubTotal[] columnSubTotals, int sourceCount, bool capped) {
        Measures = measures;
        Rows = rows;
        Columns = columns;
        Cells = cells;
        RowTotals = rowTotals;
        ColumnTotals = columnTotals;
        GrandTotal = grandTotal;
        RowSubTotals = rowSubTotals;
        ColumnSubTotals = columnSubTotals;
        SourceCount = sourceCount;
        Capped = capped;
    }
    public PivotMeasure[] Measures { get; }
    public PivotAxisResult Rows { get; }
    public PivotAxisResult Columns { get; }
    public PivotCell[] Cells { get; }
    public PivotCell[] RowTotals { get; }      // aligned with Rows.Groups; empty when switched off
    public PivotCell[] ColumnTotals { get; }   // aligned with Columns.Groups; empty when switched off
    public PivotCell GrandTotal { get; }
    public PivotSubTotal[] RowSubTotals { get; }
    public PivotSubTotal[] ColumnSubTotals { get; }
    public int SourceCount { get; }            // nodes the pivot was computed over
    public bool Capped { get; set; }           // MaxCells was hit and the row axis truncated
    public double DurationMs { get; set; }

    public PivotCell? GetCell(int row, int column) {
        if (_cellIndex == null) {
            var index = new Dictionary<(int, int), PivotCell>(Cells.Length);
            foreach (var c in Cells) index[(c.Row, c.Column)] = c;
            _cellIndex = index;
        }
        return _cellIndex.TryGetValue((row, column), out var cell) ? cell : null;
    }
    public PivotCell? this[int row, int column] => GetCell(row, column);
    public int IndexOfMeasure(string name) {
        for (var i = 0; i < Measures.Length; i++) if (string.Equals(Measures[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
    public IEnumerable<PivotRow> EnumerateRows() {
        for (var r = 0; r < Rows.Groups.Length; r++) {
            var cells = new PivotCell?[Columns.Groups.Length];
            for (var c = 0; c < cells.Length; c++) cells[c] = GetCell(r, c);
            yield return new PivotRow(r, Rows.Groups[r], cells, RowTotals.Length > r ? RowTotals[r] : null);
        }
    }
    /// <summary>
    /// The result as a flat table: the row level display names, the column level display names, the
    /// count and one column per measure. Totals are not included; read them from the result.
    /// </summary>
    public PivotTable ToTable() {
        var columns = new List<string>();
        foreach (var l in Rows.Levels) columns.Add(l.CodeName);
        foreach (var l in Columns.Levels) columns.Add(l.CodeName);
        columns.Add("Count");
        foreach (var m in Measures) columns.Add(m.Name);
        var rows = new List<object?[]>(Cells.Length);
        foreach (var cell in Cells) {
            var row = new object?[columns.Count];
            var i = 0;
            var rg = Rows.Groups[cell.Row];
            var cg = Columns.Groups[cell.Column];
            for (var l = 0; l < Rows.Levels.Length; l++) row[i++] = l < rg.DisplayNames.Length ? rg.DisplayNames[l] : null;
            for (var l = 0; l < Columns.Levels.Length; l++) row[i++] = l < cg.DisplayNames.Length ? cg.DisplayNames[l] : null;
            row[i++] = cell.Count;
            foreach (var v in cell.Values) row[i++] = v;
            rows.Add(row);
        }
        return new PivotTable(columns.ToArray(), rows);
    }
    public override string ToString() {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("DURATION: " + DurationMs.ToString("0.00ms", CultureInfo.InvariantCulture));
        sb.AppendLine("SOURCE: " + SourceCount + (Capped ? " (capped)" : ""));
        sb.AppendLine("MEASURES: " + string.Join(", ", Measures.Select(m => m.Name)));
        sb.AppendLine("COLUMNS: " + string.Join(" | ", Columns.Groups.Select(g => g.DisplayName)));
        foreach (var row in EnumerateRows()) {
            sb.Append(row.Group.DisplayName + ": ");
            sb.Append(string.Join(" | ", row.Cells.Select(c => c == null ? "-" : c.ToString())));
            if (row.Total != null) sb.Append("  => " + row.Total);
            sb.AppendLine();
        }
        sb.AppendLine("TOTAL: " + GrandTotal);
        return sb.ToString();
    }
}
