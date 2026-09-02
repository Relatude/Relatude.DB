using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Nodes;
using Relatude.DB.Query;
using Relatude.Utils;

namespace Relatude.Querying;

#region references test datamodel
[Node]
public class RefProduct {
    [InternalIdProperty]
    public int Id { get; set; }
    [StringProperty(Indexed = true)]
    public string Name { get; set; } = "";
    [ReferencesProperty(Indexed = true)]
    public References<RefBrand> Brands { get; set; } = new();
}
[Node]
public class RefBrand {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(Indexed = true, DisplayName = true)]
    public string Name { get; set; } = "";
}
[Node]
public class RefOther { // a second node type, used to verify target-type validation
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty]
    public string Name { get; set; } = "";
}
#endregion

[TestClass]
public class ReferencesTests {

    static Datamodel buildDatamodel() {
        var dm = new Datamodel();
        dm.Add<RefProduct>();
        dm.Add<RefBrand>();
        dm.Add<RefOther>();
        return dm;
    }
    // Brands per product i: every 5th holds an in-node duplicate, evens a single, odds an ordered pair
    static Guid[] brandComboFor(int i, List<RefBrand> brands) =>
        i % 5 == 0 ? [brands[0].Id, brands[0].Id, brands[1].Id]
        : (i % 2 == 0 ? [brands[0].Id] : [brands[1].Id, brands[2].Id]);

    static NodeStore OpenRefStore(out List<RefBrand> brands, out List<RefProduct> products, bool persistedIndexes = false) {
        var dm = buildDatamodel();
        var store = persistedIndexes
            ? new NodeStore(DataStoreLocal.Open(dm, new SettingsLocal() {
                ValueIndexes = [TestEngines.NativeValue], DefaultValueIndex = TestEngines.ValueId,
            }, null, null, null, null, null, () => DB.DataStores.Indexes.IndexEngines.Single(TestEngines.ValueId, new DB.DataStores.Indexes.KvStore.NativeKvIndexStore(null))))
            : new NodeStore(DataStoreLocal.Open(dm));
        brands = [
            new RefBrand { Id = Guid.NewGuid(), Name = "Acme" },
            new RefBrand { Id = Guid.NewGuid(), Name = "Globex" },
            new RefBrand { Id = Guid.NewGuid(), Name = "Initech" },
        ];
        store.Insert(brands); // brands must exist before products reference them
        products = new List<RefProduct>();
        for (var i = 1; i <= 30; i++) {
            products.Add(new RefProduct {
                Name = "P" + i,
                Brands = new() { Ids = brandComboFor(i, brands) },
            });
        }
        store.Insert(products);
        products = store.Query<RefProduct>().ToList(); // re-read for ids (mapper round-trips Brands.Ids)
        return store;
    }

    static void assertThrows(Action action, string message) {
        try { action(); } catch { return; }
        Assert.Fail(message);
    }

    [TestMethod]
    public void MapperRoundTrip_PreservesOrderAndDuplicates() {
        var store = OpenRefStore(out var brands, out var stored);
        foreach (var p in stored) {
            var i = int.Parse(p.Name.Substring(1));
            CollectionAssert.AreEqual(brandComboFor(i, brands), p.Brands.Ids, "Brands.Ids must round-trip exactly (order and duplicates) for " + p.Name);
        }
        // not preloaded: enumerating the wrapper yields nothing, Get() lazy-loads in stored order
        var pair = stored.First(p => p.Brands.Ids.Length == 2);
        Assert.AreEqual(0, pair.Brands.ToList().Count, "Enumeration without Preload must be empty");
        CollectionAssert.AreEqual(new[] { "Globex", "Initech" }, pair.Brands.Get().Select(b => b.Name).ToArray(), "Get() must lazy-load targets in stored order");
        var dup = stored.First(p => p.Brands.Ids.Length == 3);
        CollectionAssert.AreEqual(new[] { "Acme", "Acme", "Globex" }, dup.Brands.Get().Select(b => b.Name).ToArray(), "Get() must include duplicates");
        store.Dispose();
    }

    [TestMethod]
    public void Facets_CountsDisplayNamesAndSelection() {
        var store = OpenRefStore(out var brands, out var all);
        var res = store.Query<RefProduct>().Facets()
            .AddValueFacet("Brands")
            .SetFacetValue("Brands", brands[0].Id)
            .Execute();
        var facet = res.Facets.First(f => f.CodeName == "Brands");
        foreach (var brand in brands) {
            var fv = facet.Values.First(v => Equals(v.Value, brand.Id));
            Assert.AreEqual(brand.Name, fv.DisplayName, "References buckets should show the referenced node's display name");
            Assert.AreEqual(all.Count(p => p.Brands.Ids.Contains(brand.Id)), fv.Count, "Wrong count for " + brand.Name + " (duplicates in one node count once)");
        }
        Assert.AreEqual(all.Count(p => p.Brands.Ids.Contains(brands[0].Id)), res.Count());
        // the same selection given as a string must coerce to the Guid buckets:
        var res2 = store.Query<RefProduct>().Facets()
            .AddValueFacet("Brands")
            .SetFacetValue("Brands", brands[1].Id.ToString())
            .Execute();
        Assert.AreEqual(all.Count(p => p.Brands.Ids.Contains(brands[1].Id)), res2.Count());
        Assert.IsTrue(res2.Facets.First(f => f.CodeName == "Brands").Values.First(v => Equals(v.Value, brands[1].Id)).Selected);
        store.Dispose();
    }

