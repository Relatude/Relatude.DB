namespace VectorIndexBenchmarks.Harness;

/// <summary>
/// One column of a result table: its header, the number behind each cell, how that number reads,
/// and which end of it wins — throughput and accuracy columns want the largest value, latency and
/// footprint columns the smallest. The first entry is the engine label, which carries no number.
/// </summary>
public sealed record TableColumn(string Header, Func<BenchResult, double?> Value, Func<double, string> Text, bool LowerIsBetter = false, string? Phase = null) {
    public string Format(double? v) => v is null ? "-" : Text(v.Value);
    public bool Unsupported(BenchResult r) => Phase is not null && r.IsUnsupported(Phase);

    public static TableColumn Rate(string header, string phase)
        => new(header, r => r.Rate(phase), rateText, Phase: phase);
    public static TableColumn Ms(string header, string key, bool optional = false)
        => new(header, r => r.Ms(key), v => v >= 1000 ? $"{v / 1000:0.00}s" : v >= 10 ? $"{v:0}" : $"{v:0.00}", LowerIsBetter: true, Phase: optional ? key : null);
    public static TableColumn Percent(string header, string key, bool optional = false)
        => new(header, r => r.Quality0To1(key), v => (v * 100).ToString("0.0"), Phase: optional ? key : null);
    public static TableColumn Megabytes(string header, Func<BenchResult, double> value)
        => new(header, r => value(r), v => v.ToString("0.0"), LowerIsBetter: true);

    static string rateText(double v)
        => v >= 10_000_000 ? $"{v / 1e6:0.0}M"
            : v >= 1_000_000 ? $"{v / 1e6:0.00}M"
            : v >= 10_000 ? $"{v / 1e3:0}k"
            : v >= 1_000 ? $"{v / 1e3:0.0}k"
            : v >= 10 ? $"{v:0}"
            : $"{v:0.0}";
}

/// <summary>The two tables a run prints, and the measurements each is made of.</summary>
public static class Columns {
    static readonly TableColumn label = new("Engine", _ => null, _ => "");

    public static readonly TableColumn[] Search = [
        label,
        TableColumn.Rate("Top10", "Top10"),
        TableColumn.Rate("Top100", "Top100"),
        TableColumn.Rate("NoFloor", "NoFloor"),
        TableColumn.Rate("Filter", "Filter"),
        TableColumn.Percent("Recall%", "Recall"),
        TableColumn.Percent("Filter%", "FilterRecall", optional: true),
    ];

    public static readonly TableColumn[] Write = [
        label,
        TableColumn.Rate("Index/s", "Index"),
        TableColumn.Ms("Save ms", "Save"),
        TableColumn.Ms("Flush ms", "Flush", optional: true),
        TableColumn.Rate("Update/s", "Update"),
        TableColumn.Rate("Remove/s", "Remove"),
        TableColumn.Rate("Mixed/s", "Mixed"),
        TableColumn.Ms("Open ms", "Open"),
        TableColumn.Ms("Cold ms", "Cold"),
        TableColumn.Megabytes("Mem MB", r => r.ManagedMB),
        TableColumn.Megabytes("Warm MB", r => r.WarmManagedMB),
        TableColumn.Megabytes("WSet MB", r => r.WorkingSetMB),
        TableColumn.Megabytes("Disk MB", r => r.DiskMB),
    ];
}

