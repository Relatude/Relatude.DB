using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KvBenchmarks.Harness;

// KvBenchmarks — benchmarks the internal NativeKvStore (BPlusTreeStorageEngine) against
// ISortedIndex implementations built on SQLite, ZoneTree and Microsoft FASTER.
//
//   dotnet run -c Release [-- options]
//
// Options:
//   --n=100000                          entries per scenario
//   --engines=native,sqlite,zonetree,faster
//   --scenarios=int,long,string,guid,datetime
//   --data=<dir>                        working directory for store files (default: %TEMP%)
//   --no-verify                         skip the correctness verification pass
//   --in-process                        run everything in this process (memory numbers get noisy)

var options = Options.Parse(args);

if (options.ChildEngine is not null)
{
    // Child mode: run a single (engine, scenario) benchmark and emit the result as JSON.
    var scenario = Scenarios.Get(options.ChildScenario!);
    BenchResult res;
    try
    {
        res = scenario.Bench(options.ChildEngine, options.N, options.ChildDir!);
    }
    catch (Exception ex)
    {
        res = new BenchResult { Engine = options.ChildEngine, Scenario = options.ChildScenario!, N = options.N, Error = ex.ToString() };
    }
    Console.Error.WriteLine();
    Console.WriteLine("##RESULT## " + JsonSerializer.Serialize(res));
    return 0;
}

Console.WriteLine($"KvBenchmarks — NativeKvStore vs SQLite vs ZoneTree vs FASTER");
Console.WriteLine($"n={options.N:N0} per scenario | engines: {string.Join(", ", options.Engines)} | scenarios: {string.Join(", ", options.Scenarios)}");
Console.WriteLine();

string root = Path.Combine(options.DataDir, $"kvbench_{Environment.ProcessId}");
Directory.CreateDirectory(root);

