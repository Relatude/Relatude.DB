using System.Diagnostics;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Indexes.KvStore;
using Relatude.DB.Nodes;

namespace Relatude.Querying;

#region shop-profile datamodel (mirrors Website.Simple's Product/Brand, no text index)
[Node]
public class FqProduct {
    [InternalIdProperty] public int Id { get; set; }
    [StringProperty(Indexed = true)] public string Name { get; set; } = "";
    [StringProperty(Indexed = true)] public string Category { get; set; } = "";
    [DoubleProperty(Indexed = true)] public double Price { get; set; }
    [BooleanProperty(Indexed = true)] public bool InStock { get; set; }
    [StringArrayProperty(Indexed = true)] public string[] Tags { get; set; } = [];
    [ReferenceProperty(Indexed = true)] public Reference<FqBrand> Brand { get; set; } = new();
}
[Node]
public class FqBrand {
    [PublicIdProperty] public Guid Id { get; set; }
    [StringProperty(Indexed = true, DisplayName = true)] public string Name { get; set; } = "";
}
#endregion

// Reproduces the Website.Simple first-page slowness: landing facet query (5 facets, no search,
// no selection) at 10M nodes on persisted Native KV value indexes. Attribution per facet via
// Maintenance(ClearCache) between queries (clears set register + per-value ids cache + gap keys;
// engine page cache is 64MB ≈ cold anyway relative to the file). Run with:
//   FQ_PERF=1 dotnet test -c Release --filter FullyQualifiedName~FirstQueryPerfHarness
[TestClass]
public class FirstQueryPerfHarness {
    static readonly int Count = int.TryParse(Environment.GetEnvironmentVariable("FQ_COUNT"), out var c) ? c : 10_000_000;
    static readonly string _log = @"C:\Users\ogulb\AppData\Local\Temp\claude\firstquery.log";
    static readonly string[] _cats = ["Furniture", "Electronics", "Outdoor", "Kitchen", "Clothing", "Toys"];
    static readonly string[] _tags = ["bestseller", "eco", "new", "sale", "premium", "handmade", "limited"];
    static readonly string[] _brandNames = ["Fjellrev", "Nordlys", "Kvist & Co", "Bluewhale", "Habitat 7", "Solvind", "Granheim", "Urban Nest", "Polarix", "Drivved", "Lysne", "Vandrer"];

