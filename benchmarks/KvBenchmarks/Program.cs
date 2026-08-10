using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KvBenchmarks;
using KvBenchmarks.Harness;

// KvBenchmarks — benchmarks the internal NativeKvStore (BPlusTreeStorageEngine) against
// implementations built on SQLite, ZoneTree and Microsoft FASTER. Every engine appears twice:
// once in its ordered layout (ISortedIndex) and once in its unordered, lookup-only one, which is
// the same store minus whatever it keeps to answer ordered queries.
//
//   dotnet run -c Release [-- options]
//
// Options:
//   --n=xxxxx                          entries per scenario
//   --engines=all|sorted|hash|<list>   e.g. native,native-hash,sqlite-hash
//   --scenarios=int,long,string,guid,datetime
//   --data=<dir>                        working directory for store files (default: %TEMP%)
//   --no-verify                         skip the correctness verification pass
//   --in-process                        run everything in this process (memory numbers get noisy)
//   --scratch                           run Test.Run() instead (ad-hoc experiments)

if (args.Contains("--scratch")) {
    Test.Run();
    return 0;
}

var options = Options.Parse(args);

if (args.Length == 0) {
    options.N = 2000_000;
    options.SkipVerify = true;
    options.InProcess = true;
}

if (options.ChildEngine is not null) {
    // Child mode: run a single (engine, scenario) benchmark and emit the result as JSON.
    var scenario = Scenarios.Get(options.ChildScenario!);
    BenchResult res;
    try {
        res = scenario.Bench(options.ChildEngine, options.N, options.ChildDir!);
    } catch (Exception ex) {
        res = new BenchResult { Engine = options.ChildEngine, Scenario = options.ChildScenario!, N = options.N, Error = ex.ToString() };
    }
    Console.Error.WriteLine();
    Console.WriteLine("##RESULT## " + JsonSerializer.Serialize(res));
    return 0;
}

Console.WriteLine($"KvBenchmarks — NativeKvStore vs SQLite vs ZoneTree vs FASTER, each in its ordered and unordered layout");
Console.WriteLine($"n={options.N:N0} per scenario | engines: {string.Join(", ", options.Engines)} | scenarios: {string.Join(", ", options.Scenarios)}");
Console.WriteLine();

string root = Path.Combine(options.DataDir, $"kvbench_{Environment.ProcessId}");
Directory.CreateDirectory(root);

try {
    // ---- 1. Correctness verification (candidates replayed against the native engine) ----
    if (!options.SkipVerify) {
        Console.WriteLine("Verifying engines against the native reference…");
        bool allOk = true;
        foreach (string engine in options.Engines.Where(e => e != "native")) {
            foreach (string scenarioName in options.Scenarios) {
                var scenario = Scenarios.Get(scenarioName);
                string dir = Path.Combine(root, "verify", engine, scenarioName);
                string? err = scenario.Verify(engine, dir);
                Console.WriteLine($"  {engine,-14} {scenarioName,-9} {(err is null ? "OK" : "MISMATCH")}");
                if (err is not null) {
                    Console.WriteLine($"    {err}");
                    allOk = false;
                }
            }
        }
        if (!allOk) {
            Console.WriteLine("Verification failed — benchmark aborted; numbers from a wrong index are meaningless.");
            return 1;
        }
        Console.WriteLine();
    }

    // ---- 2. Benchmarks ----
    var results = new List<BenchResult>();
    foreach (string scenarioName in options.Scenarios) {
        foreach (string engine in options.Engines) {
            Console.Error.Write($"[{scenarioName}] {engine}:");
            string dir = Path.Combine(root, "bench", scenarioName, engine);
            BenchResult res = options.InProcess
                ? Scenarios.Get(scenarioName).Bench(engine, options.N, dir)
                : RunChild(engine, scenarioName, options, dir);
            Console.Error.WriteLine(" done");
            results.Add(res);
            TryDelete(dir); // keep peak disk usage of the run bounded
        }
    }

    // ---- 3. Report ----
    Console.WriteLine();
    Console.WriteLine($"Results — {options.N:N0} entries per scenario, batched transactions of {BenchRunner.BatchSize:N0} ops");
    foreach (string scenarioName in options.Scenarios) {
        Console.WriteLine();
        PrintTable(scenarioName, results.Where(r => r.Scenario == scenarioName).ToList());
    }
    Console.WriteLine();
    PrintNotes();
    return results.Any(r => r.Error != null) ? 1 : 0;
} finally {
    TryDelete(root);
}

