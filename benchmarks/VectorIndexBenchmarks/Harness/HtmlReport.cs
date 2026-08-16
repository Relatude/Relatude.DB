using System.Globalization;
using System.Text;

namespace VectorIndexBenchmarks.Harness;

/// <summary>
/// The matrix sweep's HTML report: one chart per measurement, the corpus size along the X axis,
/// every (engine, cache budget) pair a line — the engine gives the colour, the cache budget the
/// dash pattern (the tightest dashes are the smallest budget, a solid line the largest). One chart
/// therefore carries the whole grid for its measurement, and the report is as long as the metric
/// list rather than the metric list times the corpus sizes.
///
/// <para>Self-contained by construction — inline CSS and inline SVG, no scripts, no external
/// resources — so the file can be mailed, archived or opened from a share and look the same.</para>
/// </summary>
public static class HtmlReport {
    /// <summary>One run of the sweep: where it sat in the grid, and what it measured.</summary>
    public sealed record Row(int N, int CacheMB, string Engine, BenchResult Result);

    /// <summary>One charted measurement: its name, its unit (the Y axis), where it sits on a
    /// result, and how to read it — which direction wins, and whether the axis is logarithmic
    /// (everything but the percentages: engines sit orders of magnitude apart, and a linear axis
    /// would flatten every line but the fastest one's). A measurement no run produced (skipped
    /// searches, unsupported phases) gets no chart.</summary>
    sealed record Metric(string Title, string Unit, Func<BenchResult, double?> Value, bool LowerIsBetter = false, bool Log = true);

    static readonly Metric[] metrics = [
        new("Index", "inserts/s", r => r.Rate("Index")),
        new("Update", "updates/s", r => r.Rate("Update")),
        new("Remove", "removes/s", r => r.Rate("Remove")),
        new("Mixed", "ops/s", r => r.Rate("Mixed")),
        new("Top10", "queries/s", r => r.Rate("Top10")),
        new("Top100", "queries/s", r => r.Rate("Top100")),
        new("NoFloor", "queries/s", r => r.Rate("NoFloor")),
        new("Filter", "queries/s", r => r.Rate("Filter")),
        new("Recall", "%", r => r.Quality0To1("Recall") * 100, Log: false),
        new("Filter recall", "%", r => r.Quality0To1("FilterRecall") * 100, Log: false),
        new("Save", "ms", r => r.Ms("Save"), LowerIsBetter: true),
        new("Flush", "ms", r => r.Ms("Flush"), LowerIsBetter: true),
        new("Open", "ms", r => r.Ms("Open"), LowerIsBetter: true),
        new("Cold search", "ms", r => r.Ms("Cold"), LowerIsBetter: true),
        new("Memory", "MB", r => r.ManagedMB, LowerIsBetter: true),
        new("Warm memory", "MB", r => r.WarmManagedMB, LowerIsBetter: true),
        new("Working set", "MB", r => r.WorkingSetMB, LowerIsBetter: true),
        new("Disk", "MB", r => r.DiskMB, LowerIsBetter: true),
    ];

    /// <summary>A fixed colour per engine, the same in every chart and every report, so a reader
    /// who has seen one report recognizes the lines in the next. Engines without an entry draw
    /// from the palette in order.</summary>
    static readonly Dictionary<string, string> engineColors = new() {
        [Engines.Memory] = "#2563eb",     // blue
        [Engines.IVS] = "#059669",        // green
        [Engines.Hnsw] = "#ea580c",       // orange
        [Engines.HnswLowMem] = "#c026d3", // magenta
        [Engines.USearch] = "#7c3aed",    // violet
        [Engines.SqliteVec] = "#475569",  // slate
        [Engines.IVSExact] = "#0891b2",   // cyan
        [Engines.IVSLowMem] = "#a16207",  // amber
    };
    static readonly string[] fallbackColors = ["#dc2626", "#0d9488", "#4f46e5", "#ca8a04"];

