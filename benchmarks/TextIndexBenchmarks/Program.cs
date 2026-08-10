using System.Diagnostics;
using System.Text.Json;
using TextIndexBenchmarks.Harness;

// TextIndexBenchmarks — benchmarks the four IWordIndex implementations against each other on one
// corpus and one query stream: the in-memory WordIndexTrie, the built-in disk-based TextIndex,
// Lucene.NET and SQLite FTS5.
//
//   dotnet run -c Release [-- options]
//
// Options:
//   --n=50000                    documents
//   --words=60                   words per document
//   --vocab=40000                distinct words in the vocabulary
//   --batch=2000                 documents per transaction
//   --cache=256                  MB the disk TextIndex may cache
//   --engines=all|<list>         e.g. textindex,lucene — "textindex-lowmem" adds the disk index
//                                on a small cache budget, to see what the budget buys
//   --no-infix                   index without infix (*word) support
//   --persist=batch              checkpoint after every batch instead of once after the load
//   --data=<dir>                 working directory for index files (default: %TEMP%)
//   --no-verify                  skip the correctness pass
//   --strict                     stop when a candidate disagrees with the reference
//   --in-process                 run everything in this process (memory numbers get noisy)

var options = BenchOptions.Parse(args);

if (options.ChildEngine is not null) {
    // Child mode: one engine, one process, so its memory numbers are its own.
    var corpus = Corpus.Build(options.N, options.WordsPerDocument, options.VocabularySize);
    BenchResult res;
    try {
        res = BenchRunner.Run(options.ChildEngine, corpus, options, options.ChildDir!);
    } catch (Exception ex) {
        res = new BenchResult { Engine = options.ChildEngine, N = options.N, Error = ex.ToString() };
    }
    Console.Error.WriteLine();
    Console.WriteLine("##RESULT## " + JsonSerializer.Serialize(res));
    return 0;
}

Console.WriteLine("TextIndexBenchmarks — WordIndexTrie vs TextIndex vs Lucene vs SQLite FTS5");
Console.WriteLine($"{options.N:N0} documents x {options.WordsPerDocument} words from a {options.VocabularySize:N0} word vocabulary "
    + $"({(long)options.N * options.WordsPerDocument:N0} words) | infix: {(options.Infix ? "on" : "off")} | engines: {string.Join(", ", options.EngineNames)}");
Console.WriteLine();

var root = Path.Combine(options.DataDir, $"textidxbench_{Environment.ProcessId}");
Directory.CreateDirectory(root);

try {
    Console.Write("Building the corpus… ");
    var sw = Stopwatch.StartNew();
    var corpus = Corpus.Build(options.N, options.WordsPerDocument, options.VocabularySize);
    Console.WriteLine($"{sw.ElapsedMilliseconds:N0} ms");
    Console.WriteLine();

    // ---- 1. Correctness ----
    if (!options.SkipVerify) {
        Console.WriteLine($"Verifying against the reference ({Engines.DisplayName(Engines.Reference)})…");
        var allOk = true;
        foreach (var engine in options.EngineNames.Where(e => e != Engines.Reference)) {
            var dir = Path.Combine(root, "verify", engine);
            string? err;
            try {
                err = Verifier.Run(engine, corpus, options, dir);
            } catch (Exception ex) {
                err = ex.Message;
            }
            Console.WriteLine($"  {Engines.DisplayName(engine),-20} {(err is null ? "OK" : "DIFFERS")}");
            if (err is not null) {
                Console.WriteLine($"    {err}");
                allOk = false;
            }
            tryDelete(dir);
        }
        if (!allOk && options.Strict) {
            Console.WriteLine("Verification failed — aborted (--strict).");
            return 1;
        }
        if (!allOk) Console.WriteLine("  (differences noted; numbers below are still measured — run with --strict to stop here)");
        Console.WriteLine();
    }

    // ---- 2. Benchmarks ----
    var results = new List<BenchResult>();
    foreach (var engine in options.EngineNames) {
        Console.Error.Write($"[{Engines.DisplayName(engine)}]:");
        var dir = Path.Combine(root, "bench", engine);
        var res = options.InProcess ? BenchRunner.Run(engine, corpus, options, dir) : runChild(engine, options, dir);
        Console.Error.WriteLine(" done");
        results.Add(res);
        tryDelete(dir);
    }

    // ---- 3. Report ----
    Console.WriteLine();
    printTable("searches — queries/sec, first page of 20, top 500 queries per class", Columns.Search, results);
    Console.WriteLine();
    printTable($"writes and footprint — transactions of {options.BatchSize:N0} documents, "
        + $"{BenchRunner.WritePhaseOps:N0} updates and removes", Columns.Write, results);
    Console.WriteLine();
    printNotes(options);
    return results.Any(r => r.Error != null) ? 1 : 0;
} finally {
    tryDelete(root);
}

