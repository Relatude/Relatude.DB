using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Indexes.KvStore;
using Relatude.DB.Nodes;

namespace Relatude.Persistence;

[Node]
public class ScProduct {
    [InternalIdProperty] public int Id { get; set; }
    [StringProperty(Indexed = true)] public string Category { get; set; } = "";
    [DoubleProperty(Indexed = true)] public double Price { get; set; }
    [BooleanProperty(Indexed = true)] public bool Active { get; set; }
}

// The facet sets sidecar persists the per-value id set caches of the native KV value indexes
// across clean shutdowns (FacetSetsFile). These tests verify counts stay correct against a LINQ
// ground truth through: populate -> dispose -> reopen (sidecar load), updates on the loaded cache
// (per-value eviction), a second save/load cycle, and a deleted sidecar (crash fallback).
[TestClass]
public class FacetSetsSidecarTests {

    static readonly string[] _cats = ["Toys", "Games", "Tools", "Food"];

    static NodeStore open(string dir) {
        var dm = new Datamodel();
        dm.Add<ScProduct>();
        var settings = new SettingsLocal {
            UsePersistedValueIndexesByDefault = true,
            PersistedValueIndexEngine = PersistedValueIndexEngine.Native,
            UsePersistedTextIndexesByDefault = false,
        };
        return new NodeStore(DataStoreLocal.Open(dm, settings, new DB.IO.IOProviderDisk(dir), null, null, null, null, () => new NativeKvIndexStore(dir, null)));
    }

    // a facet query WITH a selection: counting the other facets runs against the filtered set,
    // which builds and caches per-value id sets (the sidecar's content)
    static void verify(NodeStore store, List<ScProduct> truth, string selectedCategory) {
        var res = store.Query<ScProduct>().Facets()
            .AddValueFacet("Category").AddValueFacet("Active")
            .SetFacetValue("Category", selectedCategory)
            .Execute();
        var expected = truth.Where(p => p.Category == selectedCategory).ToList();
        Assert.AreEqual(expected.Count, res.Count());
        var catFacet = res.Facets.First(f => f.CodeName == "Category");
        foreach (var g in truth.GroupBy(p => p.Category)) // own selection excluded: counts vs full set
            Assert.AreEqual(g.Count(), catFacet.Values.First(v => Equals(v.Value, g.Key)).Count, "Category " + g.Key);
        var activeFacet = res.Facets.First(f => f.CodeName == "Active");
        Assert.AreEqual(expected.Count(p => p.Active), activeFacet.Values.First(v => Equals(v.Value, true)).Count, "Active");
    }

    [TestMethod]
    public void FacetSetsSidecar_ReopenUpdatesAndCrashFallback_CountsStayCorrect() {
        var dir = Path.Combine(Path.GetTempPath(), "relatude-facetsets-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var truth = new List<ScProduct>();
            var store = open(dir);
            for (var i = 0; i < 500; i++) truth.Add(new ScProduct { Category = _cats[i % 4], Price = 10 + i % 40, Active = i % 3 == 0 });
            store.Insert(truth);
            truth = store.Query<ScProduct>().Execute().ToList(); // re-read for ids
            verify(store, truth, "Toys"); // populates the per-value caches
            store.Dispose(); // writes the sidecar

            var sidecar = Path.Combine(dir, "nativekv", "facetsets.bin");
            Assert.IsTrue(File.Exists(sidecar), "sidecar not written on dispose");

            store = open(dir); // loads the sidecar (timestamps match)
            verify(store, truth, "Toys");
            verify(store, truth, "Games");
            // update on top of the loaded cache: per-value eviction must keep counts exact
            var toy = truth.First(p => p.Category == "Toys");
            store.UpdateProperty<ScProduct, string>(toy.Id, p => p.Category, "Food");
            toy.Category = "Food";
            verify(store, truth, "Toys");
            verify(store, truth, "Food");
            store.Dispose(); // rewrites the sidecar with the new timestamp

            store = open(dir);
            verify(store, truth, "Food");
            store.Dispose();

            File.Delete(sidecar); // crash simulation: no sidecar, cold caches
            store = open(dir);
            verify(store, truth, "Toys");
            verify(store, truth, "Food");
            store.Dispose();
        } finally {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
