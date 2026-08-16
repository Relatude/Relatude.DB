using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace VectorIndexBenchmarks.Harness;

/// <summary>The grid a sweep runs: every engine at every corpus size at every cache budget.</summary>
public sealed class MatrixOptions {
    /// <summary>Corpus sizes, one run each (<c>--matrix-n</c>).</summary>
    public int[] VectorCounts = [];
    /// <summary>Cache budgets in MB, one run each (<c>--matrix-cache</c>). The in-memory index has
    /// no budget to spend, so its rows repeat across this axis — which makes them a useful noise
    /// floor for reading the others.</summary>
    public int[] CacheMB = [];
    /// <summary>Engines swept (<c>--engines</c>).</summary>
    public string[] EngineNames = [];
    /// <summary>Where the rows are written (<c>--csv</c>).</summary>
    public string CsvPath = "vector-matrix.csv";
    /// <summary>Where the HTML report goes (<c>--html</c>); next to the CSV when not given.</summary>
    public string? HtmlPath;
}

/// <summary>
/// A sweep: one <see cref="BenchRunner"/> run per (corpus size, cache budget, engine), each in its
/// own child process so its memory number is its own, with every row appended to a CSV as soon as
/// it is measured — a sweep of any size can be read, charted or interrupted while it is still
/// running, and nothing is lost if it is.
///
/// <para>The run itself is the ordinary benchmark, so a row's inputs are the same settings a single
/// run takes; only the reporting differs. Sweeps do not need the search rates or the recall
/// measurement, so <c>--skip-searches</c> is worth setting for them (the mixed phase still searches:
/// that is what makes it mixed).</para>
/// </summary>
public static class Matrix {
    public static int Run(BenchOptions options, MatrixOptions matrix) {
        var runs = matrix.VectorCounts.Length * matrix.CacheMB.Length * matrix.EngineNames.Length;
        var csvPath = Path.GetFullPath(matrix.CsvPath);
        var htmlPath = Path.GetFullPath(matrix.HtmlPath ?? Path.ChangeExtension(matrix.CsvPath, ".html"));
        var root = Path.Combine(options.DataDir, $"vecidxbench_{Environment.ProcessId}");
        Directory.CreateDirectory(root);

        Console.WriteLine($"Matrix: {runs} runs — {matrix.EngineNames.Length} engines x "
            + $"{matrix.VectorCounts.Length} corpus sizes x {matrix.CacheMB.Length} cache budgets, "
            + $"{options.Dimensions} dimensions{(options.SkipSearches ? ", searches skipped" : "")}");
        Console.WriteLine($"  n:      {string.Join(", ", matrix.VectorCounts.Select(v => v.ToString("N0")))}");
        Console.WriteLine($"  cache:  {string.Join(", ", matrix.CacheMB.Select(v => v + " MB"))}");
        Console.WriteLine($"  engine: {string.Join(", ", matrix.EngineNames.Select(Engines.DisplayName))}");
        Console.WriteLine($"  csv:    {csvPath}");
        Console.WriteLine($"  html:   {htmlPath}");
        // Worth saying once rather than leaving a reader to wonder why a column is flat: an index
        // without a cache budget cannot respond to the cache axis, so its rows repeat along it.
        if (matrix.EngineNames.Contains(Engines.Memory) && matrix.CacheMB.Length > 1)
            Console.WriteLine("  note:   MemorySemanticIndex has no cache budget to spend, so its rows repeat "
                + "across the cache axis (which makes them a noise floor for the others).");
        Console.WriteLine();

        using var csv = new StreamWriter(csvPath, append: false, Encoding.UTF8) { AutoFlush = true };
        csv.WriteLine(string.Join(',', header));

        var clock = Stopwatch.StartNew();
        var done = 0;
        var failed = 0;
        var report = new List<HtmlReport.Row>();
        try {
            foreach (var n in matrix.VectorCounts) {
                // The corpus depends on the size alone, so an in-process sweep builds it once per
                // size and every engine at that size answers the identical queries.
                Corpus? shared = null;
                foreach (var cacheMB in matrix.CacheMB) {
                    foreach (var engine in matrix.EngineNames) {
                        var o = options.Clone();
                        o.N = n;
                        o.CacheMB = cacheMB;
                        var label = Engines.DisplayName(engine);
                        var dir = Path.Combine(root, $"n{n}_c{cacheMB}_{engine}");
                        var runClock = Stopwatch.StartNew();
                        Console.Write($"[{++done}/{runs}] n={n:N0} cache={cacheMB} MB {label} ... ");

                        BenchResult res;
                        if (o.InProcess) {
                            shared ??= Corpus.Build(o);
                            Progress.SendTo(text => ProgressDisplay.Show(text));
                            try {
                                res = BenchRunner.Run(engine, shared, o, dir);
                            } catch (Exception ex) {
                                res = new BenchResult { Engine = engine, N = n, Error = ex.ToString() };
                            }
                            Progress.SendToConsole();
                        } else {
                            res = ChildProcess.Run(engine, o, dir, label);
                        }
                        runClock.Stop();
                        ProgressDisplay.Clear();

                        csv.WriteLine(row(engine, o, res, runClock.Elapsed.TotalSeconds));
                        report.Add(new HtmlReport.Row(n, cacheMB, engine, res));
                        if (res.Error is not null) {
                            failed++;
                            Console.WriteLine($"FAILED: {res.Error.Split('\n')[0].Trim()}");
                        } else {
                            Console.WriteLine($"{rate(res.Rate("Index"))}/s index, {rate(res.Rate("Update"))}/s update, "
                                + $"{rate(res.Rate("Mixed"))}/s mixed, {res.ManagedMB:0.0} MB  ({runClock.Elapsed.TotalSeconds:N1} s)");
                        }
                        tryDelete(dir);
                    }
                }
            }
        } finally {
            tryDelete(root);
            // Written in the finally so an interrupted sweep still reports what it measured; the
            // CSV has streamed all along, and the report is built from the same rows.
            if (report.Count > 0) HtmlReport.Write(htmlPath, options, matrix, report);
        }

        Console.WriteLine();
        Console.WriteLine($"{done} runs in {clock.Elapsed.TotalMinutes:N1} min"
            + (failed > 0 ? $", {failed} failed (see the error column)" : ""));
        Console.WriteLine($"  csv:  {csvPath}");
        Console.WriteLine($"  html: {htmlPath}");
        return failed > 0 ? 1 : 0;
    }

