using System.Diagnostics;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Indexes.KvStore;
using Relatude.DB.Nodes;

namespace Tests;

#region perf datamodel (text indexed, unlike the functional facet test model)
[Node(TextIndex = BoolValue.True)]
public class PerfProduct {
    [InternalIdProperty]
    public int Id { get; set; }
    [StringProperty(Indexed = true)]
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    [StringProperty(Indexed = true, ExcludeFromTextIndex = true)]
    public string Category { get; set; } = "";
    [DoubleProperty(Indexed = true)]
    public double Price { get; set; }
    [BooleanProperty(Indexed = true)]
    public bool InStock { get; set; }
    [StringArrayProperty(Indexed = true, ExcludeFromTextIndex = true)]
    public string[] Tags { get; set; } = [];
    [ReferenceProperty(Indexed = true)]
    public Reference<PerfBrand> Brand { get; set; } = new();
}
[Node]
public class PerfBrand {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(Indexed = true, DisplayName = true)]
    public string Name { get; set; } = "";
}
#endregion

// Facet query performance harness at 1M nodes with persisted (native KV) value indexes,
// mirroring the Website.Simple setup. Not part of the normal suite: run explicitly with
//   FACET_PERF=1 dotnet test -c Release --filter FullyQualifiedName~FacetPerfHarness
// The store directory is reused between runs so the 1M seed only happens once.
[TestClass]
public class FacetPerfHarness {

    const int ProductCount = 1_000_000;
    static readonly string _dir = @"C:\Users\ogulb\AppData\Local\Temp\claude\facetperf-db";
    static readonly string _log = @"C:\Users\ogulb\AppData\Local\Temp\claude\facetperf.log";

    static readonly string[] _adjectives = ["Compact", "Classic", "Foldable", "Ergonomic", "Portable", "Sturdy", "Elegant", "Rustic", "Modern", "Silent", "Adjustable", "Ultralight"];
    static readonly string[] _materials = ["oak", "leather", "bamboo", "titanium", "wool", "canvas", "steel", "walnut", "aluminium", "cork", "linen", "plastic"];
    static readonly string[] _features = ["waterproof", "wireless", "stackable", "handmade", "foldable", "rechargeable", "washable", "weatherproof"];
    static readonly string[] _nouns = ["Chair", "Table", "Speaker", "Backpack", "Kettle", "Jacket", "Puzzle", "Lantern"];
    static readonly string[] _cats = ["Furniture", "Electronics", "Outdoor", "Kitchen", "Clothing", "Toys"];
    static readonly string[] _tags = ["bestseller", "eco", "new", "sale", "premium", "handmade", "limited"];

    [TestMethod]
    public void MeasureFacetQueries() {
        if (Environment.GetEnvironmentVariable("FACET_PERF") != "1") { Assert.Inconclusive("Set FACET_PERF=1 to run."); return; }
        var dm = new Datamodel();
        dm.Add<PerfProduct>();
        dm.Add<PerfBrand>();
        var settings = new SettingsLocal {
            NodeCacheSizeGb = 1,
            SetCacheSizeGb = 1,
            UsePersistedValueIndexesByDefault = false, // like Website.Simple: memory value indexes (bit set backed)
            PersistedValueIndexEngine = PersistedValueIndexEngine.Native,
            UsePersistedTextIndexesByDefault = false, // memory word index keeps the harness self-contained
        };
        Directory.CreateDirectory(_dir);
        var storeData = DataStoreLocal.Open(dm, settings, new Relatude.DB.IO.IOProviderDisk(_dir), null, null, null, null, () => new NativeKvIndexStore(null, null));
        var store = new NodeStore(storeData);
        try {
            seedIfEmpty(store);
            waitForTextIndexing(store);
            log($"==== run {DateTime.Now:HH:mm:ss} nodes={store.Query<PerfProduct>().Count()} ====");

            measure(store, "empty query, all facets (cold)", "");
            measure(store, "empty query, all facets (warm)", "");
            string[] terms = ["waterproof", "leather", "wireless", "bamboo", "titanium", "handmade", "walnut", "rechargeable", "canvas", "foldable"];
            var coldTotal = 0.0;
            foreach (var term in terms) coldTotal += measure(store, $"search '{term}' (cold)", term);
            log($"  avg cold search-facet query: {coldTotal / terms.Length:0.0} ms");
            measure(store, "search 'waterproof' (warm)", "waterproof");
            string[] narrow = ["leather chair", "bamboo lantern", "walnut table", "titanium kettle", "canvas backpack"];
            var narrowTotal = 0.0;
            foreach (var term in narrow) narrowTotal += measure(store, $"search '{term}' (cold, realistic)", term);
            log($"  avg cold realistic (two-word) search-facet query: {narrowTotal / narrow.Length:0.0} ms");
            measureWithSelection(store, "search 'leather' + Category=Outdoor (cold-ish)", "leather");
            breakdown(store, "");
            breakdown(store, "waterproof");
            pagingChecks(store);
        } finally {
            store.Dispose();
        }
    }