static BenchResult RunChild(string engine, string scenarioName, Options options, string dir) {
    string exe = Environment.ProcessPath!;
    var psi = new ProcessStartInfo(exe) {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    psi.ArgumentList.Add($"--child-engine={engine}");
    psi.ArgumentList.Add($"--child-scenario={scenarioName}");
    psi.ArgumentList.Add($"--child-dir={dir}");
    psi.ArgumentList.Add($"--n={options.N}");
    using var proc = Process.Start(psi)!;
    proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.Write(e.Data); };
    proc.BeginErrorReadLine();
    string stdout = proc.StandardOutput.ReadToEnd();
    proc.WaitForExit();

    string? line = stdout.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("##RESULT## "));
    if (line is null)
        return new BenchResult { Engine = engine, Scenario = scenarioName, N = options.N, Error = $"child produced no result (exit {proc.ExitCode}): {stdout}" };
    return JsonSerializer.Deserialize<BenchResult>(line["##RESULT## ".Length..])!;
}

static void PrintTable(string scenarioName, List<BenchResult> rows) {
    var columns = TableColumn.All;
    var table = new List<string[]> { columns.Select(c => c.Header).ToArray() };
    var values = new List<double?[]>(); // the numbers behind the cells, for ranking

    foreach (var r in rows) {
        bool failed = r.Error is not null && r.Phases.Count == 0;
        var cells = new string[columns.Length];
        var vals = new double?[columns.Length];
        cells[0] = Engines.DisplayName(r.Engine);
        for (int c = 1; c < columns.Length; c++) {
            vals[c] = failed ? null : columns[c].Value(r);
            cells[c] = failed ? (c == 1 ? "FAILED" : "") : columns[c].Format(vals[c]);
        }
        table.Add(cells);
        values.Add(vals);
    }

    int[] widths = new int[columns.Length];
    foreach (var row in table)
        for (int c = 0; c < row.Length; c++)
            widths[c] = Math.Max(widths[c], row[c].Length);
    int[][] ranks = RankColumns(columns, values);

    Console.WriteLine($"— {scenarioName} —");
    for (int i = 0; i < table.Count; i++) {
        Console.Write("  ");
        for (int c = 0; c < table[i].Length; c++) {
            string cell = c == 0 ? table[i][c].PadRight(widths[c]) : table[i][c].PadLeft(widths[c]);
            TableColor.Write(cell, i == 0 ? TableColor.Plain : ranks[i - 1][c]);
            if (c < table[i].Length - 1) Console.Write("  ");
        }
        Console.WriteLine();
        if (i == 0) Console.WriteLine("  " + string.Join("  ", widths.Select(w => new string('-', w))));
    }
    foreach (var r in rows.Where(r => r.Error is not null))
        Console.WriteLine($"  ! {Engines.DisplayName(r.Engine)}: {r.Error}");
}

/// <summary>
/// Per column: the best value, the runner-up, and everything else — so a reader can find the
/// winner of a column without comparing every cell. Equal values share a place, absent ones take
/// none, and a table with a single row is not ranked at all (nothing to win against).
/// </summary>
static int[][] RankColumns(TableColumn[] columns, List<double?[]> values) {
    var ranks = new int[values.Count][];
    for (int i = 0; i < values.Count; i++) {
        ranks[i] = new int[columns.Length];
        Array.Fill(ranks[i], TableColor.Rest);
        ranks[i][0] = TableColor.Plain; // the engine name is a label, not a measurement
    }
    if (values.Count < 2) {
        foreach (var row in ranks) Array.Fill(row, TableColor.Plain);
        return ranks;
    }

    for (int c = 1; c < columns.Length; c++) {
        var distinct = values.Select(v => v[c]).Where(v => v.HasValue).Select(v => v!.Value).Distinct().ToList();
        if (distinct.Count == 0) continue;
        distinct.Sort();
        if (!columns[c].LowerIsBetter) distinct.Reverse();
        double best = distinct[0];
        double? second = distinct.Count > 1 ? distinct[1] : null;
        for (int i = 0; i < values.Count; i++) {
            double? v = values[i][c];
            if (v is null) continue;
            if (v.Value == best) ranks[i][c] = TableColor.Best;
            else if (second is not null && v.Value == second.Value) ranks[i][c] = TableColor.Second;
        }
    }
    return ranks;
}

