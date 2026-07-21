using System.Diagnostics;
using Relatude.DB.Datastores.Indexes.BTreeIndex;

namespace KvBenchmarks.Harness;

public sealed record PhaseResult(string Name, long Ops, double Seconds)
{
    public double Rate => Seconds > 0 ? Ops / Seconds : 0;
}

public sealed class BenchResult
{
    public string Engine { get; set; } = "";
    public string Scenario { get; set; } = "";
    public int N { get; set; }
    public List<PhaseResult> Phases { get; set; } = new();
    public double ManagedMB { get; set; }
    public double WorkingSetMB { get; set; }
    public double DiskMB { get; set; }
    public string? Error { get; set; }

    public PhaseResult? Phase(string name) => Phases.FirstOrDefault(p => p.Name == name);
}

public static class BenchRunner
{
    public const int BatchSize = 50_000;

    public static readonly string[] PhaseNames =
        ["Insert", "PointRead", "GetIds", "RangeScan", "RangeCount", "Update", "DurableTx", "Remove"];

    public static BenchResult Run<T>(Scenario<T> scenario, string engineName, int n, string dir) where T : notnull
    {
        var result = new BenchResult { Engine = engineName, Scenario = scenario.Name, N = n };
        var rnd = new Random(9000 + scenario.Name.GetHashCode(StringComparison.Ordinal)); // same stream for every engine

        // Pre-generate the workload so generation cost never lands inside a timed phase.
        T[] values = new T[n];
        for (int i = 0; i < n; i++) values[i] = scenario.Next(rnd, n);
        int[] insertOrder = Enumerable.Range(0, n).ToArray();
        rnd.Shuffle(insertOrder);

        int reads = Math.Min(200_000, Math.Max(n, 10_000));
        int getIdsOps = Math.Min(20_000, n);
        int rangeQueries = 500;
        int window = Math.Min(1000, Math.Max(10, n / 100));
        int updates = Math.Min(100_000, n);
        int durableTxns = 100, durableOpsPerTxn = 10;
        int removes = n / 4;

        // Range windows over the sorted inserted values: [from..to] spans ~window entries.
        T[] sorted = (T[])values.Clone();
        Array.Sort(sorted, (a, b) => OrderedCodec.Compare(OrderedCodec.EncodeValue(a), OrderedCodec.EncodeValue(b)));
        var windows = new (T From, T To)[rangeQueries];
        for (int i = 0; i < rangeQueries; i++)
        {
            int s = rnd.Next(0, Math.Max(1, n - window));
            windows[i] = (sorted[s], sorted[Math.Min(n - 1, s + window - 1)]);
        }

        int[] readIds = new int[reads];
        for (int i = 0; i < reads; i++) readIds[i] = rnd.NextDouble() < 0.9 ? rnd.Next(n) : n + rnd.Next(n); // 10 % misses
        int[] updateIds = new int[updates];
        T[] updateValues = new T[updates];
        for (int i = 0; i < updates; i++) { updateIds[i] = rnd.Next(n); updateValues[i] = scenario.Next(rnd, n); }
        int[] removeIds = Enumerable.Range(0, n).ToArray();
        rnd.Shuffle(removeIds);

        ForceGc();
        long managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        long wsBefore = Environment.WorkingSet;

        using var engineDisposable = (IDisposable)Engines.Create(engineName, dir);
        var engine = (IStorageEngine)engineDisposable;
        var index = engine.OpenOrCreateIndex<T>("bench");
        long ts = 0;
        var sw = new Stopwatch();

        // ---- Insert (batched transactions, one durable commit at the end) ----
        Progress("insert");
        sw.Restart();
        for (int i = 0; i < n;)
        {
            engine.BeginTransaction();
            int end = Math.Min(n, i + BatchSize);
            for (; i < end; i++)
            {
                int id = insertOrder[i];
                index.Set(id, values[id]);
            }
            bool last = i == n;
            engine.CommitTransaction(++ts, durable: last);
        }
        // LSM engines buffer in memory; force the loaded state onto disk so it is really
        // persisted before it is measured and later reads exercise the disk path too.
        (engine as KvBenchmarks.Engines.IBenchFlush)?.FlushAllToDisk();
        sw.Stop();
        result.Phases.Add(new("Insert", n, sw.Elapsed.TotalSeconds));

        // ---- Memory and disk right after the loaded, durably committed state ----
        ForceGc();
        result.ManagedMB = Math.Max(0, (GC.GetTotalMemory(forceFullCollection: true) - managedBefore) / (1024.0 * 1024.0));
        result.WorkingSetMB = Math.Max(0, (Environment.WorkingSet - wsBefore) / (1024.0 * 1024.0));
        result.DiskMB = engine.GetTotalDiskSpace() / (1024.0 * 1024.0);

        // Read-only phases get a short untimed warmup: they are brief enough that tiered-JIT
        // ramp-up would otherwise be a visible slice of the measured time. (Write phases can't
        // be warmed without mutating the state they are about to be measured on.)
        int warm = Math.Min(5_000, n);

        // ---- Point reads ----
        Progress("point reads");
        for (int i = 0; i < warm; i++) index.TryGetValue(readIds[i], out _);
        long found = 0;
        sw.Restart();
        for (int i = 0; i < reads; i++)
            if (index.TryGetValue(readIds[i], out _)) found++;
        sw.Stop();
        result.Phases.Add(new("PointRead", reads, sw.Elapsed.TotalSeconds));
        if (found == 0) result.Error = "sanity: no point read found anything";

        // ---- GetIds(value) ----
        Progress("GetIds");
        for (int i = 0; i < warm; i++) index.GetIds(values[i]).Count();
        long idHits = 0;
        sw.Restart();
        for (int i = 0; i < getIdsOps; i++)
        {
            foreach (int _ in index.GetIds(values[rnd.Next(n)])) idHits++;
        }
        sw.Stop();
        result.Phases.Add(new("GetIds", getIdsOps, sw.Elapsed.TotalSeconds));
        if (idHits < getIdsOps) result.Error ??= "sanity: GetIds returned fewer ids than lookups";

        // ---- Range scans (rows/sec) ----
        Progress("range scans");
        for (int i = 0; i < 25; i++) index.GetIdsInRange(windows[i % rangeQueries].From, windows[i % rangeQueries].To).Count();
        long rows = 0;
        sw.Restart();
        for (int i = 0; i < rangeQueries; i++)
        {
            var (from, to) = windows[i];
            foreach (int _ in index.GetIdsInRange(from, to)) rows++;
        }
        sw.Stop();
        result.Phases.Add(new("RangeScan", rows, sw.Elapsed.TotalSeconds));

        // ---- Range counts ----
        Progress("range counts");
        for (int i = 0; i < 25; i++) index.CountIdsInRange(windows[i % rangeQueries].From, windows[i % rangeQueries].To);
        long counted = 0;
        sw.Restart();
        for (int i = 0; i < rangeQueries; i++)
        {
            var (from, to) = windows[i];
            counted += index.CountIdsInRange(from, to);
        }
        sw.Stop();
        result.Phases.Add(new("RangeCount", rangeQueries, sw.Elapsed.TotalSeconds));
        if (counted != rows) result.Error ??= $"sanity: RangeCount total {counted} != RangeScan rows {rows}";

        // ---- Updates ----
        Progress("updates");
        sw.Restart();
        for (int i = 0; i < updates;)
        {
            engine.BeginTransaction();
            int end = Math.Min(updates, i + BatchSize);
            for (; i < end; i++)
                index.Set(updateIds[i], updateValues[i]);
            engine.CommitTransaction(++ts, durable: false);
        }
        sw.Stop();
        result.Phases.Add(new("Update", updates, sw.Elapsed.TotalSeconds));

        // ---- Small durable transactions (txns/sec) ----
        Progress("durable txns");
        sw.Restart();
        for (int i = 0; i < durableTxns; i++)
        {
            engine.BeginTransaction();
            for (int j = 0; j < durableOpsPerTxn; j++)
                index.Set(rnd.Next(n), values[rnd.Next(n)]);
            engine.CommitTransaction(++ts, durable: true);
        }
        sw.Stop();
        result.Phases.Add(new("DurableTx", durableTxns, sw.Elapsed.TotalSeconds));

        // ---- Removes ----
        Progress("removes");
        long removed = 0;
        sw.Restart();
        for (int i = 0; i < removes;)
        {
            engine.BeginTransaction();
            int end = Math.Min(removes, i + BatchSize);
            for (; i < end; i++)
                if (index.Remove(removeIds[i])) removed++;
            engine.CommitTransaction(++ts, durable: false);
        }
        sw.Stop();
        result.Phases.Add(new("Remove", removes, sw.Elapsed.TotalSeconds));
        if (removed != removes) result.Error ??= $"sanity: removed {removed} of {removes}";

        int expected = n - removes;
        if (index.Count != expected) result.Error ??= $"sanity: final Count {index.Count}, expected {expected}";
        return result;
    }

    private static void Progress(string phase) => Console.Error.Write($" {phase}…");

    private static void ForceGc()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }
}
