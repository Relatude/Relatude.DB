using System.Diagnostics;
using System.Text.Json;
using VectorIndexBenchmarks.Harness;

// VectorIndexBenchmarks — benchmarks four vector indexes against each other on one set of
// generated vectors and one query stream:
//
//   memory     Relatude MemorySemanticIndex — every vector on the managed heap, exact SIMD scan
//   native     Relatude NativeVectorIndex   — disk segments, IVF clusters, byte-budgeted block cache
//   sqlitevec  sqlite-vec                   — a vec0 virtual table in a SQLite file, exact KNN
//   usearch    USearch                      — an HNSW graph in native memory, top-k only
//
// The two Relatude ones are driven through ISemanticIndex, the interface the data store uses; the
// third-party ones take vectors directly. Both forms of every query are the same query.
//
//   dotnet run -c Release [-- options]
//
// Options:
//   --n=<count>                  vectors
//   --dims=<n>                   vector length (384 = a small sentence model, 1536 = OpenAI small)
//   --clusters=200               cluster centers the vectors are drawn around; 0 = uniformly
//                                random directions, the worst case for a clustering index
//   --noise=1.0                  how loosely vectors scatter around their center
//   --batch=5000                 vectors between state saves during the load
//   --cache=<MB>                 what the disk index may spend on cached vector blocks
//   --accuracy=0.25              fraction of clusters the disk index probes per search
//   --hnsw-m=16                  USearch graph degree (connectivity)
//   --hnsw-ef-add=128            USearch build effort (expansionAdd)
//   --hnsw-ef=64                 USearch search effort (expansionSearch) — its accuracy dial
//   --min-sim=<f>                the similarity floor the searches pass down (default: the
//                                similarity of the 500th exact neighbour, ~500 candidates)
//   --engines=all|<list>         all = memory,native,sqlitevec,usearch; also native-exact and
//                                native-lowmem. sqlite-vec scans every vector on every query, so
//                                it dominates the runtime — drop it for quick iterations.
//   --persist=batch              save state after every batch instead of once after the load
//   --data=<dir>                 working directory for index files (default: %TEMP%)
//   --in-process                 run everything in this process (memory numbers get noisy)

var options = BenchOptions.Parse(args);

if (options.ChildEngine is not null) {
    // Child mode: one engine, one process, so its memory numbers are its own. Progress goes to the
    // parent as marked stderr lines, which it labels and renders.
    Progress.SendToParent();
    var corpus = Corpus.Build(options);
    BenchResult res;
    try {
        res = BenchRunner.Run(options.ChildEngine, corpus, options, options.ChildDir!);
    } catch (Exception ex) {
        res = new BenchResult { Engine = options.ChildEngine, N = options.N, Error = ex.ToString() };
    }
    Console.WriteLine("##RESULT## " + JsonSerializer.Serialize(res));
    return 0;
}