    [TestMethod]
    public void Validation_RejectsUnknownAndWrongTypeTargets_ToleratesEmptyElements() {
        var store = OpenRefStore(out var brands, out _);
        assertThrows(() => store.Insert(new RefProduct { Name = "bad1", Brands = new() { Ids = [Guid.NewGuid()] } }),
            "Referencing a non-existent node must throw");
        var other = new RefOther { Id = Guid.NewGuid(), Name = "not a brand" };
        store.Insert(other);
        assertThrows(() => store.Insert(new RefProduct { Name = "bad2", Brands = new() { Ids = [brands[0].Id, other.Id] } }),
            "Referencing a node of the wrong type must throw, even when other elements are valid");
        // Guid.Empty elements are tolerated, like the scalar reference's unset value:
        store.Insert(new RefProduct { Name = "emptyElem", Brands = new() { Ids = [Guid.Empty, brands[0].Id] } });
        var p = store.Query<RefProduct>().Where(x => x.Name == "emptyElem").First();
        CollectionAssert.AreEqual(new[] { Guid.Empty, brands[0].Id }, p.Brands.Ids);
        // an unparsable facet selection must match nothing - in particular NOT the legitimate
        // Guid.Empty bucket the node above just created:
        var res = store.Query<RefProduct>().Facets()
            .AddValueFacet("Brands")
            .SetFacetValue("Brands", "not-a-guid")
            .Execute();
        Assert.AreEqual(0, res.Count());
        store.Dispose();
    }

    [TestMethod]
    public void ReferencesWrapper_DoesNotAliasStoreCache() {
        var store = OpenRefStore(out var brands, out var stored);
        // read side: mutating the wrapper's array in place must not write into the shared node cache
        var p = stored.First(x => x.Brands.Ids.Length == 2);
        var original = p.Brands.Ids.ToArray();
        p.Brands.Ids[0] = Guid.NewGuid(); // in-place mutation, no update call
        var reloaded = store.Query<RefProduct>().ToList().First(x => x.Id == p.Id);
        CollectionAssert.AreEqual(original, reloaded.Brands.Ids, "In-place wrapper mutation must not corrupt the store cache");
        // write side: mutating the caller's array after insert must not affect the stored value
        var arr = new[] { brands[0].Id };
        store.Insert(new RefProduct { Name = "alias", Brands = new() { Ids = arr } });
        arr[0] = Guid.NewGuid();
        var reloaded2 = store.Query<RefProduct>().ToList().First(x => x.Name == "alias");
        CollectionAssert.AreEqual(new[] { brands[0].Id }, reloaded2.Brands.Ids, "The stored value must not alias the caller's array");
        store.Dispose();
    }

    [TestMethod]
    public void LazyGet_And_Facets_SkipOrFlagDeletedTargets() {
        var store = OpenRefStore(out var brands, out var all);
        store.Delete(brands[2].Id); // Initech: referenced by every odd product, now stale
        var pair = store.Query<RefProduct>().ToList().First(p => p.Brands.Ids.Length == 2);
        CollectionAssert.AreEqual(new[] { brands[1].Id, brands[2].Id }, pair.Brands.Ids, "Raw ids keep the stale entry");
        CollectionAssert.AreEqual(new[] { "Globex" }, pair.Brands.Get().Select(b => b.Name).ToArray(), "Get() must skip the deleted target");
        var res = store.Query<RefProduct>().Facets().AddValueFacet("Brands").Execute();
        var facet = res.Facets.First(f => f.CodeName == "Brands");
        var staleBucket = facet.Values.First(v => Equals(v.Value, brands[2].Id));
        Assert.AreEqual(brands[2].Id.ToString(), staleBucket.DisplayName, "Stale bucket must fall back to the raw guid as display name");
        Assert.AreEqual(all.Count(p => p.Brands.Ids.Contains(brands[2].Id)), staleBucket.Count, "Stale bucket still counts the referring nodes");
        store.Dispose();
    }

