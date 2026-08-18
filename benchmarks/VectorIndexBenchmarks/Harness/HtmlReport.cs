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
/// <para>Styled after <c>docs/manual.html</c>: the same palette, fonts and typography, a fixed
/// sidebar with the section tree, and the manual's three-state theme button (auto / light / dark,
/// remembered). The charts are drawn in the theme's own colours — gridlines and labels from the
/// palette variables, one variable per engine with a light and a dark value — so a report read in
/// dark mode is not a light document with dark holes in it.</para>
///
/// <para>Self-contained by construction: inline CSS, inline SVG and one small inline script, no
/// external resources at all, so the file can be mailed, archived or opened from a share.</para>
/// </summary>
public static class HtmlReport {
    /// <summary>One run of the sweep: where it sat in the grid, and what it measured.</summary>
    public sealed record Row(int N, int CacheMB, string Engine, BenchResult Result);

    /// <summary>One charted measurement: the section it belongs to, its name, its unit (the Y
    /// axis), where it sits on a result, and how to read it — which direction wins, and whether the
    /// axis is logarithmic (everything but the percentages: engines sit orders of magnitude apart,
    /// and a linear axis would flatten every line but the fastest one's). A measurement no run
    /// produced (skipped searches, unsupported phases) gets no chart.</summary>
    sealed record Metric(string Group, string Title, string Unit, Func<BenchResult, double?> Value,
        bool LowerIsBetter = false, bool Log = true) {
        public string Id => "m-" + Title.ToLowerInvariant().Replace(' ', '-');
    }

    const string writes = "Writes", searches = "Searches", accuracy = "Accuracy",
        durability = "Durability and restart", footprint = "Footprint";

    static readonly Metric[] metrics = [
        new(writes, "Index", "inserts/s", r => r.Rate("Index")),
        new(writes, "Update", "updates/s", r => r.Rate("Update")),
        new(writes, "Remove", "removes/s", r => r.Rate("Remove")),
        new(writes, "Mixed", "ops/s", r => r.Rate("Mixed")),
        new(searches, "Top10", "queries/s", r => r.Rate("Top10")),
        new(searches, "Top100", "queries/s", r => r.Rate("Top100")),
        new(searches, "NoFloor", "queries/s", r => r.Rate("NoFloor")),
        new(searches, "Filter", "queries/s", r => r.Rate("Filter")),
        new(accuracy, "Recall", "%", r => r.Quality0To1("Recall") * 100, Log: false),
        new(accuracy, "Filter recall", "%", r => r.Quality0To1("FilterRecall") * 100, Log: false),
        new(durability, "Save", "ms", r => r.Ms("Save"), LowerIsBetter: true),
        new(durability, "Flush", "ms", r => r.Ms("Flush"), LowerIsBetter: true),
        new(durability, "Open", "ms", r => r.Ms("Open"), LowerIsBetter: true),
        new(durability, "Cold search", "ms", r => r.Ms("Cold"), LowerIsBetter: true),
        new(footprint, "Memory", "MB", r => r.ManagedMB, LowerIsBetter: true),
        new(footprint, "Warm memory", "MB", r => r.WarmManagedMB, LowerIsBetter: true),
        new(footprint, "Working set", "MB", r => r.WorkingSetMB, LowerIsBetter: true),
        new(footprint, "Disk", "MB", r => r.DiskMB, LowerIsBetter: true),
    ];

    /// <summary>A fixed palette slot per engine, so a reader who has seen one report recognizes the
    /// lines in the next. The slot is a CSS variable with a light and a dark value — the light ones
    /// are dark enough to read on white, the dark ones bright enough to read on the dark
    /// background. Engines without a slot take the remaining ones in order.</summary>
    static readonly Dictionary<string, int> engineSlots = new() {
        [Engines.Memory] = 0, [Engines.IVS] = 1, [Engines.Hnsw] = 2,
        [Engines.USearch] = 3, [Engines.SqliteVec] = 4, [Engines.IVSExact] = 5, [Engines.IVSLowMem] = 6,
    };
    static readonly (string light, string dark)[] palette = [
        ("#2f5bd7", "#7fa2ff"), // blue
        ("#0f7b52", "#42c896"), // green
        ("#c2410c", "#fb923c"), // orange
        ("#a21caf", "#e879f9"), // magenta
        ("#6d28d9", "#a78bfa"), // violet
        ("#475569", "#94a3b8"), // slate
        ("#0e7490", "#22d3ee"), // cyan
        ("#a16207", "#fbbf24"), // amber
    ];

    // The chart geometry, shared by every chart so the report reads as one grid. The SVG scales to
    // its column through the viewBox, so these are proportions rather than pixels.
    const int chartW = 620, chartH = 340;
    const int padL = 66, padR = 18, padT = 34, padB = 44;