    // Inputs first, then the measurements, so a spreadsheet reads left to right as settings then
    // results. Add a column by adding it here and to row() — nothing else knows the shape.
    static readonly string[] header = [
        "engine", "engine_label", "n", "dimensions", "cache_mb", "clusters", "noise", "batch",
        "accuracy", "hnsw_m", "hnsw_ef_add", "hnsw_ef",
        "inserts_per_s", "updates_per_s", "mixed_per_s", "mem_mb",
        "warm_mem_mb", "disk_mb", "save_ms", "run_seconds", "error",
    ];

    static string row(string engine, BenchOptions o, BenchResult res, double seconds) => string.Join(',', [
        csvField(engine),
        csvField(Engines.DisplayName(engine)),
        num(o.N),
        num(o.Dimensions),
        num(o.CacheMB),
        num(o.Clusters),
        num(o.ClusterNoise),
        num(o.BatchSize),
        num(o.Accuracy),
        num(o.HnswConnectivity),
        num(o.HnswExpansionAdd),
        num(o.HnswExpansionSearch),
        num(res.Rate("Index")),
        num(res.Rate("Update")),
        num(res.Rate("Mixed")),
        num(res.Error is null ? res.ManagedMB : null),
        num(res.Error is null ? res.WarmManagedMB : null),
        num(res.Error is null ? res.DiskMB : null),
        num(res.Ms("Save")),
        num(seconds),
        csvField(res.Error is null ? "" : res.Error.Split('\n')[0].Trim()),
    ]);

    /// <summary>Invariant decimals and no thousands separators: a CSV is read by a machine, and
    /// this project runs with invariant globalization anyway.</summary>
    static string num(double? v) => v is null ? "" : v.Value.ToString("0.###", CultureInfo.InvariantCulture);

    static string csvField(string s)
        => s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? '"' + s.Replace("\"", "\"\"").Replace('\n', ' ').Replace("\r", "") + '"'
            : s;

    static string rate(double? v) => v is null ? "-"
        : v >= 1_000_000 ? $"{v.Value / 1e6:0.0}M"
        : v >= 1_000 ? $"{v.Value / 1e3:0.0}k"
        : v.Value.ToString("0.#", CultureInfo.InvariantCulture);

    static void tryDelete(string dir) {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* still held by a child or an AV scanner; the temp root is cleaned next run */ }
    }
}