    // The chart geometry, shared by every chart so the report reads as one grid. Wider than the
    // per-section charts of a smaller report would be: each one now carries engines x budgets lines.
    const int chartW = 600, chartH = 320;
    const int padL = 62, padR = 16, padT = 34, padB = 42;

    public static void Write(string path, BenchOptions options, MatrixOptions matrix, IReadOnlyList<Row> rows) {
        var colors = matrix.EngineNames.Select((e, i) => (e, c: engineColors.TryGetValue(e, out var c) ? c : fallbackColors[i % fallbackColors.Length]))
            .ToDictionary(p => p.e, p => p.c);
        var dashes = dashPatterns(matrix.CacheMB);
        var html = new StringBuilder();

        html.Append($$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <title>VectorIndexBenchmarks matrix</title>
            <style>
              :root { color-scheme: light; }
              body { font: 14px/1.5 system-ui, "Segoe UI", sans-serif; color: #1e293b; background: #f8fafc; margin: 0; padding: 2rem; }
              main { max-width: 1320px; margin: 0 auto; }
              h1 { font-size: 1.5rem; margin: 0 0 .25rem; }
              p.meta { color: #64748b; margin: .15rem 0; }
              .legend { display: flex; flex-wrap: wrap; gap: .4rem 1.2rem; margin: .75rem 0 0; align-items: center; }
              .legend span { display: inline-flex; align-items: center; gap: .45rem; }
              .legend i { width: 1.4em; height: .3em; border-radius: 2px; display: inline-block; }
              .legend .grouplabel { color: #94a3b8; }
              .charts { display: flex; flex-wrap: wrap; gap: 1rem; margin-top: 1.25rem; }
              .chart { background: #fff; border: 1px solid #e2e8f0; border-radius: 8px; padding: .4rem; }
              details { margin-top: 1.5rem; }
              summary { cursor: pointer; color: #475569; }
              table { border-collapse: collapse; font-size: 12.5px; margin-top: .5rem; background: #fff; }
              th, td { border: 1px solid #e2e8f0; padding: .25rem .55rem; text-align: right; }
              th:first-child, td:first-child { text-align: left; }
              thead th { background: #f1f5f9; position: sticky; top: 0; }
              .err { color: #b91c1c; }
              svg text { font: 11px system-ui, "Segoe UI", sans-serif; fill: #475569; }
              svg .title { font-size: 13px; font-weight: 600; fill: #1e293b; }
              svg .unit { fill: #94a3b8; }
            </style>
            </head>
            <body>
            <main>
            """);

        // ---- Header: what was run, with what held fixed, and how to read the lines ----------------
        html.Append($"""
            <h1>VectorIndexBenchmarks — matrix sweep</h1>
            <p class="meta">{esc(DateTime.Now.ToString("yyyy-MM-dd HH:mm"))} &middot; {rows.Count} runs &middot;
              {options.Dimensions} dimensions &middot; {(options.Clusters > 0 ? $"{options.Clusters} clusters, noise {options.ClusterNoise:0.##}" : "uniformly random directions")} &middot;
              batch {options.BatchSize:N0} &middot; accuracy {options.Accuracy:0.##} &middot; hnsw m={options.HnswConnectivity} efAdd={options.HnswExpansionAdd} ef={options.HnswExpansionSearch}
              {(options.SkipSearches ? "&middot; search phases skipped" : "")}</p>
            <p class="meta">One chart per measurement: vectors indexed along the X axis, colour = engine,
              dash pattern = cache budget (tighter dashes, smaller budget). Every chart says under its
              unit whether up or down wins, and the Y axes are logarithmic (except the percentages) —
              the engines sit orders of magnitude apart, so on these axes equal spacing is a constant
              factor. Hover any point for its exact value. MemorySemanticIndex has no cache budget to
              spend, so its dashed variants sit on top of each other by design — a reference for the
              others. A missing point is a failed or unsupported run, not a zero.</p>
            <div class="legend"><span class="grouplabel">engine</span>{string.Join("", matrix.EngineNames.Select(e =>
                $"<span><i style=\"background:{colors[e]}\"></i>{esc(Engines.ShortName(e))}</span>"))}</div>
            <div class="legend"><span class="grouplabel">cache</span>{string.Join("", matrix.CacheMB.Select((mb, i) =>
                $"<span><svg width=\"34\" height=\"8\"><line x1=\"1\" y1=\"4\" x2=\"33\" y2=\"4\" stroke=\"#1e293b\" stroke-width=\"2\"{dash(dashes[i])}/></svg>{mb} MB</span>"))}</div>
            """);

        // ---- One chart per measurement any run produced, all sizes and budgets in it --------------
        html.Append("<div class=\"charts\">\n");
        foreach (var metric in metrics) {
            // Series in legend order: engines outermost so the colours group, budgets within.
            var series = new List<(string engine, int cacheIdx, double?[] values)>();
            foreach (var engine in matrix.EngineNames)
                for (var c = 0; c < matrix.CacheMB.Length; c++)
                    series.Add((engine, c, matrix.VectorCounts.Select(n => value(rows, engine, n, matrix.CacheMB[c], metric)).ToArray()));
            if (series.All(s => s.values.All(v => v is null))) continue; // nothing measured it: no chart
            html.Append(chart(metric, matrix, series, colors, dashes));
        }
        html.Append("</div>\n");

        var failures = rows.Where(r => r.Result.Error is not null).ToList();
        if (failures.Count > 0) {
            html.Append("<p class=\"err\">Failed runs:</p><ul class=\"err\">");
            foreach (var f in failures)
                html.Append($"<li>{esc(Engines.ShortName(f.Engine))} at n={f.N:N0}, {f.CacheMB} MB: {esc(firstLine(f.Result.Error!))}</li>");
            html.Append("</ul>");
        }

        html.Append(rawTable(rows, matrix));
        html.Append("</main>\n</body>\n</html>\n");
        File.WriteAllText(path, html.ToString(), Encoding.UTF8);
    }

    static double? value(IReadOnlyList<Row> rows, string engine, int n, int cacheMB, Metric metric) {
        var row = rows.FirstOrDefault(r => r.Engine == engine && r.N == n && r.CacheMB == cacheMB);
        if (row is null || (row.Result.Error is not null && row.Result.Phases.Count == 0)) return null;
        var v = metric.Value(row.Result);
        return v is null or double.NaN ? null : v;
    }

    /// <summary>One dash pattern per cache budget, smallest budget the tightest dashes and the
    /// largest a solid line, so "more cache" reads as "more line" without consulting the legend.</summary>
    static string[] dashPatterns(int[] cacheMB) {
        // Patterns from tight to open; the last budget is always solid ("" = no dash attribute).
        string[] ladder = ["2 4", "5 4", "8 4", "12 4", "17 4"];
        var patterns = new string[cacheMB.Length];
        for (var i = 0; i < cacheMB.Length; i++)
            patterns[i] = i == cacheMB.Length - 1 ? ""
                : ladder[Math.Min(i, ladder.Length - 1)];
        return patterns;
    }

    static string dash(string pattern) => pattern.Length == 0 ? "" : $" stroke-dasharray=\"{pattern}\"";

    // ---- The chart: one measurement, ordinal corpus sizes on X, logarithmic Y --------------------
    // Both axes of the grid are swept in rough powers of ten, so the X axis is ordinal (1k, 5k,
    // 10k... equally spaced): what a reader compares is adjacent steps, not distances. The Y axis
    // is logarithmic for everything but percentages — the whole point of the comparison is engines
    // that differ by factors, and a linear axis would pin every line but the fastest to the floor.
    static string chart(Metric metric, MatrixOptions matrix, List<(string engine, int cacheIdx, double?[] values)> series,
        Dictionary<string, string> colors, string[] dashes) {
        var counts = matrix.VectorCounts;
        var plotW = chartW - padL - padR;
        var plotH = chartH - padT - padB;
        double x(int i) => padL + (counts.Length == 1 ? plotW / 2.0 : plotW * i / (double)(counts.Length - 1));

        // The Y mapping and its gridlines, log or linear. A log axis cannot place zero or less, so
        // such values are treated as unplottable (they stay in the tooltip-free raw table).
        var plottable = series.SelectMany(s => s.values).Where(v => v is > 0).Select(v => v!.Value).ToList();
        var linear = !metric.Log || plottable.Count == 0;
        Func<double, double> y;
        bool plottableValue(double v) => linear || v > 0;
        var grid = new List<(double v, bool labeled)>();
        if (linear) {
            var max = series.SelectMany(s => s.values).Where(v => v.HasValue).Max(v => v!.Value);
            var (top, ticks) = niceScale(max <= 0 ? 1 : max);
            y = v => padT + plotH - plotH * v / top;
            for (var t = 0; t <= ticks; t++) grid.Add((top * t / ticks, true));
        } else {
            var loPow = Math.Floor(Math.Log10(plottable.Min()));
            var hiPow = Math.Ceiling(Math.Log10(plottable.Max()));
            if (hiPow <= loPow) hiPow = loPow + 1;
            y = v => padT + plotH - plotH * (Math.Log10(v) - loPow) / (hiPow - loPow);
            var decades = (int)(hiPow - loPow);
            for (var p = loPow; p <= hiPow; p++) {
                grid.Add((Math.Pow(10, p), true));
                // Room permitting, faint 2x and 5x lines inside each decade keep a factor of two
                // readable without a labeled tick.
                if (decades <= 4 && p < hiPow) {
                    grid.Add((2 * Math.Pow(10, p), false));
                    grid.Add((5 * Math.Pow(10, p), false));
                }
            }
        }

        var svg = new StringBuilder();
        svg.Append($"<div class=\"chart\"><svg width=\"{chartW}\" height=\"{chartH}\" viewBox=\"0 0 {chartW} {chartH}\" role=\"img\" aria-label=\"{esc(metric.Title)}\">\n");
        svg.Append($"<text class=\"title\" x=\"{padL}\" y=\"18\">{esc(metric.Title)}</text>");
        svg.Append($"<text class=\"unit\" x=\"{chartW - padR}\" y=\"18\" text-anchor=\"end\">{esc(metric.Unit)}"
            + $" &#183; {(metric.LowerIsBetter ? "&#9660; lower is better" : "&#9650; higher is better")}{(linear ? "" : " &#183; log scale")}</text>\n");

        foreach (var (v, labeled) in grid) {
            var ty = y(v);
            svg.Append($"<line x1=\"{padL}\" y1=\"{f(ty)}\" x2=\"{chartW - padR}\" y2=\"{f(ty)}\" stroke=\"{(labeled ? "#e2e8f0" : "#f1f5f9")}\"/>");
            if (labeled) svg.Append($"<text x=\"{padL - 6}\" y=\"{f(ty + 4)}\" text-anchor=\"end\">{fmt(v)}</text>\n");
        }
        // X labels: one per corpus size.
        for (var i = 0; i < counts.Length; i++)
            svg.Append($"<text x=\"{f(x(i))}\" y=\"{chartH - padB + 16}\" text-anchor=\"middle\">{fmt(counts[i])}</text>");
        svg.Append($"<text x=\"{padL + plotW / 2}\" y=\"{chartH - 6}\" text-anchor=\"middle\" class=\"unit\">vectors</text>\n");

        // One polyline per (engine, budget), broken at missing points. Every point is a small dot
        // wrapped in a larger invisible hover target carrying the exact value as a tooltip, so the
        // value is reachable without aiming at a three-pixel circle.
        foreach (var (engine, cacheIdx, values) in series) {
            var color = colors[engine];
            var dashAttr = dash(dashes[cacheIdx]);
            var segment = new List<string>();
            void flush() {
                if (segment.Count > 1)
                    svg.Append($"<polyline points=\"{string.Join(' ', segment)}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"2\"{dashAttr}/>\n");
                segment.Clear();
            }
            for (var i = 0; i < values.Length; i++) {
                if (values[i] is not double v || !plottableValue(v)) { flush(); continue; }
                segment.Add($"{f(x(i))},{f(y(v))}");
            }
            flush();
            for (var i = 0; i < values.Length; i++) {
                if (values[i] is not double v || !plottableValue(v)) continue;
                svg.Append($"<circle cx=\"{f(x(i))}\" cy=\"{f(y(v))}\" r=\"2.5\" fill=\"{color}\"/>"
                    + $"<circle cx=\"{f(x(i))}\" cy=\"{f(y(v))}\" r=\"9\" fill=\"transparent\">"
                    + $"<title>{esc(Engines.ShortName(engine))}, {matrix.CacheMB[cacheIdx]} MB cache @ {counts[i]:N0} vectors: {fmt(v)} {esc(metric.Unit)}</title></circle>");
            }
            svg.Append('\n');
        }
        svg.Append("</svg></div>\n");
        return svg.ToString();
    }

    // ---- The raw numbers behind the charts, folded away until asked for --------------------------
    static string rawTable(IReadOnlyList<Row> rows, MatrixOptions matrix) {
        var present = metrics.Where(m => rows.Any(r => r.Result.Error is null && m.Value(r.Result) is not null and not double.NaN)).ToList();
        var html = new StringBuilder();
        html.Append("<details><summary>Raw numbers</summary><table><thead><tr><th>Engine</th><th>Vectors</th><th>Cache MB</th>");
        foreach (var m in present) html.Append($"<th>{esc(m.Title)} ({esc(m.Unit)})</th>");
        html.Append("</tr></thead><tbody>");
        foreach (var n in matrix.VectorCounts) {
            foreach (var cache in matrix.CacheMB) {
                foreach (var engine in matrix.EngineNames) {
                    var row = rows.FirstOrDefault(r => r.Engine == engine && r.N == n && r.CacheMB == cache);
                    if (row is null) continue;
                    html.Append($"<tr><td>{esc(Engines.ShortName(engine))}</td><td>{n:N0}</td><td>{cache}</td>");
                    foreach (var m in present) {
                        var v = row.Result.Error is not null && row.Result.Phases.Count == 0 ? null : m.Value(row.Result);
                        html.Append($"<td>{(v is null or double.NaN ? "" : fmt(v.Value))}</td>");
                    }
                    html.Append("</tr>");
                }
            }
        }
        html.Append("</tbody></table></details>\n");
        return html.ToString();
    }

    /// <summary>The smallest 1/2/2.5/4/5 x 10^k at or above <paramref name="v"/>, and a tick count
    /// that divides it evenly — so the Y axis tops out at a round number and every gridline label
    /// is one too (a top of 250k gets five 50k steps, never four 62.5k ones).</summary>
    static (double top, int ticks) niceScale(double v) {
        var mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var (m, ticks) in ((double m, int ticks)[])[(1, 4), (2, 4), (2.5, 5), (4, 4), (5, 5)])
            if (v <= m * mag * 1.0000001) return (m * mag, ticks);
        return (10 * mag, 4);
    }

    static string fmt(double v)
        => v >= 1_000_000 ? (v / 1e6).ToString("0.##", CultureInfo.InvariantCulture) + "M"
            : v >= 1_000 ? (v / 1e3).ToString("0.##", CultureInfo.InvariantCulture) + "k"
            : v >= 10 ? v.ToString("0.#", CultureInfo.InvariantCulture)
            : v.ToString("0.##", CultureInfo.InvariantCulture);

    static string f(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    static string esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    static string firstLine(string s) => s.Split('\n')[0].Trim();
}