static void PrintNotes() {
    Console.WriteLine("""
        Notes
          - In every column the best value is light green and the runner-up light blue, the rest
            dimmed; the three footprint columns rank low-to-high, the rest high-to-low. Piped or
            redirected output (and NO_COLOR) stays plain text.
          - Every engine was verified against the native engine on identical op streams before
            timing. The (hash) rows implement the unordered subset of the contract and were verified
            on the same stream, comparing their enumerations as sets — they have no order to compare.
          - A (hash) row is the same engine and the same store as the row above it, opened through
            OpenOrCreateIntHashIndex: the layout that keeps only what an id lookup needs. Ordered
            columns are blank because there is no ordering, and GetIds is blank because without a
            value index it degrades to an O(n) scan that no other column would be comparable to.
            NativeKv: one extendible-hash table instead of an id tree plus a value tree.
            SQLite: the table without the covering (v, id) index. ZoneTree: the id tree without the
            composite one. FASTER: the store alone, without the in-memory sorted set beside it.
          - Insert/Update/Remove run in transactions of 20k ops; Insert ends with one durable commit.
          - Read/s: point lookups by id (10% misses). Range rows/s: rows yielded by GetIdsInRange
            over windows of ~1k rows. DurTx/s: small durable (fsync) transactions of 10 ops.
          - Mixed/s: ops/sec over an interleaved insert / read / delete stream — every insert of a
            previously unseen id is followed by 3 reads, and past a lag every other insert also
            deletes an id written earlier in the phase, in transactions of 5k inserts. It is the one
            phase where lookups run against a store that is churning rather than holding still. A
            quarter of its reads target ids it inserted itself.
          - Mem MB: managed heap growth after load (full GC). WSet MB: working-set growth (includes
            native memory: SQLite page cache, FASTER log, ZoneTree segments). Disk MB: store size
            after the loaded state was durably committed.
          - NativeKv: 64 MB page cache, copy-on-write B+Tree, driven like production drives it:
            non-durable commit = publish (readers see it, pages buffered in memory, crash rolls
            back to the last durable point — the same trade FASTER and ZoneTree make), durable
            commit = deduplicated page write-out + flush + fsync'd meta.
          - SQLite: WAL mode, synchronous=FULL, 64 MB cache, table (id PRIMARY KEY, v) + index (v, id).
          - ZoneTree: LSM; two trees per index (id→value and (value,id) composite). WAL=AsyncCompressed,
            so batched writes are buffered like the others, but its "durable" commit only saves
            metadata — ZoneTree exposes no group-commit/fsync primitive, so DurTx/s overstates it.
          - FASTER: hash KV store — no ordered scans. Ordered ops are served by an in-memory
            SortedSet of (value,id) keys maintained beside the store (rebuilt on open); its cost
            shows in Mem MB. Durable commit = FoldOver checkpoint.
        """);
}

static void TryDelete(string dir) {
    try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* still held by a child or AV scanner; the temp root is cleaned next run */ }
}

/// <summary>
/// One column of a result table: its header, the number behind each cell, how that number reads,
/// and which end of it wins — throughput columns want the largest value, footprint columns the
/// smallest. The first entry is the engine label, which carries no number.
/// </summary>
sealed record TableColumn(string Header, Func<BenchResult, double?> Value, Func<double, string> Text, bool LowerIsBetter = false) {
    public string Format(double? v) => v is null ? "-" : Text(v.Value);