Console.WriteLine("VectorIndexBenchmarks — Relatude vs sqlite-vec vs USearch");
Console.WriteLine($"{options.N:N0} vectors x {options.Dimensions} dimensions "
    + $"({options.N * (long)options.Dimensions * 4 / (1024.0 * 1024.0):N0} MB of raw float32) | "
    + $"{(options.Clusters > 0 ? $"{options.Clusters:N0} clusters, noise {options.ClusterNoise:0.##}" : "uniformly random directions")} | "
    + $"engines: {string.Join(", ", options.EngineNames)}");
Console.WriteLine();

var root = Path.Combine(options.DataDir, $"vecidxbench_{Environment.ProcessId}");
Directory.CreateDirectory(root);

try {
    var sw = Stopwatch.StartNew();
    var corpus = Corpus.Build(options);
    ProgressDisplay.Clear();
    Console.WriteLine($"Built the vectors and their exact answers in {sw.Elapsed.TotalSeconds:N1} s.");
    Console.WriteLine($"Searches use a minimum similarity of {corpus.MinSimilarity:0.000} "
        + $"(the {Corpus.FilterRank}th exact neighbour), recall is measured over "
        + $"{corpus.ExactNeighbours.Length} of the {Corpus.QueryCount} queries.");
    Console.WriteLine();

    var results = new List<BenchResult>();
    foreach (var engine in options.EngineNames) {
        var label = Engines.DisplayName(engine);
        var dir = Path.Combine(root, engine);
        var engineClock = Stopwatch.StartNew();
        BenchResult res;
        if (options.InProcess) {
            Progress.SendTo(text => ProgressDisplay.Show($"[{label}] {text}"));
            res = BenchRunner.Run(engine, corpus, options, dir);
            Progress.SendToConsole();
        } else {
            res = runChild(engine, options, dir, label);
        }
        ProgressDisplay.Clear();
        Console.WriteLine($"{label}: done in {engineClock.Elapsed.TotalSeconds:N1} s.");
        checkExactness(res, options);
        results.Add(res);
        tryDelete(dir);
    }

    Console.WriteLine();
    printTable($"searches — queries/sec over {Corpus.QueryCount} queries, and accuracy against a brute-force scan", Columns.Search, results);
    Console.WriteLine();
    printTable($"writes and footprint — state saves every {options.BatchSize:N0} vectors, "
        + $"{BenchRunner.WritePhaseOps:N0} updates and removes", Columns.Write, results);
    Console.WriteLine();
    printNotes(options, corpus);
    return results.Any(r => r.Error != null) ? 1 : 0;
} finally {
    tryDelete(root);
}

/// <summary>An implementation that answers exactly must reproduce the brute-force answer. Anything
/// less is a defect rather than a speed/accuracy trade-off, so it is reported as an error.</summary>
static void checkExactness(BenchResult res, BenchOptions options) {
    if (!Engines.IsExact(res.Engine, options)) return;
    var recall = res.Quality0To1("Recall");
    var filter = res.Quality0To1("FilterRecall");
    if (recall is < 0.999) res.Error ??= $"sanity: {Engines.DisplayName(res.Engine)} searches exactly but its recall is {recall:P1}";
    else if (filter is < 0.999) res.Error ??= $"sanity: {Engines.DisplayName(res.Engine)} searches exactly but its filter recall is {filter:P1}";
}

static BenchResult runChild(string engine, BenchOptions options, string dir, string label) {
    var psi = new ProcessStartInfo(Environment.ProcessPath!) {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    psi.ArgumentList.Add($"--child-engine={engine}");
    psi.ArgumentList.Add($"--child-dir={dir}");
    psi.ArgumentList.Add($"--n={options.N}");
    psi.ArgumentList.Add($"--dims={options.Dimensions}");
    psi.ArgumentList.Add($"--clusters={options.Clusters}");
    psi.ArgumentList.Add($"--noise={options.ClusterNoise}");
    psi.ArgumentList.Add($"--batch={options.BatchSize}");
    psi.ArgumentList.Add($"--cache={options.CacheBytes / 1024 / 1024}");
    psi.ArgumentList.Add($"--accuracy={options.Accuracy}");
    psi.ArgumentList.Add($"--hnsw-m={options.HnswConnectivity}");
    psi.ArgumentList.Add($"--hnsw-ef-add={options.HnswExpansionAdd}");
    psi.ArgumentList.Add($"--hnsw-ef={options.HnswExpansionSearch}");
    if (options.MinSimilarity.HasValue) psi.ArgumentList.Add($"--min-sim={options.MinSimilarity.Value}");
    if (options.PersistEveryBatch) psi.ArgumentList.Add("--persist=batch");
    using var proc = Process.Start(psi)!;
    // The child's progress lines, labelled with the engine and rendered over each other; anything
    // else it writes to stderr is a real message and gets a line of its own.
    proc.ErrorDataReceived += (_, e) => {
        if (e.Data is null) return;
        if (Progress.TryUnwrap(e.Data, out var text)) ProgressDisplay.Show($"[{label}] {text}");
        else if (e.Data.Trim().Length > 0) {
            ProgressDisplay.Clear();
            Console.Error.WriteLine(e.Data);
        }
    };
    proc.BeginErrorReadLine();
    var stdout = proc.StandardOutput.ReadToEnd();
    proc.WaitForExit();
    var line = stdout.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("##RESULT## "));
    return line is null
        ? new BenchResult { Engine = engine, N = options.N, Error = $"child produced no result (exit {proc.ExitCode}): {stdout}" }
        : JsonSerializer.Deserialize<BenchResult>(line["##RESULT## ".Length..])!;
}

static void printTable(string title, TableColumn[] columns, List<BenchResult> rows) {
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
        Console.WriteLine($"  ! {Engines.DisplayName(r.Engine)}: {firstLine(r.Error!)}");
}

static string firstLine(string s) => s.Split('\n')[0].Trim();

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

static void printNotes(BenchOptions options, Corpus corpus) {
    foreach (var e in options.EngineNames)
        Console.WriteLine($"  {Engines.DisplayName(e),-28} {Engines.Description(e)}");
    Console.WriteLine($$"""

        Notes
          - In every column the best value is green and the runner-up cyan, the rest dimmed; the
            millisecond and footprint columns rank low-to-high, the rest high-to-low. "n/a" is a
            capability the implementation does not have — not a slow result. Redirected output
            (and NO_COLOR) stays plain text.
          - Speed without accuracy means nothing here. MemorySemanticIndex and sqlite-vec are exact by
            construction — they look at every vector. NativeVectorIndex probes the {{options.Accuracy:P0}} of clusters
            nearest the query, and USearch walks an HNSW graph with an expansion of {{options.HnswExpansionSearch}}; both may
            miss a neighbour they never visited. Read the search rates and the recall columns
            together, and use --accuracy / --hnsw-ef to compare the two approximate ones at matched
            recall rather than at matched settings, which mean different things.
          - Recall% is the share of the exact first page of {{Corpus.RecallTopK}} a ranked search returns; Filter%
            the share of the exact above-threshold set an unranked filter search returns. Both are
            measured against a brute-force scan of the freshly loaded vectors, over {{Corpus.RecallQueryCount}} queries, and
            both are 100% for an implementation that searches exactly (anything less is reported as
            an error rather than a trade-off).
          - The Relatude implementations are driven only through ISemanticIndex, the interface the data
            store uses, so what is measured for them is the production path. A semantic search arrives
            there as text the index embeds itself; the benchmark supplies an AI engine that maps each
            query text back to its generated vector and caches it, so what is timed is the index and
            not an embedding call. The third-party libraries take the same vector directly.
          - Not every implementation has every operation, and "n/a" means absent rather than slow.
            USearch answers "the best k" and has no unranked threshold query, so its Filter columns
            are blank — emulating one with a k of the whole index would measure a query nobody would
            write. Only NativeVectorIndex has a post-WAL-flush durability hook.
          - The third-party libraries keep their data in native memory (USearch's graph) or in a page
            cache (sqlite-vec), so almost nothing of theirs appears in the managed Mem/Warm columns —
            for them, only WSet MB and Disk MB carry information. That asymmetry is a property of the
            measurement, not a result: do not read their low Mem MB as frugality.
          - sqlite-vec has no approximate index. Every ranked query scans and ranks every stored
            vector, and a similarity floor buys it nothing (a vec0 KNN query takes a k and nothing
            else, so the floor is applied to the rows that come back). It is the exact-answer
            reference next to native-exact, and it is why a full run takes as long as it does.
          - The vectors are synthetic: unit vectors drawn around {{(options.Clusters > 0 ? options.Clusters + " random cluster centers" : "no centers at all")}}, and the
            queries are drawn from the same distribution, so a query has real near neighbours the way
            a real one does. Clustered data is what embeddings look like and what an IVF index is
            built for; --clusters=0 removes the structure entirely, which is its worst case.
          - Top10 is a first page of {{BenchRunner.PageSize}} out of {{BenchRunner.MaxHitsEvaluated}} evaluated hits and Top100 a page of {{BenchRunner.DeepPageSize}}
            out of {{BenchRunner.DeepMaxHitsEvaluated:N0}}, both at a minimum similarity of {{corpus.MinSimilarity:0.000}} — the {{Corpus.FilterRank}}th exact
            neighbour, so about that many vectors clear the floor, which is what a semantic query
            with a minimum-similarity setting looks like. NoFloor is the same page of {{BenchRunner.PageSize}} with no
            floor at all: every vector in the index is then a candidate and none can be discarded
            before it reaches the implementation's top-k structure, which is a different bottleneck
            and worth reading next to Top10. Filter is the unranked id-set path a semantic
            WhereSearch filter uses, at the same floor, measured with the store's set cache disabled
            so the index answers every call.
          - Index/s is vectors per second including the state save at the end of the
            load{{(options.PersistEveryBatch ? " (--persist=batch: after every batch instead)" : "")}}. Save ms is one state save after a small delta: the in-memory index
            writes a file containing every vector it holds, the disk index writes the delta and swaps
            a manifest, so this gap widens with the corpus. Flush ms is the post-WAL-flush hook the
            disk index has and the in-memory one does not — the store makes an in-memory index
            durable only at the periodic state save and replays the WAL for anything newer.
          - Update replaces the vector of an existing id; Remove drops ids at random. Mixed
            interleaves inserts, searches and deletes, so searches run against an index that is
            churning rather than holding still. All three run a fixed {{BenchRunner.WritePhaseOps:N0}} / {{BenchRunner.WritePhaseOps:N0}} / {{BenchRunner.MixedPhaseWrites:N0}} operations
            rather than a share of the corpus, so they measure cost per operation and their numbers
            do not depend on --n.
          - Open ms reopens the persisted index; Cold ms is the first search after that, un-warmed.
            The in-memory index reads every vector back into the heap to open; the disk index reads
            its manifest, centroids and one block directory per segment, then pays for the blocks it
            probes on the first searches.
          - Mem MB is managed heap growth right after the load (full GC), before the index has served
            anything: for the in-memory index that is the vector data itself, for the disk index the
            ids, offsets and centroids. Warm MB is the same measurement after the search phases, which
            is the only place a read-through cache appears — the disk index has by then pulled the
            blocks its searches touched into its {{options.CacheBytes / 1024 / 1024}} MB budget, and that memory is what its search
            rates were bought with (run --engines=native,native-lowmem to see the trade directly).
            WSet MB is working-set growth at the same point, which also covers native memory and file
            reads. Disk MB is the index on disk after the load was made durable. The generated vectors
            are {{options.N * (long)options.Dimensions * 4 / (1024.0 * 1024.0):N0}} MB of raw float32, held by the harness and outside all of these numbers:
            every vector is handed to an index as a fresh copy, so what an index keeps is its own.
          - Each engine runs in its own child process, so its memory numbers are not polluted by the
            other's. --in-process runs them together, which is faster and noisier.
        """);
}

static void tryDelete(string dir) {
    try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* still held by a child or an AV scanner; the temp root is cleaned next run */ }
}

/// <summary>
/// One column of a result table: its header, the number behind each cell, how that number reads,
/// and which end of it wins — throughput and accuracy columns want the largest value, latency and
/// footprint columns the smallest. The first entry is the engine label, which carries no number.
/// </summary>
sealed record TableColumn(string Header, Func<BenchResult, double?> Value, Func<double, string> Text, bool LowerIsBetter = false, string? Phase = null) {
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

static class Columns {
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

/// <summary>
/// Result-table colouring: the best value in a column green, the runner-up cyan, the rest dimmed
/// so the two that matter stand out. Set through <see cref="Console.ForegroundColor"/> rather than
/// escape codes, so it works the same in a Windows console and a POSIX terminal, and is skipped
/// for redirected output (and when NO_COLOR is set) so piped tables stay plain text.
/// </summary>
static class TableColor {
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
