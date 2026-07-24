using System.Diagnostics;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Indexes.KvStore;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.Nodes;

namespace Tests;

#region perf datamodel
[Node]
public class CpProduct {
    [InternalIdProperty] public int Id { get; set; }
    [StringProperty(Indexed = true)] public string Category { get; set; } = "";  // 6 distinct
    [StringProperty(Indexed = true)] public string Brand { get; set; } = "";     // 12 distinct
    [BooleanProperty(Indexed = true)] public bool InStock { get; set; }
    [DoubleProperty(Indexed = true)] public double Price { get; set; }           // ~200k distinct per 1M
}
#endregion

// A/B harness for the batch set construction (SetRegister.UseBatchCollect) and the persisted
// per-value id set cache (PersistedIndexBase.UseValueIdsCache). Both modes run in the same
// process on the same store; the set register cache is cleared before every timed run so each
// measurement pays the full set construction. Run with:
//   CP_PERF=1 dotnet test -c Release --filter FullyQualifiedName~CollectPerfHarness
// Sizes via CP_COUNT (memory, default 1M) and CPKV_COUNT (persisted, default 1M), iterations via CP_K.
[TestClass]
public class CollectPerfHarness {
    static readonly int MemCount = int.TryParse(Environment.GetEnvironmentVariable("CP_COUNT"), out var c) ? c : 1_000_000;
    static readonly int KvCount = int.TryParse(Environment.GetEnvironmentVariable("CPKV_COUNT"), out var kc) ? kc : 1_000_000;
    static readonly int K = int.TryParse(Environment.GetEnvironmentVariable("CP_K"), out var k) ? k : 20;
    static readonly string _log = @"C:\Users\ogulb\AppData\Local\Temp\claude\collectperf.log";
    static readonly string[] _cats = ["Furniture", "Electronics", "Outdoor", "Kitchen", "Clothing", "Toys"];
    static bool Enabled => Environment.GetEnvironmentVariable("CP_PERF") == "1";

    [TestMethod]
    public void MeasureMemoryCollect() {
        if (!Enabled) return;
        var dir = @"C:\Users\ogulb\AppData\Local\Temp\claude\collectperf-mem-db-" + MemCount;
        var store = open(dir, persisted: false, out var storeData);
        try {
            seedIfEmpty(store, MemCount);
            var sets = storeData._definition.Sets;
            log($"==== A memory collect {DateTime.Now:HH:mm:ss} nodes={store.Query<CpProduct>().Count():n0} K={K} ====");

            abScenario("A1 where Price<X count", sets, i => {
                var max = (double)(100 + i * 37 % 1500);
                return store.Query<CpProduct>().Where(p => p.Price < max).Count();
            });
            abScenario("A2 price range facet selected", sets, i => {
                var from = (double)(i * 53 % 800);
                var res = store.Query<CpProduct>().Facets()
                    .AddValueFacet("Category").AddValueFacet("Brand").AddRangeFacet("Price")
                    .SetFacetRangeValue("Price", from, from + 400)
                    .Page(0, 20).Execute();
                return sig(res);
            });
            abScenario("A3 two-category selection (WhereIn)", sets, i => {
                var res = store.Query<CpProduct>().Facets()
                    .AddValueFacet("Category").AddValueFacet("Brand").AddValueFacet("InStock")
                    .SetFacetValue("Category", _cats[i % 6]).SetFacetValue("Category", _cats[(i + 1) % 6])
                    .Page(0, 20).Execute();
                return sig(res);
            });
            abScenario("A4 control: landing, no selection", sets, i => {
                var res = store.Query<CpProduct>().Facets()
                    .AddValueFacet("Category").AddValueFacet("Brand").AddValueFacet("InStock")
                    .Page(0, 20).Execute();
                return sig(res);
            });
        } finally {
            store.Dispose();
        }
    }

    [TestMethod]
    public void MeasurePersistedIdsCache() {
        if (!Enabled) return;
        var dir = @"C:\Users\ogulb\AppData\Local\Temp\claude\collectperf-kv-db-" + KvCount;
        var store = open(dir, persisted: true, out var storeData);
        try {
            seedIfEmpty(store, KvCount);
            var sets = storeData._definition.Sets;
            log($"==== C persisted ids cache {DateTime.Now:HH:mm:ss} nodes={store.Query<CpProduct>().Count():n0} K={K} ====");

            // each iteration writes first (bumps every StateId, like production churn), then measures
            kvScenario("C1 landing, 3 value facets", sets, store, i => {
                var res = store.Query<CpProduct>().Facets()
                    .AddValueFacet("Category").AddValueFacet("Brand").AddValueFacet("InStock")
                    .Page(0, 20).Execute();
                return sig(res);
            });
            kvScenario("C2 category selection + counts", sets, store, i => {
                var res = store.Query<CpProduct>().Facets()
                    .AddValueFacet("Category").AddValueFacet("Brand").AddValueFacet("InStock")
                    .SetFacetValue("Category", _cats[i % 6])
                    .Page(0, 20).Execute();
                return sig(res);
            });
        } finally {
            store.Dispose();
        }
    }

