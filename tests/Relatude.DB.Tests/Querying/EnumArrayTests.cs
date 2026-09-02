using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Nodes;
using Relatude.DB.Query;
using Relatude.Utils;

namespace Relatude.Querying;

#region enum array test datamodel
[Node]
public class EnumArrayProduct {
    [InternalIdProperty]
    public int Id { get; set; }
    [StringProperty(Indexed = true)]
    public string Category { get; set; } = "";
    [EnumArrayProperty(Indexed = true)]
    public Sizes[] Options { get; set; } = [];
}
#endregion

[TestClass]
public class EnumArrayTests {

    static readonly string[] _categories = ["Toys", "Games", "Tools"];

    // every 5th product holds an in-array duplicate; evens a single; odds a pair
    static Sizes[] optionsFor(int i) =>
        i % 5 == 0 ? [Sizes.Small, Sizes.Small, Sizes.Large]
        : (i % 2 == 0 ? [Sizes.Small] : [Sizes.Medium, Sizes.Large]);

    static NodeStore OpenStore(out List<EnumArrayProduct> products, bool persistedIndexes = false) {
        var dm = new Datamodel();
        dm.Add<EnumArrayProduct>();
        var store = persistedIndexes
            ? new NodeStore(DataStoreLocal.Open(dm, new SettingsLocal() {
                ValueIndexes = [TestEngines.NativeValue], DefaultValueIndex = TestEngines.ValueId,
            }, null, null, null, null, null, () => DB.DataStores.Indexes.IndexEngines.Single(TestEngines.ValueId, new DB.DataStores.Indexes.KvStore.NativeKvIndexStore(null))))
            : new NodeStore(DataStoreLocal.Open(dm));
        products = new List<EnumArrayProduct>();
        for (var i = 1; i <= 30; i++) {
            products.Add(new EnumArrayProduct { Category = _categories[i % 3], Options = optionsFor(i) });
        }
        store.Insert(products);
        products = store.Query<EnumArrayProduct>().ToList();
        return store;
    }

    static Facets FacetOf<T>(ResultSetFacets<T> res, string codeName)
        => res.Facets.First(f => f.CodeName == codeName);

    [TestMethod]
    public void MapperRoundTrip_PreservesOrderAndDuplicates() {
        var store = OpenStore(out var stored);
        foreach (var p in stored) {
            Assert.IsTrue(p.Options.Length > 0);
        }
        var dup = stored.First(p => p.Options.Length == 3);
        CollectionAssert.AreEqual(new[] { Sizes.Small, Sizes.Small, Sizes.Large }, dup.Options, "Options must round-trip exactly (order and duplicates)");
        store.Dispose();
    }

    [TestMethod]
    public void Facets_CountsAndEnumNameDisplay() {
        var store = OpenStore(out var all);
        var res = store.Query<EnumArrayProduct>().Facets().AddValueFacet("Options").Execute();
        var facet = FacetOf(res, "Options");
        foreach (var size in new[] { Sizes.Small, Sizes.Medium, Sizes.Large }) {
            var fv = facet.Values.First(v => Equals(v.Value, (int)size));
            Assert.AreEqual(size.ToString(), fv.DisplayName, "Buckets must show the enum name");
            Assert.AreEqual(all.Count(p => p.Options.Contains(size)), fv.Count, "Wrong count for " + size + " (duplicates in one node count once)");
        }
        store.Dispose();
    }

    [TestMethod]
    public void Selection_ByEnumIntAndName_AllFilter() {
        var store = OpenStore(out var all);
        var expected = all.Count(p => p.Options.Contains(Sizes.Large));
        // boxed enum (exercises the valueToString fix: enums used to serialize as their NAME):
        var res1 = store.Query<EnumArrayProduct>().Facets()
            .AddValueFacet("Options").SetFacetValue("Options", Sizes.Large).Execute();
        Assert.AreEqual(expected, res1.Count(), "Selection by boxed enum");
        Assert.IsTrue(FacetOf(res1, "Options").Values.First(v => Equals(v.Value, (int)Sizes.Large)).Selected);
        // raw int:
        var res2 = store.Query<EnumArrayProduct>().Facets()
            .AddValueFacet("Options").SetFacetValue("Options", (int)Sizes.Large).Execute();
        Assert.AreEqual(expected, res2.Count(), "Selection by int");
        // enum name string (resolved via the property's name map):
        var res3 = store.Query<EnumArrayProduct>().Facets()
            .AddValueFacet("Options").SetFacetValue("Options", "Large").Execute();
        Assert.AreEqual(expected, res3.Count(), "Selection by enum name string");
        // unparsable input matches nothing:
        var res4 = store.Query<EnumArrayProduct>().Facets()
            .AddValueFacet("Options").SetFacetValue("Options", "NoSuchSize").Execute();
        Assert.AreEqual(0, res4.Count(), "Unparsable selection must match nothing");
        store.Dispose();
    }