    public static void Write(string path, BenchOptions options, MatrixOptions matrix, IReadOnlyList<Row> rows) {
        var slots = assignSlots(matrix.EngineNames);
        var dashes = dashPatterns(matrix.CacheMB);
        var charted = metrics.Where(m => rows.Any(r => r.Result.Error is null && plottable(m.Value(r.Result)))).ToList();
        var groups = charted.Select(m => m.Group).Distinct().ToList();

        var html = new StringBuilder();
        head(html, slots);
        sidebar(html, groups, charted);
        body(html, options, matrix, rows, charted, groups, slots, dashes);
        script(html);
        File.WriteAllText(path, html.ToString(), Encoding.UTF8);
    }

    // ---- The document shell, palette and typography lifted from docs/manual.html -----------------
    static void head(StringBuilder html, Dictionary<string, int> slots) {
        // The engine colours are emitted three times for the same reason the manual emits its
        // palette three times: the base, the system-preference dark, and the explicitly chosen dark.
        var lightVars = string.Join("\n  ", slots.Select(s => $"--eng-{s.Value}: {palette[s.Value % palette.Length].light};"));
        var darkVars = string.Join("\n    ", slots.Select(s => $"--eng-{s.Value}: {palette[s.Value % palette.Length].dark};"));

        html.Append($$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>VectorIndexBenchmarks — matrix sweep</title>
            <script>
            // Applied before first paint so an explicitly chosen theme does not flash the
            // other one. localStorage access itself can throw on file:// origins, so guard.
            (function () {
              try {
                var t = window.localStorage.getItem("relatude-bench-theme-v1");
                if (t === "dark" || t === "light") document.documentElement.setAttribute("data-theme", t);
              } catch (e) {}
            })();
            </script>
            <style>
            /* Light is the base palette. Dark is applied in two places: under the system
               preference (unless the reader has explicitly chosen light), and under an
               explicit data-theme="dark" set by the theme button. Keep the two dark blocks
               in sync — they are deliberately identical. */
            :root {
              color-scheme: light;
              --bg: #ffffff;
              --bg-side: #f7f8fa;
              --bg-code: #f6f8fa;
              --bg-hover: #ececf0;
              --bg-active: #e4e9f7;
              --fg: #1f2328;
              --fg-dim: #656d76;
              --fg-faint: #8b949e;
              --accent: #2f5bd7;
              --accent-soft: #dbe4fb;
              --border: #d8dce3;
              --border-soft: #e8ebef;
              --grid: #dfe3ea;
              --grid-faint: #eef1f5;
              --bad: #b91c1c;
              --side-w: 300px;
              --mono: ui-monospace, SFMono-Regular, "SF Mono", Menlo, Consolas, "Liberation Mono", monospace;
              --sans: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif;
              {{lightVars}}
            }
            @media (prefers-color-scheme: dark) {
              :root:not([data-theme="light"]) {
                color-scheme: dark;
                --bg: #0f1116;
                --bg-side: #14161c;
                --bg-code: #171a21;
                --bg-hover: #1e222b;
                --bg-active: #232a3d;
                --fg: #e3e6eb;
                --fg-dim: #9aa3b2;
                --fg-faint: #6e7787;
                --accent: #7fa2ff;
                --accent-soft: #22304f;
                --border: #2a2f3a;
                --border-soft: #21252e;
                --grid: #262b35;
                --grid-faint: #1c2029;
                --bad: #f87171;
                {{darkVars}}
              }
            }
            :root[data-theme="dark"] {
              color-scheme: dark;
              --bg: #0f1116;
              --bg-side: #14161c;
              --bg-code: #171a21;
              --bg-hover: #1e222b;
              --bg-active: #232a3d;
              --fg: #e3e6eb;
              --fg-dim: #9aa3b2;
              --fg-faint: #6e7787;
              --accent: #7fa2ff;
              --accent-soft: #22304f;
              --border: #2a2f3a;
              --border-soft: #21252e;
              --grid: #262b35;
              --grid-faint: #1c2029;
              --bad: #f87171;
              {{darkVars}}
            }
            * { box-sizing: border-box; }
            html { scroll-behavior: smooth; }
            body {
              margin: 0;
              font-family: var(--sans);
              font-size: 15.5px;
              line-height: 1.65;
              color: var(--fg);
              background: var(--bg);
            }

            /* ---------------------------------------------------------------- sidebar */
            #sidebar {
              position: fixed;
              top: 0; left: 0; bottom: 0;
              width: var(--side-w);
              background: var(--bg-side);
              border-right: 1px solid var(--border);
              display: flex;
              flex-direction: column;
              z-index: 40;
            }
            #brand {
              position: relative;
              padding: 16px 52px 12px 18px;
              border-bottom: 1px solid var(--border-soft);
            }
            #brand h1 { font-size: 14px; margin: 0 0 2px; letter-spacing: .01em; }
            #brand p { font-size: 11.5px; color: var(--fg-faint); margin: 0; }
            #brand p.back { margin-top: 8px; }
            #brand p.back a { color: var(--fg-dim); text-decoration: none; }
            #brand p.back a:hover { color: var(--accent); }
            #theme {
              position: absolute; top: 14px; right: 14px;
              width: 30px; height: 30px; padding: 0;
              border: 1px solid var(--border); border-radius: 8px;
              background: var(--bg); color: var(--fg-dim);
              font-size: 14px; line-height: 1; cursor: pointer;
              display: flex; align-items: center; justify-content: center;
            }
            #theme:hover { color: var(--accent); border-color: var(--accent); }
            #nav { flex: 1; overflow-y: auto; padding: 10px 8px 40px; }
            #nav a {
              display: block; padding: 4px 10px; border-radius: 6px;
              color: var(--fg-dim); text-decoration: none; font-size: 13px; line-height: 1.4;
            }
            #nav a:hover { background: var(--bg-hover); color: var(--fg); }
            #nav a.active { background: var(--bg-active); color: var(--accent); font-weight: 600; }
            #nav a.lvl1 {
              font-size: 11px; font-weight: 700; text-transform: uppercase;
              letter-spacing: .07em; color: var(--fg-faint); margin-top: 12px;
            }
            #nav a.lvl2 { margin-left: 10px; }

            /* ------------------------------------------------------------------ main */
            #main {
              margin-left: var(--side-w);
              padding: 46px 48px 200px;
              max-width: 1280px;
            }
            #main h1, #main h2, #main h3 { scroll-margin-top: 24px; line-height: 1.3; }
            #main h1 {
              font-size: 27px; margin: 0 0 20px; padding-bottom: 10px;
              border-bottom: 2px solid var(--border);
            }
            #main h2 {
              font-size: 21px; margin: 46px 0 14px; padding-bottom: 7px;
              border-bottom: 1px solid var(--border-soft);
            }
            #main h3 { font-size: 15px; margin: 0 0 6px; }
            #main p { margin: 0 0 14px; }
            #main ul { margin: 0 0 14px; padding-left: 26px; }
            #main li { margin-bottom: 5px; }
            #main a { color: var(--accent); text-decoration: none; }
            #main a:hover { text-decoration: underline; }
            #main code {
              font-family: var(--mono); font-size: .875em;
              background: var(--bg-code); border: 1px solid var(--border-soft);
              border-radius: 5px; padding: .12em .38em;
            }
            #main blockquote {
              margin: 0 0 16px; padding: 10px 16px;
              border-left: 3px solid var(--accent);
              background: var(--accent-soft);
              border-radius: 0 7px 7px 0;
            }
            #main blockquote p:last-child { margin-bottom: 0; }
            table { border-collapse: collapse; margin: 0 0 18px; font-size: 13.5px; }
            th, td {
              border: 1px solid var(--border-soft); padding: 7px 11px;
              text-align: left; vertical-align: top;
            }
            th { background: var(--bg-side); font-weight: 600; }
            tbody tr:nth-child(even) { background: color-mix(in srgb, var(--bg-side) 55%, transparent); }
            td.num, th.num { text-align: right; font-variant-numeric: tabular-nums; }
            .scroll { overflow-x: auto; }

            /* ----------------------------------------------------------------- charts */
            .charts {
              display: grid; gap: 18px; margin: 0 0 8px;
              grid-template-columns: repeat(auto-fit, minmax(420px, 1fr));
            }
            .chart {
              background: var(--bg); border: 1px solid var(--border-soft);
              border-radius: 9px; padding: 12px 14px 6px;
            }
            .chart .cap { font-size: 12px; color: var(--fg-faint); margin: 0 0 4px; }
            .chart svg { width: 100%; height: auto; display: block; }
            svg text { font: 11px var(--sans); fill: var(--fg-dim); }
            svg text.axis { fill: var(--fg-faint); }
            .swatch { width: 1.5em; height: .3em; border-radius: 2px; display: inline-block; vertical-align: middle; }
            .keys { display: flex; flex-wrap: wrap; gap: .35rem 1.3rem; align-items: center; margin: 0 0 18px; font-size: 13px; }
            .keys span { display: inline-flex; align-items: center; gap: .45rem; }
            .keys .lbl { color: var(--fg-faint); font-size: 11px; text-transform: uppercase; letter-spacing: .07em; }
            .bad { color: var(--bad); }
            .stars { color: var(--accent); font-size: 15px; letter-spacing: .05em; white-space: nowrap; }
            .stars .half { opacity: .45; }
            .stars .off { color: var(--fg-faint); opacity: .55; }
            .statnote { display: block; font-size: 11.5px; color: var(--fg-faint); font-variant-numeric: tabular-nums; }
            th .dir { font-weight: 400; font-size: 11px; color: var(--fg-faint); }
            p.cap { font-size: 12.5px; color: var(--fg-faint); }

            /* ------------------------------------------------------------- chrome bits */
            #progress {
              position: fixed; top: 0; left: var(--side-w); right: 0; height: 2px;
              background: var(--accent); width: 0; z-index: 50; transition: width .1s linear;
            }
            #toggle {
              position: fixed; top: 12px; left: 12px; z-index: 60; display: none;
              border: 1px solid var(--border); background: var(--bg); color: var(--fg);
              border-radius: 8px; width: 38px; height: 38px; font-size: 17px; cursor: pointer;
            }
            #totop {
              position: fixed; right: 22px; bottom: 22px; z-index: 45;
              border: 1px solid var(--border); background: var(--bg); color: var(--fg-dim);
              border-radius: 50%; width: 40px; height: 40px; cursor: pointer; font-size: 15px;
              opacity: 0; pointer-events: none; transition: opacity .18s;
            }
            #totop.show { opacity: 1; pointer-events: auto; }
            #totop:hover { color: var(--accent); border-color: var(--accent); }
            @media (max-width: 1080px) {
              #sidebar { transform: translateX(-100%); transition: transform .2s ease; }
              #sidebar.open { transform: none; box-shadow: 0 0 40px rgba(0,0,0,.25); }
              #main { margin-left: 0; padding: 66px 22px 200px; }
              #progress { left: 0; }
              #toggle { display: block; }
            }
            @media print {
              #sidebar, #toggle, #totop, #progress { display: none !important; }
              #main { margin: 0; max-width: none; padding: 0; }
              .chart { break-inside: avoid; }
            }
            </style>
            </head>
            <body>
            """);
    }

    static void sidebar(StringBuilder html, List<string> groups, List<Metric> charted) {
        html.Append("""

            <div id="progress"></div>
            <button id="toggle" title="Menu">&#9776;</button>

            <aside id="sidebar">
              <div id="brand">
                <h1>Vector index benchmarks</h1>
                <p>Relatude.DB &middot; matrix sweep</p>
                <p class="back"><a href="manual.html">&#8592; Data modelling &amp; querying manual</a></p>
                <button id="theme" type="button" title="Theme" aria-label="Theme"></button>
              </div>
              <nav id="nav">
                <a class="lvl1" href="#the-run">The run</a>
                <a class="lvl2" href="#summary">Summary</a>
                <a class="lvl2" href="#engines">Engines</a>
                <a class="lvl2" href="#reading">How to read the charts</a>

            """);
        foreach (var g in groups) {
            html.Append($"    <a class=\"lvl1\" href=\"#{slug(g)}\">{esc(g)}</a>\n");
            foreach (var m in charted.Where(m => m.Group == g))
                html.Append($"    <a class=\"lvl2\" href=\"#{m.Id}\">{esc(m.Title)}</a>\n");
        }
        html.Append("""
                <a class="lvl1" href="#raw-numbers">Raw numbers</a>
              </nav>
            </aside>

            <button id="totop" title="Back to top">&#9650;</button>

            <main id="main">

            """);
    }

    static void body(StringBuilder html, BenchOptions options, MatrixOptions matrix, IReadOnlyList<Row> rows,
        List<Metric> charted, List<string> groups, Dictionary<string, int> slots, string[] dashes) {
        var failures = rows.Where(r => r.Result.Error is not null).ToList();

        // ---- The run: what was swept, and what was held fixed -------------------------------------
        html.Append($$"""
            <h1 id="the-run">Vector index matrix sweep</h1>
            <p>{{rows.Count}} benchmark runs, one per (engine &times; corpus size &times; cache budget), each in its own
              process so its memory numbers are its own. Generated {{esc(DateTime.Now.ToString("yyyy-MM-dd HH:mm"))}}.</p>
            <div class="scroll"><table>
              <tbody>
                <tr><th>Vectors (X axis)</th><td>{{string.Join(", ", matrix.VectorCounts.Select(v => v.ToString("N0")))}}</td></tr>
                <tr><th>Cache budgets</th><td>{{string.Join(", ", matrix.CacheMB.Select(v => v + " MB"))}}</td></tr>
                <tr><th>Engines</th><td>{{string.Join(", ", matrix.EngineNames.Select(e => esc(Engines.ShortName(e))))}}</td></tr>
                <tr><th>Dimensions</th><td>{{options.Dimensions}}</td></tr>
                <tr><th>Vector distribution</th><td>{{(options.Clusters > 0 ? $"{options.Clusters} cluster centers, noise {options.ClusterNoise:0.##}" : "uniformly random directions")}}</td></tr>
                <tr><th>State save every</th><td>{{options.BatchSize:N0}} vectors</td></tr>
                <tr><th>IVS accuracy</th><td>{{options.Accuracy:0.##}} of clusters probed per search</td></tr>
                <tr><th>HNSW dials</th><td>m {{options.HnswConnectivity}}, efConstruction {{options.HnswExpansionAdd}}, efSearch {{options.HnswExpansionSearch}}</td></tr>
                <tr><th>Search phases</th><td>{{(options.SkipSearches ? "skipped (--skip-searches): the mixed phase still searches" : "measured")}}</td></tr>
              </tbody>
            </table></div>

            """);
        summary(html, matrix, rows, slots);
        html.Append("""
            <h2 id="engines">Engines</h2>
            <div class="scroll"><table>
              <thead><tr><th>Engine</th><th>What it is</th></tr></thead>
              <tbody>
            """);
        foreach (var e in matrix.EngineNames)
            html.Append($"    <tr><th><span class=\"swatch\" style=\"background: var(--eng-{slots[e]})\"></span> {esc(Engines.ShortName(e))}</th>"
                + $"<td>{esc(Engines.Description(e))}</td></tr>\n");
        html.Append("  </tbody>\n</table></div>\n");

        // ---- How to read the charts ---------------------------------------------------------------
        html.Append($$"""
            <h2 id="reading">How to read the charts</h2>
            <p>One chart per measurement. The X axis is the number of vectors indexed, the Y axis the
              measurement's own unit. Each line is one engine at one cache budget: <strong>colour is the
              engine</strong>, <strong>dash pattern is the cache budget</strong> — the tightest dashes are the
              smallest budget, a solid line the largest. Hover any point for its exact value.</p>
            <div class="keys"><span class="lbl">engine</span>{{string.Join("", matrix.EngineNames.Select(e =>
                $"<span><i class=\"swatch\" style=\"background: var(--eng-{slots[e]})\"></i>{esc(Engines.ShortName(e))}</span>"))}}</div>
            <div class="keys"><span class="lbl">cache</span>{{string.Join("", matrix.CacheMB.Select((mb, i) =>
                $"<span><svg width=\"36\" height=\"8\"><line x1=\"1\" y1=\"4\" x2=\"35\" y2=\"4\" style=\"stroke: var(--fg)\" stroke-width=\"2\"{dash(dashes[i])}/></svg>{mb} MB</span>"))}}</div>
            <blockquote>
              <p>Every chart says under its title whether <strong>higher</strong> or <strong>lower</strong> is better, and
                the Y axes are logarithmic except for the percentages — the engines sit orders of magnitude
                apart, so on these axes an equal distance is an equal factor. A missing point is a failed or
                unsupported run, never a zero. The in-memory index has no cache budget to spend, so its
                dashed variants lie on top of each other by design: it is the reference the others are read
                against.</p>
            </blockquote>
            """);

        // ---- The charts, grouped -------------------------------------------------------------------
        foreach (var g in groups) {
            html.Append($"\n<h2 id=\"{slug(g)}\">{esc(g)}</h2>\n<div class=\"charts\">\n");
            foreach (var metric in charted.Where(m => m.Group == g)) {
                var series = new List<(string engine, int cacheIdx, double?[] values)>();
                foreach (var engine in matrix.EngineNames)
                    for (var c = 0; c < matrix.CacheMB.Length; c++)
                        series.Add((engine, c, matrix.VectorCounts.Select(n => value(rows, engine, n, matrix.CacheMB[c], metric)).ToArray()));
                html.Append(chart(metric, matrix, series, slots, dashes));
            }
            html.Append("</div>\n");
        }

        // ---- Failures and the numbers behind the lines ---------------------------------------------
        if (failures.Count > 0) {
            html.Append("\n<h2 id=\"failed\">Failed runs</h2>\n<ul class=\"bad\">\n");
            foreach (var f in failures)
                html.Append($"  <li>{esc(Engines.ShortName(f.Engine))} at {f.N:N0} vectors, {f.CacheMB} MB: {esc(firstLine(f.Result.Error!))}</li>\n");
            html.Append("</ul>\n");
        }
        html.Append("\n<h2 id=\"raw-numbers\">Raw numbers</h2>\n");
        html.Append(rawTable(rows, matrix, charted));
        html.Append("</main>\n");
    }

    // ---- The summary: three stars-out-of-five verdicts per engine --------------------------------
    // One number per engine per category — the geometric mean of that measurement over every run
    // the engine made, which is the right average for values that differ by factors and keeps a
    // single corpus size from deciding the verdict. Stars are relative to the best engine in the
    // column: five for the best, and every factor of ten behind it costs two stars. The rule is
    // printed under the table, and the exact mean is in each cell's tooltip, so a star is a summary
    // of the charts rather than a judgement replacing them.
    sealed record Category(string Title, string Unit, Func<BenchResult, double?> Value, bool LowerIsBetter, string What);

    static readonly Category[] categories = [
        new("Indexing", "inserts/s", r => r.Rate("Index"), false, "vectors indexed per second, including the state save"),
        // Named "Memory use" rather than "Memory" so the column cannot be read as the engine of
        // that name, and scored the way every other column is: more stars is better, which here
        // means less memory.
        new("Memory use", "MB", r => r.ManagedMB, true, "managed heap held after the load, so fewer MB earns more stars"),
        new("Search", "queries/s", r => r.Rate("Top10"), false, "Top10 queries per second"),
    ];

    static void summary(StringBuilder html, MatrixOptions matrix, IReadOnlyList<Row> rows, Dictionary<string, int> slots) {
        // Per category: the engine means, and the best of them to score the others against.
        var means = categories.ToDictionary(c => c, c => matrix.EngineNames.ToDictionary(
            e => e,
            e => geoMean(rows.Where(r => r.Engine == e && r.Result.Error is null)
                .Select(r => c.Value(r.Result))
                .Where(plottable)
                .Select(v => v!.Value))));
        if (means.Values.All(m => m.Values.All(v => v is null))) return; // nothing to summarize

        html.Append("""
            <h2 id="summary">Summary</h2>
            <div class="scroll"><table>
              <thead><tr><th>Engine</th>
            """);
        // The header carries the direction, as the charts do: more stars is always better, and this
        // says what "better" is for the column.
        foreach (var c in categories)
            html.Append($"    <th class=\"num\">{esc(c.Title)}<br><span class=\"dir\">"
                + $"{(c.LowerIsBetter ? "&#9660; less is more stars" : "&#9650; more is more stars")}</span></th>\n");
        html.Append("  </tr></thead>\n  <tbody>\n");

        foreach (var engine in matrix.EngineNames) {
            html.Append($"    <tr><th><span class=\"swatch\" style=\"background: var(--eng-{slots[engine]})\"></span> {esc(Engines.ShortName(engine))}</th>\n");
            foreach (var c in categories) {
                var mine = means[c][engine];
                var best = c.LowerIsBetter
                    ? means[c].Values.Where(v => v is > 0).DefaultIfEmpty(null).Min()
                    : means[c].Values.Where(v => v is > 0).DefaultIfEmpty(null).Max();
                if (mine is not > 0 || best is not > 0) {
                    html.Append("      <td class=\"num\"><span class=\"stars off\">not measured</span></td>\n");
                    continue;
                }
                var behind = c.LowerIsBetter ? mine.Value / best.Value : best.Value / mine.Value;
                var stars = Math.Clamp(5 - 2 * Math.Log10(behind), 1, 5);
                var halves = (int)Math.Round(stars * 2, MidpointRounding.AwayFromZero);
                html.Append($"      <td class=\"num\">{starGlyphs(halves)}"
                    + $"<span class=\"statnote\" title=\"geometric mean over {rows.Count(r => r.Engine == engine && r.Result.Error is null && plottable(c.Value(r.Result)))} runs\">"
                    + $"{fmt(mine.Value)} {esc(c.Unit)}</span></td>\n");
            }
            html.Append("    </tr>\n");
        }
        html.Append($$"""
              </tbody>
            </table></div>
            <p class="cap">More stars is better in every column: five stars goes to the best engine in it —
              the fastest, or the one holding the least memory — and every factor of ten behind that costs
              two stars, down to one. The number beside the stars is the geometric mean over that engine's
              runs ({{string.Join("; ", categories.Select(c => esc(c.Title.ToLowerInvariant()) + ": " + esc(c.What)))}}), which is
              the average that suits values differing by factors. A verdict this compact hides the trade
              the charts below show: read them together.</p>

            """);
    }

    /// <summary>Half-star resolution, drawn as five glyphs so it renders the same everywhere: full
    /// stars, a faded one for the half, and hollow ones for the rest.</summary>
    static string starGlyphs(int halves) {
        var full = halves / 2;
        var half = halves % 2;
        var sb = new StringBuilder("<span class=\"stars\" aria-label=\"" + (halves / 2.0).ToString("0.#", CultureInfo.InvariantCulture) + " of 5\">");
        for (var i = 0; i < full; i++) sb.Append('★');
        if (half == 1) sb.Append("<span class=\"half\">★</span>");
        for (var i = full + half; i < 5; i++) sb.Append("<span class=\"off\">☆</span>");
        return sb.Append("</span>").ToString();
    }

    /// <summary>The geometric mean — the average of ratios, so an engine ten times faster at one
    /// corpus size and equal at another lands in the middle rather than being pulled by the biggest
    /// absolute number.</summary>
    static double? geoMean(IEnumerable<double> values) {
        double sum = 0;
        var n = 0;
        foreach (var v in values) {
            if (v <= 0) continue;
            sum += Math.Log(v);
            n++;
        }
        return n == 0 ? null : Math.Exp(sum / n);
    }

    static double? value(IReadOnlyList<Row> rows, string engine, int n, int cacheMB, Metric metric) {
        var row = rows.FirstOrDefault(r => r.Engine == engine && r.N == n && r.CacheMB == cacheMB);
        if (row is null || (row.Result.Error is not null && row.Result.Phases.Count == 0)) return null;
        var v = metric.Value(row.Result);
        return plottable(v) ? v : null;
    }

    static bool plottable(double? v) => v is not null and not double.NaN;

    static Dictionary<string, int> assignSlots(string[] engines) {
        var used = new HashSet<int>();
        var slots = new Dictionary<string, int>();
        foreach (var e in engines) {
            var slot = engineSlots.TryGetValue(e, out var s) && used.Add(s)
                ? s
                : Enumerable.Range(0, palette.Length).First(i => !used.Contains(i) && used.Add(i));
            slots[e] = slot;
        }
        return slots;
    }

    /// <summary>One dash pattern per cache budget, smallest budget the tightest dashes and the
    /// largest a solid line, so "more cache" reads as "more line" without consulting the legend.</summary>
    static string[] dashPatterns(int[] cacheMB) {
        string[] ladder = ["2 4", "5 4", "8 4", "12 4", "17 4"];
        var patterns = new string[cacheMB.Length];
        for (var i = 0; i < cacheMB.Length; i++)
            patterns[i] = i == cacheMB.Length - 1 ? "" : ladder[Math.Min(i, ladder.Length - 1)];
        return patterns;
    }

    static string dash(string pattern) => pattern.Length == 0 ? "" : $" stroke-dasharray=\"{pattern}\"";

    // ---- The chart: one measurement, ordinal corpus sizes on X, logarithmic Y --------------------
    // Both axes of the grid are swept in rough powers of ten, so the X axis is ordinal (1k, 5k,
    // 10k... equally spaced): what a reader compares is adjacent steps, not distances. The Y axis
    // is logarithmic for everything but percentages — the whole point of the comparison is engines
    // that differ by factors, and a linear axis would pin every line but the fastest to the floor.
    static string chart(Metric metric, MatrixOptions matrix, List<(string engine, int cacheIdx, double?[] values)> series,
        Dictionary<string, int> slots, string[] dashes) {
        var counts = matrix.VectorCounts;
        var plotW = chartW - padL - padR;
        var plotH = chartH - padT - padB;
        double x(int i) => padL + (counts.Length == 1 ? plotW / 2.0 : plotW * i / (double)(counts.Length - 1));

        // The Y mapping and its gridlines, log or linear. A log axis cannot place zero or less, so
        // such values are unplottable and leave a gap (they are still in the raw table).
        var positives = series.SelectMany(s => s.values).Where(v => v is > 0).Select(v => v!.Value).ToList();
        var linear = !metric.Log || positives.Count == 0;
        Func<double, double> y;
        bool canPlot(double v) => linear || v > 0;
        var grid = new List<(double v, bool labeled)>();
        if (linear) {
            var max = series.SelectMany(s => s.values).Where(v => v.HasValue).Max(v => v!.Value);
            var (top, ticks) = niceScale(max <= 0 ? 1 : max);
            y = v => padT + plotH - plotH * v / top;
            for (var t = 0; t <= ticks; t++) grid.Add((top * t / ticks, true));
        } else {
            var loPow = Math.Floor(Math.Log10(positives.Min()));
            var hiPow = Math.Ceiling(Math.Log10(positives.Max()));
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
        svg.Append($"<figure class=\"chart\" id=\"{metric.Id}\">\n");
        svg.Append($"<h3>{esc(metric.Title)}</h3>\n");
        svg.Append($"<p class=\"cap\">{esc(metric.Unit)} &middot; {(metric.LowerIsBetter ? "&#9660; lower is better" : "&#9650; higher is better")}"
            + $"{(linear ? "" : " &middot; log scale")}</p>\n");
        svg.Append($"<svg viewBox=\"0 0 {chartW} {chartH}\" role=\"img\" aria-label=\"{esc(metric.Title)}\">\n");

        foreach (var (v, labeled) in grid) {
            var ty = y(v);
            svg.Append($"<line x1=\"{padL}\" y1=\"{f(ty)}\" x2=\"{chartW - padR}\" y2=\"{f(ty)}\" style=\"stroke: var({(labeled ? "--grid" : "--grid-faint")})\"/>");
            if (labeled) svg.Append($"<text class=\"axis\" x=\"{padL - 7}\" y=\"{f(ty + 4)}\" text-anchor=\"end\">{fmt(v)}</text>\n");
        }
        for (var i = 0; i < counts.Length; i++)
            svg.Append($"<text class=\"axis\" x=\"{f(x(i))}\" y=\"{chartH - padB + 17}\" text-anchor=\"middle\">{fmt(counts[i])}</text>");
        svg.Append($"<text class=\"axis\" x=\"{padL + plotW / 2}\" y=\"{chartH - 8}\" text-anchor=\"middle\">vectors</text>\n");

        // One polyline per (engine, budget), broken at missing points. Every point is a small dot
        // wrapped in a larger invisible hover target carrying the exact value as a tooltip, so the
        // value is reachable without aiming at a three-pixel circle.
        foreach (var (engine, cacheIdx, values) in series) {
            var stroke = $"var(--eng-{slots[engine]})";
            var dashAttr = dash(dashes[cacheIdx]);
            var segment = new List<string>();
            void flush() {
                if (segment.Count > 1)
                    svg.Append($"<polyline points=\"{string.Join(' ', segment)}\" fill=\"none\" style=\"stroke: {stroke}\" stroke-width=\"2\"{dashAttr}/>\n");
                segment.Clear();
            }
            for (var i = 0; i < values.Length; i++) {
                if (values[i] is not double v || !canPlot(v)) { flush(); continue; }
                segment.Add($"{f(x(i))},{f(y(v))}");
            }
            flush();
            for (var i = 0; i < values.Length; i++) {
                if (values[i] is not double v || !canPlot(v)) continue;
                svg.Append($"<circle cx=\"{f(x(i))}\" cy=\"{f(y(v))}\" r=\"2.5\" style=\"fill: {stroke}\"/>"
                    + $"<circle cx=\"{f(x(i))}\" cy=\"{f(y(v))}\" r=\"9\" fill=\"transparent\">"
                    + $"<title>{esc(Engines.ShortName(engine))}, {matrix.CacheMB[cacheIdx]} MB cache @ {counts[i]:N0} vectors: {fmt(v)} {esc(metric.Unit)}</title></circle>");
            }
            svg.Append('\n');
        }
        svg.Append("</svg>\n</figure>\n");
        return svg.ToString();
    }

    // ---- The raw numbers behind the charts -------------------------------------------------------
    static string rawTable(IReadOnlyList<Row> rows, MatrixOptions matrix, List<Metric> charted) {
        var html = new StringBuilder();
        html.Append("<div class=\"scroll\"><table>\n<thead><tr><th>Engine</th><th class=\"num\">Vectors</th><th class=\"num\">Cache MB</th>");
        foreach (var m in charted) html.Append($"<th class=\"num\">{esc(m.Title)}<br><span style=\"font-weight:400;color:var(--fg-faint)\">{esc(m.Unit)}</span></th>");
        html.Append("</tr></thead>\n<tbody>\n");
        foreach (var n in matrix.VectorCounts) {
            foreach (var cache in matrix.CacheMB) {
                foreach (var engine in matrix.EngineNames) {
                    var row = rows.FirstOrDefault(r => r.Engine == engine && r.N == n && r.CacheMB == cache);
                    if (row is null) continue;
                    html.Append($"<tr><th>{esc(Engines.ShortName(engine))}</th><td class=\"num\">{n:N0}</td><td class=\"num\">{cache}</td>");
                    foreach (var m in charted) {
                        var v = row.Result.Error is not null && row.Result.Phases.Count == 0 ? null : m.Value(row.Result);
                        html.Append($"<td class=\"num\">{(plottable(v) ? fmt(v!.Value) : "")}</td>");
                    }
                    html.Append("</tr>\n");
                }
            }
        }
        html.Append("</tbody>\n</table></div>\n");
        return html.ToString();
    }

    // ---- The page's own behaviour: theme, nav highlighting, chrome --------------------------------
    static void script(StringBuilder html) {
        html.Append("""

            <script>
            (function () {
              "use strict";
              var THEME_KEY = "relatude-bench-theme-v1";
              var $theme = document.getElementById("theme");
              var $sidebar = document.getElementById("sidebar");
              var $progress = document.getElementById("progress");
              var $totop = document.getElementById("totop");
              var links = Array.prototype.slice.call(document.querySelectorAll("#nav a"));

              function lsGet(k) { try { return window.localStorage.getItem(k); } catch (e) { return null; } }
              function lsSet(k, v) { try { window.localStorage.setItem(k, v); } catch (e) {} }

              // Three states, cycled by the button: auto (follow the OS) -> light -> dark.
              var THEMES = {
                auto:  { icon: "◐", label: "Theme: auto (follows your system) — click for light" },
                light: { icon: "☀", label: "Theme: light — click for dark" },
                dark:  { icon: "☾", label: "Theme: dark — click to follow your system" }
              };
              var ORDER = ["auto", "light", "dark"];
              var theme = lsGet(THEME_KEY);
              if (theme !== "light" && theme !== "dark") theme = "auto";
              function applyTheme(t) {
                theme = t;
                if (t === "auto") document.documentElement.removeAttribute("data-theme");
                else document.documentElement.setAttribute("data-theme", t);
                $theme.textContent = THEMES[t].icon;
                $theme.title = THEMES[t].label;
                $theme.setAttribute("aria-label", THEMES[t].label);
              }
              applyTheme(theme);
              $theme.addEventListener("click", function () {
                var next = ORDER[(ORDER.indexOf(theme) + 1) % ORDER.length];
                applyTheme(next);
                if (next === "auto") { try { window.localStorage.removeItem(THEME_KEY); } catch (e) {} }
                else lsSet(THEME_KEY, next);
              });

              // The nav follows the reader: the last heading or chart above the top third wins.
              var targets = links.map(function (a) {
                return { link: a, el: document.getElementById(a.getAttribute("href").slice(1)) };
              }).filter(function (t) { return t.el; });

              function spy() {
                var line = window.scrollY + window.innerHeight / 3;
                var current = null;
                for (var i = 0; i < targets.length; i++) {
                  if (targets[i].el.getBoundingClientRect().top + window.scrollY <= line) current = targets[i].link;
                }
                for (var j = 0; j < links.length; j++) links[j].classList.toggle("active", links[j] === current);
                var max = document.body.scrollHeight - window.innerHeight;
                $progress.style.width = (max > 0 ? (window.scrollY / max) * 100 : 0) + "%";
                $totop.classList.toggle("show", window.scrollY > 600);
              }
              var ticking = false;
              window.addEventListener("scroll", function () {
                if (ticking) return;
                ticking = true;
                window.requestAnimationFrame(function () { spy(); ticking = false; });
              }, { passive: true });
              spy();

              document.getElementById("toggle").addEventListener("click", function () { $sidebar.classList.toggle("open"); });
              $totop.addEventListener("click", function () { window.scrollTo({ top: 0, behavior: "smooth" }); });
              for (var k = 0; k < links.length; k++) {
                links[k].addEventListener("click", function () { $sidebar.classList.remove("open"); });
              }
            })();
            </script>
            </body>
            </html>

            """);
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
    static string slug(string s) => s.ToLowerInvariant().Replace(' ', '-');
    static string esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    static string firstLine(string s) => s.Split('\n')[0].Trim();
}