try
{
    // ---- 1. Correctness verification (candidates replayed against the native engine) ----
    if (!options.SkipVerify)
    {
        Console.WriteLine("Verifying engines against the native reference…");
        bool allOk = true;
        foreach (string engine in options.Engines.Where(e => e != "native"))
        {
            foreach (string scenarioName in options.Scenarios)
            {
                var scenario = Scenarios.Get(scenarioName);
                string dir = Path.Combine(root, "verify", engine, scenarioName);
                string? err = scenario.Verify(engine, dir);
                Console.WriteLine($"  {engine,-9} {scenarioName,-9} {(err is null ? "OK" : "MISMATCH")}");
                if (err is not null)
                {
                    Console.WriteLine($"    {err}");
                    allOk = false;
                }
            }
        }
        if (!allOk)
        {
            Console.WriteLine("Verification failed — benchmark aborted; numbers from a wrong index are meaningless.");
            return 1;
        }
        Console.WriteLine();
    }

    // ---- 2. Benchmarks ----
    var results = new List<BenchResult>();
    foreach (string scenarioName in options.Scenarios)
    {
        foreach (string engine in options.Engines)
        {
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
    foreach (string scenarioName in options.Scenarios)
    {
        Console.WriteLine();
        PrintTable(scenarioName, results.Where(r => r.Scenario == scenarioName).ToList());
    }
    Console.WriteLine();
    PrintNotes();
    return results.Any(r => r.Error != null) ? 1 : 0;
}
finally
{
    TryDelete(root);
}

static BenchResult RunChild(string engine, string scenarioName, Options options, string dir)
{
    string exe = Environment.ProcessPath!;
    var psi = new ProcessStartInfo(exe)
    {
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

static void PrintTable(string scenarioName, List<BenchResult> rows)
{
    string[] header = ["Engine", "Insert/s", "Read/s", "GetIds/s", "Range rows/s", "RangeCnt/s", "Update/s", "DurTx/s", "Remove/s", "Mem MB", "WSet MB", "Disk MB"];
    var table = new List<string[]> { header };
    foreach (var r in rows)
    {
        if (r.Error is not null && r.Phases.Count == 0)
        {
            table.Add([Engines.DisplayName(r.Engine), "FAILED", "", "", "", "", "", "", "", "", "", ""]);
            continue;
        }
        table.Add([
            Engines.DisplayName(r.Engine),
            Rate(r.Phase("Insert")), Rate(r.Phase("PointRead")), Rate(r.Phase("GetIds")),
            Rate(r.Phase("RangeScan")), Rate(r.Phase("RangeCount")), Rate(r.Phase("Update")),
            Rate(r.Phase("DurableTx")), Rate(r.Phase("Remove")),
            r.ManagedMB.ToString("0.0"), r.WorkingSetMB.ToString("0.0"), r.DiskMB.ToString("0.0"),
        ]);
    }
    int[] widths = new int[header.Length];
    foreach (var row in table)
        for (int c = 0; c < row.Length; c++)
            widths[c] = Math.Max(widths[c], row[c].Length);

    Console.WriteLine($"— {scenarioName} —");
    for (int i = 0; i < table.Count; i++)
    {
        var sb = new StringBuilder("  ");
        for (int c = 0; c < table[i].Length; c++)
        {
            string cell = table[i][c];
            sb.Append(c == 0 ? cell.PadRight(widths[c]) : cell.PadLeft(widths[c]));
            if (c < table[i].Length - 1) sb.Append("  ");
        }
        Console.WriteLine(sb.ToString());
        if (i == 0) Console.WriteLine("  " + string.Join("  ", widths.Select(w => new string('-', w))));
    }
    foreach (var r in rows.Where(r => r.Error is not null))
        Console.WriteLine($"  ! {Engines.DisplayName(r.Engine)}: {r.Error}");
}

static string Rate(PhaseResult? p)
{
    if (p is null) return "-";
    double v = p.Rate;
    return v >= 10_000_000 ? $"{v / 1e6:0.0}M"
        : v >= 1_000_000 ? $"{v / 1e6:0.00}M"
        : v >= 10_000 ? $"{v / 1e3:0}k"
        : v >= 1_000 ? $"{v / 1e3:0.0}k"
        : $"{v:0}";
}

static void PrintNotes()
{
    Console.WriteLine("""
        Notes
          - All engines implement the same ISortedIndex<T> contract and were verified against the
            native engine on identical op streams before timing.
          - Insert/Update/Remove run in transactions of 20k ops; Insert ends with one durable commit.
          - Read/s: point lookups by id (10% misses). Range rows/s: rows yielded by GetIdsInRange
            over windows of ~1k rows. DurTx/s: small durable (fsync) transactions of 10 ops.
          - Mem MB: managed heap growth after load (full GC). WSet MB: working-set growth (includes
            native memory: SQLite page cache, FASTER log, ZoneTree segments). Disk MB: store size
            after the loaded state was durably committed.
          - NativeKv: 64 MB page cache, copy-on-write B+Tree, durable commit = flush + fsync'd meta.
          - SQLite: WAL mode, synchronous=FULL, 64 MB cache, table (id PRIMARY KEY, v) + index (v, id).
          - ZoneTree: LSM; two trees per index (id→value and (value,id) composite). WAL=AsyncCompressed,
            so batched writes are buffered like the others, but its "durable" commit only saves
            metadata — ZoneTree exposes no group-commit/fsync primitive, so DurTx/s overstates it.
          - FASTER: hash KV store — no ordered scans. Ordered ops are served by an in-memory
            SortedSet of (value,id) keys maintained beside the store (rebuilt on open); its cost
            shows in Mem MB. Durable commit = FoldOver checkpoint.
        """);
}

static void TryDelete(string dir)
{
    try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    catch { /* still held by a child or AV scanner; the temp root is cleaned next run */ }
}

sealed class Options
{
    public int N = 500_000;
    public string[] Engines = KvBenchmarks.Harness.Engines.All;
    public string[] Scenarios = ["int", "long", "string", "guid", "datetime"];
    public string DataDir = Path.GetTempPath();
    public bool SkipVerify;
    public bool InProcess;
    public string? ChildEngine, ChildScenario, ChildDir;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        foreach (string a in args)
        {
            string[] kv = a.Split('=', 2);
            switch (kv[0])
            {
                case "--n": o.N = int.Parse(kv[1]); break;
                case "--engines": o.Engines = kv[1] == "all" ? KvBenchmarks.Harness.Engines.All : kv[1].Split(','); break;
                case "--scenarios": o.Scenarios = kv[1] == "all" ? ["int", "long", "string", "guid", "datetime"] : kv[1].Split(','); break;
                case "--data": o.DataDir = kv[1]; break;
                case "--no-verify": o.SkipVerify = true; break;
                case "--in-process": o.InProcess = true; break;
                case "--child-engine": o.ChildEngine = kv[1]; break;
                case "--child-scenario": o.ChildScenario = kv[1]; break;
                case "--child-dir": o.ChildDir = kv[1]; break;
                default: throw new ArgumentException($"Unknown option '{a}'.");
            }
        }
        return o;
    }
}