    void pagingChecks(NodeStore store) {
        double t(string label, Func<object> run, int reps = 3) { // best of N (warm)
            var best = double.MaxValue;
            for (var i = 0; i < reps; i++) {
                var sw = Stopwatch.StartNew();
                run();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            log($"  paging: {label}: {best:0.0} ms");
            return best;
        }
        t("unsorted page 0 (facet query, empty search)", () => store.Query<PerfProduct>().Facets().AddValueFacet("Category").Page(0, 20).Execute());
        t("unsorted page 5", () => store.Query<PerfProduct>().Facets().AddValueFacet("Category").Page(5, 20).Execute());
        t("unsorted page 500 (deep)", () => store.Query<PerfProduct>().Facets().AddValueFacet("Category").Page(500, 20).Execute());
        t("unsorted page 25000 (deepest)", () => store.Query<PerfProduct>().Facets().AddValueFacet("Category").Page(25_000, 20).Execute());
        t("plain query page 0 (no facets)", () => store.Query<PerfProduct>().Page(0, 20).Execute());
        t("plain query page 500", () => store.Query<PerfProduct>().Page(500, 20).Execute());
        t("sorted by Price page 0 (first, may build sort)", () => store.Query<PerfProduct>().OrderBy(p => p.Price).Page(0, 20).Execute(), 1);
        t("sorted by Price page 0 (repeat)", () => store.Query<PerfProduct>().OrderBy(p => p.Price).Page(0, 20).Execute());
        t("sorted by Price page 500 (repeat)", () => store.Query<PerfProduct>().OrderBy(p => p.Price).Page(500, 20).Execute());
        t("search 'waterproof' page 3", () => store.Query<PerfProduct>().WhereSearch("waterproof").Facets().AddValueFacet("Category").Page(3, 20).Execute());
        var info = store.Datastore.GetInfo();
        log($"  set cache: count={info.SetCacheCount} hits={info.SetCacheHits} misses={info.SetCacheMisses} overflows={info.SetCacheOverflows} sizeMb={info.SetCacheSize / 1024 / 1024}");
        log($"  aggregate cache: hits={info.AggregateCacheHits} misses={info.AggregateCacheMisses}");
    }

    void breakdown(NodeStore store, string term) {
        var label = term.Length == 0 ? "empty" : "'" + term + "'";
        double t(Func<object> run) { // warm: best of 3
            var best = double.MaxValue;
            for (var i = 0; i < 3; i++) {
                var sw = Stopwatch.StartNew();
                run();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            return best;
        }
        Relatude.DB.Query.IQueryOfNodes<PerfProduct, PerfProduct> q() { var x = store.Query<PerfProduct>(); return term.Length > 0 ? x.WhereSearch(term) : x; }
        log($"  breakdown {label}: search+count only: {t(() => q().Count()):0.0} ms");
        log($"  breakdown {label}: +Category: {t(() => q().Facets().AddValueFacet("Category").Page(0, 20).Execute()):0.0} ms");
        log($"  breakdown {label}: +Brand: {t(() => q().Facets().AddValueFacet("Brand").Page(0, 20).Execute()):0.0} ms");
        log($"  breakdown {label}: +InStock: {t(() => q().Facets().AddValueFacet("InStock").Page(0, 20).Execute()):0.0} ms");
        log($"  breakdown {label}: +Tags: {t(() => q().Facets().AddValueFacet("Tags").Page(0, 20).Execute()):0.0} ms");
        log($"  breakdown {label}: +Price ranges: {t(() => q().Facets().AddRangeFacet("Price").Page(0, 20).Execute()):0.0} ms");
    }

    double measure(NodeStore store, string label, string term) {
        var sw = Stopwatch.StartNew();
        var q = store.Query<PerfProduct>();
        if (term.Length > 0) q = q.WhereSearch(term);
        var res = q.Facets()
            .AddValueFacet("Category").AddValueFacet("Brand").AddRangeFacet("Price").AddValueFacet("InStock").AddValueFacet("Tags")
            .SetFacetOptions("Tags", maxValues: 8, sortByCount: true)
            .Page(0, 20)
            .Execute();
        sw.Stop();
        log($"  {label}: {sw.Elapsed.TotalMilliseconds:0.0} ms (total {res.TotalCount}, facets {res.Facets.Count()})");
        return sw.Elapsed.TotalMilliseconds;
    }

    void measureWithSelection(NodeStore store, string label, string term) {
        var sw = Stopwatch.StartNew();
        var res = store.Query<PerfProduct>().WhereSearch(term).Facets()
            .AddValueFacet("Category").AddValueFacet("Brand").AddRangeFacet("Price").AddValueFacet("InStock")
            .SetFacetValue("Category", "Outdoor")
            .Page(0, 20)
            .Execute();
        sw.Stop();
        log($"  {label}: {sw.Elapsed.TotalMilliseconds:0.0} ms (total {res.TotalCount})");
    }

    void seedIfEmpty(NodeStore store) {
        if (store.Query<PerfProduct>().Count() >= ProductCount) return;
        var sw = Stopwatch.StartNew();
        var rnd = new Random(2026);
        var brands = Enumerable.Range(0, 12).Select(i => new PerfBrand { Id = Guid.NewGuid(), Name = "Brand " + (char)('A' + i) }).ToList();
        store.Insert(brands);
        var batch = new List<PerfProduct>(5000);
        for (var i = 0; i < ProductCount; i++) {
            var material = _materials[rnd.Next(_materials.Length)];
            var feature = _features[rnd.Next(_features.Length)];
            var noun = _nouns[rnd.Next(_nouns.Length)];
            batch.Add(new PerfProduct {
                Name = $"{_adjectives[rnd.Next(_adjectives.Length)]} {material} {noun}",
                Description = $"A {noun.ToLower()} in {material}, {feature} and {_features[rnd.Next(_features.Length)]}.",
                Category = _cats[rnd.Next(_cats.Length)],
                Price = Math.Round(9 + Math.Pow(rnd.NextDouble(), 2) * 1990, 2),
                InStock = rnd.Next(5) > 0,
                Tags = Enumerable.Range(0, rnd.Next(3)).Select(_ => _tags[rnd.Next(_tags.Length)]).Distinct().ToArray(),
                Brand = new() { Id = brands[rnd.Next(brands.Count)].Id },
            });
            if (batch.Count == 5000) { store.Insert(batch); batch.Clear(); if (i % 100_000 < 5000) log($"  seeded {i + 1:n0} in {sw.Elapsed.TotalSeconds:0}s"); }
        }
        if (batch.Count > 0) store.Insert(batch);
        log($"  seed complete: {ProductCount:n0} nodes in {sw.Elapsed.TotalSeconds:0}s");
    }

    // text indexing runs as background tasks after insert; search measurements are only
    // meaningful once the queue has drained (hit count stops growing):
    void waitForTextIndexing(NodeStore store) {
        var last = -1;
        var stableSince = DateTime.UtcNow;
        var start = DateTime.UtcNow;
        while ((DateTime.UtcNow - start).TotalMinutes < 30) {
            var count = store.Query<PerfProduct>().WhereSearch("waterproof").Count();
            if (count != last) { last = count; stableSince = DateTime.UtcNow; }
            else if (count > 0 && (DateTime.UtcNow - stableSince).TotalSeconds > 10) return;
            log($"  waiting for text indexing... 'waterproof' hits: {count}");
            Thread.Sleep(5000);
        }
        log("  WARNING: text indexing still not stable after 30 min");
    }

    static readonly object _lock = new();
    void log(string line) {
        lock (_lock) File.AppendAllText(_log, line + Environment.NewLine);
        Console.WriteLine(line);
    }
}