/// <summary>Prints one result table: a row per engine, ranked per column so the winner of a column
/// can be found without comparing every cell, with the errors of failed engines listed under it.</summary>
public static class ResultTable {
    public static void Print(string title, TableColumn[] columns, IReadOnlyList<BenchResult> rows) {
        var table = new List<string[]> { columns.Select(c => c.Header).ToArray() };
        var values = new List<double?[]>(); // the numbers behind the cells, for ranking

        foreach (var r in rows) {
            var failed = r.Error is not null && r.Phases.Count == 0;
            var cells = new string[columns.Length];
            var vals = new double?[columns.Length];
            cells[0] = Engines.DisplayName(r.Engine);
            for (var c = 1; c < columns.Length; c++) {
                if (failed) {
                    cells[c] = c == 1 ? "FAILED" : "";
                } else if (columns[c].Unsupported(r)) {
                    cells[c] = "n/a"; // the implementation does not have it: not a slow result, an absent one
                } else {
                    vals[c] = columns[c].Value(r);
                    cells[c] = columns[c].Format(vals[c]);
                }
            }
            table.Add(cells);
            values.Add(vals);
        }

        var widths = new int[columns.Length];
        foreach (var row in table)
            for (var c = 0; c < row.Length; c++)
                widths[c] = Math.Max(widths[c], row[c].Length);
        var ranks = rankColumns(columns, values);

        Console.WriteLine($"— {title} —");
        for (var i = 0; i < table.Count; i++) {
            Console.Write("  ");
            for (var c = 0; c < table[i].Length; c++) {
                var cell = c == 0 ? table[i][c].PadRight(widths[c]) : table[i][c].PadLeft(widths[c]);
                TableColor.Write(cell, i == 0 ? TableColor.Plain : ranks[i - 1][c]);
                if (c < table[i].Length - 1) Console.Write("  ");
            }
            Console.WriteLine();
            if (i == 0) Console.WriteLine("  " + string.Join("  ", widths.Select(w => new string('-', w))));
        }
        foreach (var r in rows.Where(r => r.Error is not null))
            Console.WriteLine($"  ! {Engines.DisplayName(r.Engine)}: {r.Error!.Split('\n')[0].Trim()}");
    }

    /// <summary>
    /// Per column: the best value, the runner-up, and everything else — so a reader can find the
    /// winner of a column without comparing every cell. Equal values share a place, absent ones take
    /// none, and a table with a single row is not ranked at all (nothing to win against).
    /// </summary>
    static int[][] rankColumns(TableColumn[] columns, List<double?[]> values) {
        var ranks = new int[values.Count][];
        for (var i = 0; i < values.Count; i++) {
            ranks[i] = new int[columns.Length];
            Array.Fill(ranks[i], TableColor.Rest);
            ranks[i][0] = TableColor.Plain; // the engine name is a label, not a measurement
        }
        if (values.Count < 2) {
            foreach (var row in ranks) Array.Fill(row, TableColor.Plain);
            return ranks;
        }
        for (var c = 1; c < columns.Length; c++) {
            var distinct = values.Select(v => v[c]).Where(v => v.HasValue).Select(v => v!.Value).Distinct().ToList();
            if (distinct.Count == 0) continue;
            distinct.Sort();
            if (!columns[c].LowerIsBetter) distinct.Reverse();
            var best = distinct[0];
            double? second = distinct.Count > 1 ? distinct[1] : null;
            for (var i = 0; i < values.Count; i++) {
                var v = values[i][c];
                if (v is null) continue;
                if (v.Value == best) ranks[i][c] = TableColor.Best;
                else if (second is not null && v.Value == second.Value) ranks[i][c] = TableColor.Second;
            }
        }
        return ranks;
    }
}

/// <summary>
/// Result-table colouring: the best value in a column green, the runner-up cyan, the rest dimmed
/// so the two that matter stand out. Set through <see cref="Console.ForegroundColor"/> rather than
/// escape codes, so it works the same in a Windows console and a POSIX terminal, and is skipped
/// for redirected output (and when NO_COLOR is set) so piped tables stay plain text.
/// </summary>
public static class TableColor {
    public const int Best = 0, Second = 1, Rest = 2, Plain = 3;

    static readonly bool enabled = Environment.GetEnvironmentVariable("NO_COLOR") is null && !Console.IsOutputRedirected;

    public static void Write(string text, int rank) {
        if (!enabled || rank == Plain) {
            Console.Write(text);
            return;
        }
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = rank switch {
            Best => ConsoleColor.Green,
            Second => ConsoleColor.Cyan,
            _ => ConsoleColor.DarkGray,
        };
        Console.Write(text);
        Console.ForegroundColor = previous;
    }
}