static BenchResult runChild(string engine, BenchOptions options, string dir) {
    var psi = new ProcessStartInfo(Environment.ProcessPath!) {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    psi.ArgumentList.Add($"--child-engine={engine}");
    psi.ArgumentList.Add($"--child-dir={dir}");
    psi.ArgumentList.Add($"--n={options.N}");
    psi.ArgumentList.Add($"--words={options.WordsPerDocument}");
    psi.ArgumentList.Add($"--vocab={options.VocabularySize}");
    psi.ArgumentList.Add($"--batch={options.BatchSize}");
    psi.ArgumentList.Add($"--cache={options.CacheBytes / 1024 / 1024}");
    if (!options.Infix) psi.ArgumentList.Add("--no-infix");
    if (options.PersistEveryBatch) psi.ArgumentList.Add("--persist=batch");
    using var proc = Process.Start(psi)!;
    proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.Write(e.Data); };
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
                cells[c] = "n/a"; // the engine does not implement it: not a slow result, an absent one
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

static void printNotes(BenchOptions options) {
    foreach (var e in options.EngineNames)
        Console.WriteLine($"  {Engines.DisplayName(e),-20} {Engines.Description(e)}");
    Console.WriteLine($$"""

        Notes
          - In every column the best value is green and the runner-up cyan, the rest dimmed; the
            millisecond and footprint columns rank low-to-high, the rest high-to-low. "n/a" is a
            capability the implementation does not have — not a slow result. Redirected output
            (and NO_COLOR) stays plain text.
          - Every candidate was verified against WordIndexTrie before timing: identical documents,
            updates and deletes, then the same searches, comparing the id sets of the unranked
            search after every round and after a reopen. Ranked order is not compared — each engine
            scores its own way (the disk TextIndex matches the trie's BM25, Lucene and FTS5 do not)
            — and the word-expansion cap is lifted there, because a prefix over a large vocabulary
            matches more words than the cap allows and each engine then evaluates a different
            subset of them (Lucene and FTS5 do not cap at all). That is a difference in where the
            cut falls, not in which documents match, and capping is part of what the timings below
            compare.
          - The corpus is synthetic: words drawn from a skewed distribution, so the most common ones
            land in roughly a tenth of the documents. Query terms are drawn the same way, so the
            query mix is dominated by common (expensive) terms, as a real one is.
          - Searches ask for the first page of {{BenchRunner.PageSize}}, evaluating at most
            {{BenchRunner.MaxHitsEvaluated:N0}} hits and {{BenchRunner.MaxWordsEvaluated}} word variations per term —
            the caps the data store passes down. Each engine honours them its own way (top-N heap,
            SQL LIMIT, an evaluation cut-off), which is part of what is being compared.
          - Term/And/Or/Prefix/Infix/Fuzzy are ranked searches (SearchForRankedHitData). Filter is
            the unranked id-set path a WhereSearch filter uses (SearchForIdSetUnranked), measured
            with the store's set cache disabled so the index answers every call.
          - Index/s is documents per second including one durability checkpoint at the end of the
            load{{(options.PersistEveryBatch ? " (--persist=batch: after every transaction instead)" : "")}}. Persist ms is the commit and checkpoint of one small delta, timed together
            because the implementations split them differently: SQLite makes data durable in the
            commit itself, the other three defer it to the checkpoint. An LSM flush and a Lucene
            commit write the delta; the trie writes its whole state file every time.
          - The trie is the only implementation whose durability is not per transaction: the data
            store saves memory-index state periodically and replays the WAL for anything newer.
            The persisted engines checkpoint after every WAL flush. Run --persist=batch to see what
            that cadence costs each of them.
          - Update = the store's node update: the old text removed, the new one added, in one
            transaction. Mixed interleaves inserts, searches and deletes in short transactions, so
            searches run against an index that is churning rather than holding still.
          - The update, remove and mixed phases run a fixed {{BenchRunner.WritePhaseOps:N0}} / {{BenchRunner.WritePhaseOps:N0}} / {{BenchRunner.MixedPhaseWrites:N0}} operations
            rather than a share of the corpus, so they measure cost per operation and an engine
            whose per-operation cost grows with the corpus cannot decide how long a run takes (nor
            have its own numbers depend on --n).
          - Open ms reopens the persisted index from disk; Cold ms is the first search after that,
            un-warmed. The trie reads its entire state file to open; the disk index reads its
            manifest and one block index per segment.
          - Mem MB: managed heap growth after the load (full GC). WSet MB: working-set growth,
            which is where native memory shows up (SQLite page cache, Lucene buffers, memory-mapped
            segment reads). Disk MB: the index on disk after the load was made durable.
        """);
}

static void tryDelete(string dir) {
    try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* still held by a child or an AV scanner; the temp root is cleaned next run */ }
}