    [TestMethod]
    public void Preload_YieldsTargetsInOrder_HonorsTop_SkipsDeleted() {
        var store = OpenRefStore(out var brands, out _);
        var nameById = brands.ToDictionary(b => b.Id, b => b.Name);
        var res = store.Query<RefProduct>().Preload(p => p.Brands).Execute();
        foreach (var p in res) {
            var expected = p.Brands.Ids.Select(id => nameById[id]).ToArray();
            CollectionAssert.AreEqual(expected, p.Brands.Select(b => b.Name).ToArray(), "Preloaded enumeration must match stored order and duplicates for " + p.Name);
        }
        var res2 = store.Query<RefProduct>().Preload(p => p.Brands, 1).Execute();
        foreach (var p in res2) {
            Assert.IsTrue(p.Brands.ToList().Count <= 1, "top must cap the preloaded targets");
        }
        store.Delete(brands[2].Id); // stale entries must be skipped, not truncate the rest
        var res3 = store.Query<RefProduct>().Preload(p => p.Brands).Execute();
        foreach (var p in res3.Where(p => p.Brands.Ids.Contains(brands[2].Id))) {
            var expected = p.Brands.Ids.Where(id => id != brands[2].Id).Select(id => nameById[id]).ToArray();
            CollectionAssert.AreEqual(expected, p.Brands.Select(b => b.Name).ToArray(), "Preload must skip deleted targets for " + p.Name);
        }
        store.Dispose();
    }

    [TestMethod]
    public void UpdatesAndDeletes_ReflectedInValuesAndFacets() {
        foreach (var persistedIndexes in new[] { false, true }) {
            var store = OpenRefStore(out var brands, out var all, persistedIndexes);
            var victims = all.Take(10).ToList();
            Guid[][] combos = [[brands[2].Id], [brands[2].Id, brands[0].Id], [], [brands[0].Id, brands[2].Id]];
            for (var i = 0; i < victims.Count; i++) {
                var combo = combos[i % combos.Length];
                store.UpdateProperty<RefProduct, object>(victims[i].Id, x => x.Brands, combo);
                victims[i].Brands = new() { Ids = combo };
            }
            store.Delete(victims[0].Id);
            var remaining = all.Where(p => p.Id != victims[0].Id).ToList();
            var reloaded = store.Query<RefProduct>().ToList();
            foreach (var p in remaining) {
                CollectionAssert.AreEqual(p.Brands.Ids, reloaded.First(r => r.Id == p.Id).Brands.Ids, "Updated ids must round-trip, persistedIndexes: " + persistedIndexes);
            }
            var res = store.Query<RefProduct>().Facets().AddValueFacet("Brands").Execute();
            var facet = res.Facets.First(f => f.CodeName == "Brands");
            Assert.AreEqual(remaining.Count, res.SourceCount);
            foreach (var brand in brands) {
                var expected = remaining.Count(p => p.Brands.Ids.Contains(brand.Id));
                var fv = facet.Values.FirstOrDefault(v => Equals(v.Value, brand.Id));
                Assert.AreEqual(expected, fv?.Count ?? 0, "Wrong count for " + brand.Name + ", persistedIndexes: " + persistedIndexes);
            }
            store.Dispose();
        }
    }

    [TestMethod]
    public void References_ValuesAndFacets_SurviveRestart() {
        foreach (var persistedIndexes in new[] { false, true }) {
            var dir = Path.Combine(Path.GetTempPath(), "relatude-references-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {
                var store = openRefStoreOnDisk(dir, persistedIndexes);
                var brands = new List<RefBrand> {
                    new() { Id = Guid.NewGuid(), Name = "Acme" },
                    new() { Id = Guid.NewGuid(), Name = "Globex" },
                    new() { Id = Guid.NewGuid(), Name = "Initech" },
                };
                store.Insert(brands);
                var products = new List<RefProduct>();
                for (var i = 1; i <= 20; i++) {
                    products.Add(new RefProduct { Name = "P" + i, Brands = new() { Ids = brandComboFor(i, brands) } });
                }
                store.Insert(products);
                var truth = store.Query<RefProduct>().ToList();
                store.Dispose();

                store = openRefStoreOnDisk(dir, persistedIndexes);
                var reloaded = store.Query<RefProduct>().ToList();
                Assert.AreEqual(truth.Count, reloaded.Count, "persistedIndexes: " + persistedIndexes);
                foreach (var p in truth) {
                    CollectionAssert.AreEqual(p.Brands.Ids, reloaded.First(r => r.Id == p.Id).Brands.Ids, "Brands.Ids must survive restart exactly, persistedIndexes: " + persistedIndexes);
                }
                var res = store.Query<RefProduct>().Facets()
                    .AddValueFacet("Brands")
                    .SetFacetValue("Brands", brands[0].Id)
                    .Execute();
                var facet = res.Facets.First(f => f.CodeName == "Brands");
                var expected = truth.Count(p => p.Brands.Ids.Contains(brands[0].Id));
                Assert.AreEqual(expected, facet.Values.First(v => Equals(v.Value, brands[0].Id)).Count, "persistedIndexes: " + persistedIndexes);
                Assert.AreEqual("Acme", facet.Values.First(v => Equals(v.Value, brands[0].Id)).DisplayName, "Display names must resolve after restart");
                Assert.AreEqual(expected, res.Count(), "persistedIndexes: " + persistedIndexes);
                store.Dispose();
            } finally {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }

    static NodeStore openRefStoreOnDisk(string dir, bool persistedIndexes) {
        var dm = buildDatamodel();
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