    [TestMethod]
    public void MeasureFirstQuery() {
        if (Environment.GetEnvironmentVariable("FQ_PERF") != "1") return;
        var dir = @"C:\Users\ogulb\AppData\Local\Temp\claude\firstquery-db-" + Count;
        var dm = new Datamodel();
        dm.Add<FqProduct>();
        dm.Add<FqBrand>();
        var settings = new SettingsLocal {
            NodeCacheSizeGb = 3,
            SetCacheSizeGb = 10,
            UsePersistedValueIndexesByDefault = true,
            PersistedValueIndexEngine = PersistedValueIndexEngine.Native,
            UsePersistedTextIndexesByDefault = false,
        };
        Directory.CreateDirectory(dir);
        var swOpen = Stopwatch.StartNew();
        var storeData = DataStoreLocal.Open(dm, settings, new DB.IO.IOProviderDisk(dir), null, null, null, null, () => new NativeKvIndexStore(dir, null));
        var store = new NodeStore(storeData);
        swOpen.Stop();
        try {
            var wait = int.TryParse(Environment.GetEnvironmentVariable("FQ_WAIT"), out var w) ? w : 0;
            if (wait > 0) Thread.Sleep(wait); // simulates the user arriving a moment after startup (mirror warm-up done)
            seedIfEmpty(store);
            var sidecar = Path.Combine(dir, "nativekv", "facetsets.bin");
            var sidecarInfo = File.Exists(sidecar) ? (new FileInfo(sidecar).Length / 1024 / 1024) + "MB" : "none";
            log($"==== first query perf {DateTime.Now:HH:mm:ss} nodes={store.Query<FqProduct>().Count():n0} open={swOpen.Elapsed.TotalSeconds:0.0}s wait={wait}ms kvSize={kvSizeMb(dir):n0}MB sidecar={sidecarInfo} ====");

            double t(string label, Func<long> run) {
                var sw = Stopwatch.StartNew();
                var sig = run();
                sw.Stop();
                log($"  {label}: {sw.Elapsed.TotalMilliseconds,9:0.0} ms (sig {sig})");
                return sw.Elapsed.TotalMilliseconds;
            }
            long landing() {
                var res = store.Query<FqProduct>().Facets()
                    .AddValueFacet("Category").AddValueFacet("Brand").AddRangeFacet("Price").AddValueFacet("InStock").AddValueFacet("Tags")
                    .SetFacetOptions("Tags", maxValues: 8, sortByCount: true)
                    .Page(0, 10).Execute();
                long s = res.TotalCount;
                foreach (var f in res.Facets) foreach (var v in f.Values) s = s * 31 + v.Count;
                return s;
            }
            long single(string prop, bool range = false) {
                var q = store.Query<FqProduct>().Facets();
                q = range ? q.AddRangeFacet(prop) : q.AddValueFacet(prop);
                var res = q.Page(0, 10).Execute();
                long s = res.TotalCount;
                foreach (var f in res.Facets) foreach (var v in f.Values) s = s * 31 + v.Count;
                return s;
            }
            long filtered() { // value facets counted against the selection's filtered set: the per-value cache path
                var res = store.Query<FqProduct>().Facets()
                    .AddValueFacet("Category").AddValueFacet("Brand").AddValueFacet("InStock")
                    .SetFacetValue("Category", "Furniture")
                    .Page(0, 10).Execute();
                long s = res.TotalCount;
                foreach (var f in res.Facets) foreach (var v in f.Values) s = s * 31 + v.Count;
                return s;
            }
            t("landing 5 facets, FIRST (all caches cold)", landing);
            t("landing 5 facets, repeat (warm)", landing);
            t("filtered Category=Furniture, FIRST", filtered);
            t("filtered Category=Furniture, repeat (warm)", filtered);
            clearAll(storeData);
            t("landing 5 facets, after ClearCache", landing);

            log("  -- per facet attribution (ClearCache before each) --");
            clearAll(storeData); t("Category only", () => single("Category"));
            clearAll(storeData); t("Brand only", () => single("Brand"));
            clearAll(storeData); t("InStock only", () => single("InStock"));
            clearAll(storeData); t("Tags only", () => single("Tags"));
            clearAll(storeData); t("Price range only", () => single("Price", range: true));
            log("  -- warm per facet (no clear) --");
            t("Category only (warm)", () => single("Category"));
            t("Price range only (warm)", () => single("Price", range: true));
        } finally {
            store.Dispose();
        }
    }

    static void clearAll(DataStoreLocal storeData) => storeData.Maintenance(MaintenanceAction.ClearCache);

    static long kvSizeMb(string dir) {
        var f = Path.Combine(dir, "nativekv", "nativekv.db");
        return File.Exists(f) ? new FileInfo(f).Length / 1024 / 1024 : 0;
    }

    void seedIfEmpty(NodeStore store) {
        if (store.Query<FqProduct>().Count() >= Count) return;
        var sw = Stopwatch.StartNew();
        var rnd = new Random(2026);
        var brands = _brandNames.Select(n => new FqBrand { Id = Guid.NewGuid(), Name = n }).ToList();
        store.Insert(brands);
        var batch = new List<FqProduct>(1000);
        for (var i = 0; i < Count; i++) {
            batch.Add(new FqProduct {
                Name = "Product " + i,
                Category = _cats[rnd.Next(_cats.Length)],
                Price = Math.Round(9 + Math.Pow(rnd.NextDouble(), 2) * 1990, 2),
                InStock = rnd.Next(5) > 0,
                Tags = Enumerable.Range(0, rnd.Next(3)).Select(_ => _tags[rnd.Next(_tags.Length)]).Distinct().ToArray(),
                Brand = new() { Id = brands[rnd.Next(brands.Count)].Id },
            });
            if (batch.Count == 1000) { store.Insert(batch); batch.Clear(); if (i % 1_000_000 < 1000) log($"  seeded {i + 1:n0} in {sw.Elapsed.TotalSeconds:0}s"); }
        }
        if (batch.Count > 0) store.Insert(batch);
        log($"  seed complete: {Count:n0} nodes in {sw.Elapsed.TotalSeconds:0}s");
    }

    static readonly object _lock = new();
    void log(string line) {
        lock (_lock) File.AppendAllText(_log, line + Environment.NewLine);
        Console.WriteLine(line);
    }
}