/// <summary>
/// One column of a result table: its header, the number behind each cell, how that number reads,
/// and which end of it wins — throughput columns want the largest value, latency and footprint
/// columns the smallest. The first entry is the engine label, which carries no number.
/// </summary>
sealed record TableColumn(string Header, Func<BenchResult, double?> Value, Func<double, string> Text, bool LowerIsBetter = false, string? Phase = null) {
    public string Format(double? v) => v is null ? "-" : Text(v.Value);
    public bool Unsupported(BenchResult r) => Phase is not null && r.IsUnsupported(Phase);

    public static TableColumn Rate(string header, string phase)
        => new(header, r => r.Rate(phase), rateText, Phase: phase);
    public static TableColumn Ms(string header, string key)
        => new(header, r => r.Ms(key), v => v >= 1000 ? $"{v / 1000:0.00}s" : v >= 10 ? $"{v:0}" : $"{v:0.00}", LowerIsBetter: true);
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
        TableColumn.Rate("Term", "Term"),
        TableColumn.Rate("And", "And"),
        TableColumn.Rate("Or", "Or"),
        TableColumn.Rate("Prefix", "Prefix"),
        TableColumn.Rate("Infix", "Infix"),
        TableColumn.Rate("Fuzzy", "Fuzzy"),
        TableColumn.Rate("Filter", "Filter"),
        TableColumn.Rate("Suggest", "Suggest"),
    ];

    public static readonly TableColumn[] Write = [
        label,
        TableColumn.Rate("Index/s", "Index"),
        TableColumn.Ms("Persist ms", "Persist"),
        TableColumn.Rate("Update/s", "Update"),
        TableColumn.Rate("Remove/s", "Remove"),
        TableColumn.Rate("Mixed/s", "Mixed"),
        TableColumn.Ms("Open ms", "Open"),
        TableColumn.Ms("Cold ms", "Cold"),
        TableColumn.Megabytes("Mem MB", r => r.ManagedMB),
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
