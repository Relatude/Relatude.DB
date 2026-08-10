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

    /// <summary>Reads per insert in the mixed phase, and how many inserts one of its transactions carries.</summary>
    public const int MixedReadsPerWrite = 3;
    public const int MixedBatchSize = 5_000;

    public static readonly string[] PhaseNames =
        ["Insert", "PointRead", "GetIds", "RangeScan", "RangeCount", "Update", "Mixed", "DurableTx", "Remove"];

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
        int mixedWrites = Math.Clamp(n / 4, 1_000, 50_000);

        // The hash layout has no ordering to measure: it skips the ordered phases, which show as
        // "-" in the report, and does not pay for their setup either. (It is exactly the layout
        // whose index does not implement ISortedIntIndex, which is what gates the phases below.)
        bool ordered = !Engines.IsHashLayout(engineName);

        // Range windows over the sorted inserted values: [from..to] spans ~window entries.
        var windows = new (T From, T To)[ordered ? rangeQueries : 0];
        if (ordered)
        {
            T[] sorted = (T[])values.Clone();
            Array.Sort(sorted, (a, b) => OrderedCodec.Compare(OrderedCodec.EncodeValue(a), OrderedCodec.EncodeValue(b)));
            for (int i = 0; i < rangeQueries; i++)
            {
                int s = rnd.Next(0, Math.Max(1, n - window));
                windows[i] = (sorted[s], sorted[Math.Min(n - 1, s + window - 1)]);
            }
        }

        int[] readIds = new int[reads];
        for (int i = 0; i < reads; i++) readIds[i] = rnd.NextDouble() < 0.9 ? rnd.Next(n) : n + rnd.Next(n); // 10 % misses
        int[] updateIds = new int[updates];
        T[] updateValues = new T[updates];
        for (int i = 0; i < updates; i++) { updateIds[i] = rnd.Next(n); updateValues[i] = scenario.Next(rnd, n); }
        int[] removeIds = Enumerable.Range(0, n).ToArray();
        rnd.Shuffle(removeIds);

        // Mixed phase: ids the store has never seen, in random order (a sequential run would hand
        // the ordered layouts a rightmost-leaf append the hash ones can never have).
        int[] mixedWriteIds = Enumerable.Range(n, mixedWrites).ToArray();
        rnd.Shuffle(mixedWriteIds);
        T[] mixedWriteValues = new T[mixedWrites];
        for (int i = 0; i < mixedWrites; i++) mixedWriteValues[i] = scenario.Next(rnd, n);

        // Only even-indexed inserts are ever deleted, and only long after they were written, so
        // reads can safely chase the odd-indexed ones and deletes hit keys that have had time to
        // settle into pages and segments rather than ones still sitting in a write buffer.
        int mixedRemoveLag = Math.Max(2, (mixedWrites / 4) & ~1); // even: keeps deletes on even indexes
        int[] mixedReadIds = new int[mixedWrites * MixedReadsPerWrite];
        for (int i = 0; i < mixedWrites; i++)
        {
            for (int r = 0; r < MixedReadsPerWrite; r++)
            {
                // A quarter of the reads chase ids this phase inserted (the hot set a real
                // workload re-reads); the rest hit the loaded data.
                mixedReadIds[i * MixedReadsPerWrite + r] = i >= 2 && rnd.Next(4) == 0
                    ? mixedWriteIds[1 + 2 * rnd.Next(i / 2)] // a random odd index below i: never deleted
                    : rnd.Next(n);
            }
        }

        ForceGc();
        long managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        long wsBefore = Environment.WorkingSet;

        using var engineDisposable = (IDisposable)Engines.Create(engineName, dir);
        var engine = (IStorageEngine)engineDisposable;
        IIntIndex<T> index = Engines.OpenBenchIndex<T>(engine, engineName);
        var sortedIndex = index as ISortedIntIndex<T>; // null only for the hash layout
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

        // Read-only phases warm up untimed until the process reaches steady state. A fixed
        // iteration count is not enough: an engine whose read path is a deep generic call chain
        // (FASTER most of all) is still tiering up after tens of thousands of calls, and measuring
        // it there reports the JIT ramp as if it were the engine — a 3x error, measured.
        // (Write phases can't be warmed without mutating the state they are about to be measured on.)
        int warm = Math.Min(20_000, n);

        // ---- Point reads ----
        Progress("point reads");
        Warm(warm, i => index.TryGetValue(readIds[i], out _));
        long found = 0;
        sw.Restart();
        for (int i = 0; i < reads; i++)
            if (index.TryGetValue(readIds[i], out _)) found++;
        sw.Stop();
        result.Phases.Add(new("PointRead", reads, sw.Elapsed.TotalSeconds));
        if (found == 0) result.Error = "sanity: no point read found anything";

        if (sortedIndex is not null)
        {
            // ---- GetIds(value) ----
            // Only for layouts with a value index. The hash layout answers this by scanning every
            // bucket, so running it here would time an O(n) operation against O(log n) ones.
            Progress("GetIds");
            Warm(warm, i => sortedIndex.GetIds(values[i]).Count());
            long idHits = 0;
            sw.Restart();
            for (int i = 0; i < getIdsOps; i++)
            {
                foreach (int _ in sortedIndex.GetIds(values[rnd.Next(n)])) idHits++;
            }
            sw.Stop();
            result.Phases.Add(new("GetIds", getIdsOps, sw.Elapsed.TotalSeconds));
            if (idHits < getIdsOps) result.Error ??= "sanity: GetIds returned fewer ids than lookups";

            // ---- Range scans (rows/sec) ----
            Progress("range scans");
            Warm(25, i => sortedIndex.GetIdsInRange(windows[i % rangeQueries].From, windows[i % rangeQueries].To).Count());
            long rows = 0;
            sw.Restart();
            for (int i = 0; i < rangeQueries; i++)
            {
                var (from, to) = windows[i];
                foreach (int _ in sortedIndex.GetIdsInRange(from, to)) rows++;
            }
            sw.Stop();
            result.Phases.Add(new("RangeScan", rows, sw.Elapsed.TotalSeconds));

            // ---- Range counts ----
            Progress("range counts");
            Warm(25, i => sortedIndex.CountIdsInRange(windows[i % rangeQueries].From, windows[i % rangeQueries].To));
            long counted = 0;
            sw.Restart();
            for (int i = 0; i < rangeQueries; i++)
            {
                var (from, to) = windows[i];
                counted += sortedIndex.CountIdsInRange(from, to);
            }
            sw.Stop();
            result.Phases.Add(new("RangeCount", rangeQueries, sw.Elapsed.TotalSeconds));
            if (counted != rows) result.Error ??= $"sanity: RangeCount total {counted} != RangeScan rows {rows}";
        }

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

        // ---- Mixed inserts, reads and deletes (ops/sec) ----
        // Every other phase measures a store that holds still, one operation at a time. This one
        // interleaves all three in short transactions, so lookups work against a structure that is
        // churning underneath them — leaf and bucket splits, directory doublings, freed pages, LSM
        // segment merges, tombstones — which is the state a live store is usually in.
        Progress("mixed");
        long mixedFound = 0, mixedRemoved = 0, mixedRemoveAttempts = 0;
        sw.Restart();
        for (int i = 0; i < mixedWrites;)
        {
            engine.BeginTransaction();
            int end = Math.Min(mixedWrites, i + MixedBatchSize);
            for (; i < end; i++)
            {
                index.Set(mixedWriteIds[i], mixedWriteValues[i]);
                int firstRead = i * MixedReadsPerWrite;
                for (int r = 0; r < MixedReadsPerWrite; r++)
                    if (index.TryGetValue(mixedReadIds[firstRead + r], out _)) mixedFound++;
                if (i >= mixedRemoveLag && (i & 1) == 0)
                {
                    mixedRemoveAttempts++;
                    if (index.Remove(mixedWriteIds[i - mixedRemoveLag])) mixedRemoved++;
                }
            }
            engine.CommitTransaction(++ts, durable: false);
        }
        sw.Stop();
        long mixedOps = mixedWrites + (long)mixedWrites * MixedReadsPerWrite + mixedRemoveAttempts;
        result.Phases.Add(new("Mixed", mixedOps, sw.Elapsed.TotalSeconds));
        // Every read targets an id that is in the store and every delete an id this phase wrote,
        // so a miss means an operation went astray under the churn around it.
        if (mixedFound != (long)mixedWrites * MixedReadsPerWrite)
            result.Error ??= $"sanity: mixed reads found {mixedFound} of {(long)mixedWrites * MixedReadsPerWrite}";
        if (mixedRemoved != mixedRemoveAttempts)
            result.Error ??= $"sanity: mixed deletes removed {mixedRemoved} of {mixedRemoveAttempts}";

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

        int expected = n + mixedWrites - (int)mixedRemoved - removes; // net of what the mixed phase added and deleted
        if (index.Count != expected) result.Error ??= $"sanity: final Count {index.Count}, expected {expected}";
        return result;
    }

    /// <summary>Milliseconds of untimed work a read-only phase does before it is measured.</summary>
    private const int WarmupMs = 300;

    /// <summary>Repeats <paramref name="body"/> over <paramref name="iterations"/> until the phase has been warm for <see cref="WarmupMs"/>.</summary>
    private static void Warm(int iterations, Action<int> body)
    {
        var clock = Stopwatch.StartNew();
        do
        {
            for (int i = 0; i < iterations; i++) body(i);
        } while (clock.ElapsedMilliseconds < WarmupMs);
    }

    private static void Progress(string phase) => Console.Error.Write($" {phase}…");

    private static void ForceGc()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }
}