    [TestMethod]
    public void UpdatesAndDeletes_ReflectedInFacetCounts() {
        foreach (var persistedIndexes in new[] { false, true }) {
            var store = OpenStore(out var all, persistedIndexes);
            var victims = all.Take(10).ToList();
            Sizes[][] combos = [[Sizes.Large], [Sizes.Large, Sizes.Small], [], [Sizes.Medium, Sizes.Medium]];
            for (var i = 0; i < victims.Count; i++) {
                var combo = combos[i % combos.Length];
                store.UpdateProperty<EnumArrayProduct, Sizes[]>(victims[i].Id, x => x.Options, combo);
                victims[i].Options = combo;
            }
            store.Delete(victims[0].Id);
            var remaining = all.Where(p => p.Id != victims[0].Id).ToList();
            var res = store.Query<EnumArrayProduct>().Facets().AddValueFacet("Options").Execute();
            var facet = FacetOf(res, "Options");
            Assert.AreEqual(remaining.Count, res.SourceCount);
            foreach (var size in new[] { Sizes.Small, Sizes.Medium, Sizes.Large }) {
                var expected = remaining.Count(p => p.Options.Contains(size));
                var fv = facet.Values.FirstOrDefault(v => Equals(v.Value, (int)size));
                Assert.AreEqual(expected, fv?.Count ?? 0, "Wrong count for " + size + ", persistedIndexes: " + persistedIndexes);
            }
            store.Dispose();
        }
    }

    [TestMethod]
    public void ValuesAndFacets_SurviveRestart() {
        foreach (var persistedIndexes in new[] { false, true }) {
            var dir = Path.Combine(Path.GetTempPath(), "relatude-enumarray-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {
                var store = openStoreOnDisk(dir, persistedIndexes);
                var products = new List<EnumArrayProduct>();
                for (var i = 1; i <= 20; i++) {
                    products.Add(new EnumArrayProduct { Category = _categories[i % 3], Options = optionsFor(i) });
                }
                store.Insert(products);
                var truth = store.Query<EnumArrayProduct>().ToList();
                store.Dispose();

                store = openStoreOnDisk(dir, persistedIndexes);
                var reloaded = store.Query<EnumArrayProduct>().ToList();
                Assert.AreEqual(truth.Count, reloaded.Count, "persistedIndexes: " + persistedIndexes);
                foreach (var p in truth) {
                    CollectionAssert.AreEqual(p.Options, reloaded.First(r => r.Id == p.Id).Options, "Options must survive restart exactly, persistedIndexes: " + persistedIndexes);
                }
                var res = store.Query<EnumArrayProduct>().Facets()
                    .AddValueFacet("Options").SetFacetValue("Options", Sizes.Small).Execute();
                var expected = truth.Count(p => p.Options.Contains(Sizes.Small));
                Assert.AreEqual(expected, res.Count(), "persistedIndexes: " + persistedIndexes);
                Assert.AreEqual("Small", FacetOf(res, "Options").Values.First(v => Equals(v.Value, (int)Sizes.Small)).DisplayName, "Enum names must resolve after restart");
                store.Dispose();
            } finally {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }

    static NodeStore openStoreOnDisk(string dir, bool persistedIndexes) {
        var dm = new Datamodel();
        dm.Add<EnumArrayProduct>();
        if (persistedIndexes) {
            var settings = new SettingsLocal {
                ValueIndexes = [TestEngines.NativeValue], DefaultValueIndex = TestEngines.ValueId,
            };
            return new NodeStore(DataStoreLocal.Open(dm, settings, new Relatude.DB.IO.IOProviderDisk(dir), null, null, null, null,
                () => DB.DataStores.Indexes.IndexEngines.Single(TestEngines.ValueId, new DB.DataStores.Indexes.KvStore.NativeKvIndexStore(dir))));
        }
        return new NodeStore(DataStoreLocal.Open(dm, new SettingsLocal(), new Relatude.DB.IO.IOProviderDisk(dir), null, null, null, null, null));
    }
}