    private static TableColumn Rate(string header, string phase)
        => new(header, r => r.Phase(phase)?.Rate, RateText);

    private static string RateText(double v)
        => v >= 10_000_000 ? $"{v / 1e6:0.0}M"
            : v >= 1_000_000 ? $"{v / 1e6:0.00}M"
            : v >= 10_000 ? $"{v / 1e3:0}k"
            : v >= 1_000 ? $"{v / 1e3:0.0}k"
            : $"{v:0}";

    private static TableColumn Megabytes(string header, Func<BenchResult, double> value)
        => new(header, r => value(r), v => v.ToString("0.0"), LowerIsBetter: true);

    public static readonly TableColumn[] All = [
        new("Engine", _ => null, _ => ""),
        Rate("Insert/s", "Insert"),
        Rate("Read/s", "PointRead"),
        Rate("GetIds/s", "GetIds"),
        Rate("Range rows/s", "RangeScan"),
        Rate("RangeCnt/s", "RangeCount"),
        Rate("Update/s", "Update"),
        Rate("Mixed/s", "Mixed"),
        Rate("DurTx/s", "DurableTx"),
        Rate("Remove/s", "Remove"),
        Megabytes("Mem MB", r => r.ManagedMB),
        Megabytes("WSet MB", r => r.WorkingSetMB),
        Megabytes("Disk MB", r => r.DiskMB),
    ];
}

/// <summary>
/// Result-table colouring: the best value in a column light green, the runner-up light blue,
/// the rest dimmed so the two that matter stand out. Set through <see cref="Console.ForegroundColor"/>
/// rather than escape codes, so it works the same in a Windows console and a POSIX terminal, and
/// is skipped for redirected output (and when NO_COLOR is set) so piped tables stay plain text.
/// </summary>
static class TableColor {
    public const int Best = 0, Second = 1, Rest = 2, Plain = 3;

    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("NO_COLOR") is null && !Console.IsOutputRedirected;

    public static void Write(string text, int rank) {
        if (!Enabled || rank == Plain) {
            Console.Write(text);
            return;
        }
        ConsoleColor previous = Console.ForegroundColor;
        Console.ForegroundColor = rank switch {
            Best => ConsoleColor.Green,
            Second => ConsoleColor.Cyan,
            _ => ConsoleColor.DarkGray,
        };
        Console.Write(text);
        Console.ForegroundColor = previous;
    }
}

sealed class Options {
    public int N = 500_000;
    public string[] Engines = KvBenchmarks.Harness.Engines.All;
    //public string[] Scenarios = ["int", "long", "string", "guid", "datetime"];
    public string[] Scenarios = ["int"];
    public string DataDir = Path.GetTempPath();
    public bool SkipVerify;
    public bool InProcess;
    public string? ChildEngine, ChildScenario, ChildDir;

    public static Options Parse(string[] args) {
        var o = new Options();
        foreach (string a in args) {
            string[] kv = a.Split('=', 2);
            switch (kv[0]) {
                case "--n": o.N = int.Parse(kv[1]); break;
                case "--engines":
                    o.Engines = kv[1] switch {
                        "all" => KvBenchmarks.Harness.Engines.All,
                        "sorted" => KvBenchmarks.Harness.Engines.Sorted,
                        "hash" => KvBenchmarks.Harness.Engines.Hash,
                        _ => kv[1].Split(','),
                    };
                    break;
                case "--scenarios": o.Scenarios = kv[1] == "all" ? ["int", "long", "string", "guid", "datetime"] : kv[1].Split(','); break;
                case "--data": o.DataDir = kv[1]; break;
                case "--no-verify": o.SkipVerify = true; break;
                case "--in-process": o.InProcess = true; break;
                case "--scratch": break; // handled before parsing
                case "--child-engine": o.ChildEngine = kv[1]; break;
                case "--child-scenario": o.ChildScenario = kv[1]; break;
                case "--child-dir": o.ChildDir = kv[1]; break;
                default: throw new ArgumentException($"Unknown option '{a}'.");
            }
        }
        return o;
    }
}