    void abScenario(string label, SetRegister sets, Func<int, long> run) {
        SetRegister.UseBatchCollect = false; sets.ClearCache(); run(0);
        SetRegister.UseBatchCollect = true; sets.ClearCache(); run(0);
        double offSum = 0, onSum = 0, offMin = double.MaxValue, onMin = double.MaxValue;
        var mismatches = 0;
        for (var i = 0; i < K; i++) {
            var offMs = timed(sets, false, run, i, out var offSig);
            var onMs = timed(sets, true, run, i, out var onSig);
            if (offSig != onSig) mismatches++;
            offSum += offMs; onSum += onMs;
            offMin = Math.Min(offMin, offMs); onMin = Math.Min(onMin, onMs);
        }
        SetRegister.UseBatchCollect = true;
        report(label, offSum, onSum, offMin, onMin, mismatches);
    }
    double timed(SetRegister sets, bool batch, Func<int, long> run, int i, out long sig) {
        SetRegister.UseBatchCollect = batch;
        sets.ClearCache();
        var sw = Stopwatch.StartNew();
        sig = run(i);
        return sw.Elapsed.TotalMilliseconds;
    }

    void kvScenario(string label, SetRegister sets, NodeStore store, Func<int, long> run) {
        void probeWrite(int i) { // touches every index, evicting only the probe's values from the per-value cache
            var probe = new CpProduct { Category = _cats[i % 6], Brand = "Brand A", InStock = true, Price = 1.23 };
            store.Insert(probe, out Guid id);
            store.Delete(id);
        }
        PersistedIndexBase.UseValueIdsCache = true; probeWrite(0); sets.ClearCache(); run(0); // warm cache + JIT
        PersistedIndexBase.UseValueIdsCache = false; sets.ClearCache(); run(0);
        double offSum = 0, onSum = 0, offMin = double.MaxValue, onMin = double.MaxValue;
        var mismatches = 0;
        for (var i = 0; i < K; i++) {
            probeWrite(i);
            PersistedIndexBase.UseValueIdsCache = false;
            sets.ClearCache();
            var sw = Stopwatch.StartNew();
            var offSig = run(i);
            var offMs = sw.Elapsed.TotalMilliseconds;
            PersistedIndexBase.UseValueIdsCache = true;
            sets.ClearCache();
            sw.Restart();
            var onSig = run(i);
            var onMs = sw.Elapsed.TotalMilliseconds;
            if (offSig != onSig) mismatches++;
            offSum += offMs; onSum += onMs;
            offMin = Math.Min(offMin, offMs); onMin = Math.Min(onMin, onMs);
        }
        PersistedIndexBase.UseValueIdsCache = true;
        report(label, offSum, onSum, offMin, onMin, mismatches);
    }

    void report(string label, double offSum, double onSum, double offMin, double onMin, int mismatches) {
        var offAvg = offSum / K; var onAvg = onSum / K;
        var pct = offAvg > 0 ? (offAvg - onAvg) / offAvg * 100 : 0;
        log($"  {label}");
        log($"    OFF avg {offAvg,7:0.00} ms (min {offMin,6:0.00})   ON avg {onAvg,7:0.00} ms (min {onMin,6:0.00})   delta {offAvg - onAvg,6:0.00} ms ({pct,5:0.0}%)   parity {(mismatches == 0 ? "OK" : "MISMATCH x" + mismatches)}");
    }

    static long sig<T>(Relatude.DB.Query.ResultSetFacets<T> res) {
        long s = res.TotalCount;
        foreach (var f in res.Facets) foreach (var v in f.Values) s = s * 31 + v.Count;
        return s;
    }

    NodeStore open(string dir, bool persisted, out DataStoreLocal storeData) {
        var dm = new Datamodel();
        dm.Add<CpProduct>();
        var settings = new SettingsLocal {
            NodeCacheSizeGb = 1,
            SetCacheSizeGb = 1,
            UsePersistedValueIndexesByDefault = persisted,
            PersistedValueIndexEngine = PersistedValueIndexEngine.Native,
            UsePersistedTextIndexesByDefault = false,
        };
        Directory.CreateDirectory(dir);
        storeData = DataStoreLocal.Open(dm, settings, new Relatude.DB.IO.IOProviderDisk(dir), null, null, null, null, () => new NativeKvIndexStore(persisted ? dir : null, null));
        return new NodeStore(storeData);
    }

    void seedIfEmpty(NodeStore store, int count) {
        if (store.Query<CpProduct>().Count() >= count) return;
        var sw = Stopwatch.StartNew();
        var rnd = new Random(2026);
        var batch = new List<CpProduct>(10000);
        for (var i = 0; i < count; i++) {
            batch.Add(new CpProduct {
                Category = _cats[rnd.Next(_cats.Length)],
                Brand = "Brand " + (char)('A' + rnd.Next(12)),
                InStock = rnd.Next(5) > 0,
                Price = Math.Round(9 + Math.Pow(rnd.NextDouble(), 2) * 1990, 2),
            });
            if (batch.Count == 10000) { store.Insert(batch); batch.Clear(); if (i % 200_000 < 10000) log($"  seeded {i + 1:n0} in {sw.Elapsed.TotalSeconds:0}s"); }
        }
        if (batch.Count > 0) store.Insert(batch);
        log($"  seed complete: {count:n0} nodes in {sw.Elapsed.TotalSeconds:0}s");
    }

    static readonly object _lock = new();
    void log(string line) {
        lock (_lock) File.AppendAllText(_log, line + Environment.NewLine);
        Console.WriteLine(line);
    }
}
