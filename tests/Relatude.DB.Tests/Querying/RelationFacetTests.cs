using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Nodes;
using Relatude.DB.Query;

namespace Relatude.Querying;

#region relation facet test datamodel
[Node]
public class RelFacetProduct {
    [InternalIdProperty]
    public int Id { get; set; }
    [PublicIdProperty]
    public Guid PId { get; set; }
    [StringProperty(Indexed = true)]
    public string Category { get; set; } = "";
    [RelationProperty(Facet = true)]
    public RelFacetBrand? Brand { get; set; }
    public RelFacetSupplier? Supplier { get; set; } // relation WITHOUT the Facet flag (gate tests)
}
[Node]
public class RelFacetBrand {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(Indexed = true, DisplayName = true)]
    public string Name { get; set; } = "";
    [RelationProperty(Facet = true)]
    public IEnumerable<RelFacetProduct>? Products { get; set; } // reverse side, also facetable
}
[Node]
public class RelFacetSupplier {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(DisplayName = true)]
    public string Name { get; set; } = "";
    public IEnumerable<RelFacetProduct>? Supplies { get; set; }
}
#endregion

[TestClass]
public class RelationFacetTests {

    static readonly string[] _categories = ["Toys", "Games", "Tools"];

    // truth: product public id -> brand public id (null = no brand relation)
    static NodeStore openStore(out List<RelFacetBrand> brands, out List<RelFacetProduct> products, out Dictionary<Guid, Guid?> brandOf) {
        var dm = new Datamodel();
        dm.Add<RelFacetProduct>();
        dm.Add<RelFacetBrand>();
        dm.Add<RelFacetSupplier>();
        var store = new NodeStore(DataStoreLocal.Open(dm));
        brands = [
            new RelFacetBrand { Id = Guid.NewGuid(), Name = "Acme" },
            new RelFacetBrand { Id = Guid.NewGuid(), Name = "Globex" },
            new RelFacetBrand { Id = Guid.NewGuid(), Name = "Initech" },
        ];
        store.Insert(brands);
        products = new List<RelFacetProduct>();
        for (var i = 1; i <= 30; i++) {
            products.Add(new RelFacetProduct { PId = Guid.NewGuid(), Category = _categories[i % 3] });
        }
        store.Insert(products);
        brandOf = new Dictionary<Guid, Guid?>();
        for (var i = 1; i <= 30; i++) {
            var product = products[i - 1];
            if (i % 5 == 0) { // every 5th product has no brand
                brandOf[product.PId] = null;
            } else {
                var brand = brands[i % 3];
                store.AddRelation(product, p => p.Brand, brand);
                brandOf[product.PId] = brand.Id;
            }
        }
        return store;
    }

    static Facets FacetOf<T>(ResultSetFacets<T> res, string codeName)
        => res.Facets.First(f => f.CodeName == codeName);

    [TestMethod]
    public void RelationFacet_BucketsAreMappedNodesWithDisplayNames() {
        var store = openStore(out var brands, out var products, out var brandOf);
        var res = store.Query<RelFacetProduct>().Facets().AddValueFacet("Brand").Execute();
        var facet = FacetOf(res, "Brand");
        Assert.IsTrue(facet.Values.Count > 0);
        Assert.IsTrue(facet.Values.All(v => v.Value is RelFacetBrand), "Bucket values must be mapped node objects at the NodeStore layer");
        foreach (var brand in brands) {
            var fv = facet.Values.First(v => v.Value is RelFacetBrand b && b.Id == brand.Id);
            Assert.AreEqual(brand.Name, fv.DisplayName, "Relation buckets should show the related node's display name");
            Assert.AreEqual(brandOf.Values.Count(b => b == brand.Id), fv.Count, "Wrong count for " + brand.Name);
        }
        // deterministic bucket order: sorted by display name
        var names = facet.Values.Select(v => v.DisplayName).ToList();
        CollectionAssert.AreEqual(names.OrderBy(n => n, StringComparer.Ordinal).ToList(), names);
        // products without a brand are simply not counted (no missing bucket in v1):
        Assert.AreEqual(brandOf.Values.Count(b => b != null), facet.Values.Sum(v => v.Count));
        store.Dispose();
    }

    [TestMethod]
    public void RelationFacet_SelectionByGuidAndString_FiltersAndDrillsSideways() {
        var store = openStore(out var brands, out var products, out var brandOf);
        var res = store.Query<RelFacetProduct>().Facets()
            .AddValueFacet("Brand").AddValueFacet("Category")
            .SetFacetValue("Brand", brands[0].Id)
            .Execute();
        var expected = products.Where(p => brandOf[p.PId] == brands[0].Id).ToList();
        Assert.AreEqual(expected.Count, res.Count());
        var brandFacet = FacetOf(res, "Brand");
        Assert.IsTrue(brandFacet.Values.First(v => v.Value is RelFacetBrand b && b.Id == brands[0].Id).Selected, "Selection must match the node data bucket");
        // multi-select semantics: the selected facet's own counts are computed WITHOUT its selection...
        Assert.AreEqual(products.Count(p => brandOf[p.PId] == brands[1].Id),
            brandFacet.Values.First(v => v.Value is RelFacetBrand b && b.Id == brands[1].Id).Count);
        // ...while other facets' counts reflect the selection:
        var catFacet = FacetOf(res, "Category");
        foreach (var cat in _categories) {
            Assert.AreEqual(expected.Count(p => p.Category == cat),
                catFacet.Values.First(v => Equals(v.Value, cat)).Count, "Wrong drill-sideways count for " + cat);
        }
        // the same selection given as a guid string must coerce and match:
        var res2 = store.Query<RelFacetProduct>().Facets()
            .AddValueFacet("Brand")
            .SetFacetValue("Brand", brands[0].Id.ToString())
            .Execute();
        Assert.AreEqual(expected.Count, res2.Count());
        // multi-select is a union:
        var res3 = store.Query<RelFacetProduct>().Facets()
            .AddValueFacet("Brand")
            .SetFacetValue("Brand", brands[0].Id).SetFacetValue("Brand", brands[1].Id)
            .Execute();
        Assert.AreEqual(products.Count(p => brandOf[p.PId] == brands[0].Id || brandOf[p.PId] == brands[1].Id), res3.Count());
        store.Dispose();
    }

    [TestMethod]
    public void RelationFacet_ReverseSide_BucketsArePerRelatedProduct() {
        var store = openStore(out var brands, out var products, out var brandOf);
        var res = store.Query<RelFacetBrand>().Facets().AddValueFacet("Products").Execute();
        var facet = FacetOf(res, "Products");
        Assert.AreEqual(brandOf.Values.Count(b => b != null), facet.Values.Count, "One bucket per product with a brand");
        Assert.IsTrue(facet.Values.All(v => v.Value is RelFacetProduct && v.Count == 1), "Each product belongs to exactly one brand");
        // selecting a product filters brands to the one it belongs to:
        var withBrand = products.First(p => brandOf[p.PId] != null);
        var res2 = store.Query<RelFacetBrand>().Facets()
            .AddValueFacet("Products")
            .SetFacetValue("Products", withBrand.PId)
            .Execute();
        Assert.AreEqual(1, res2.Count());
        Assert.AreEqual(brandOf[withBrand.PId], res2.First().Id);
        store.Dispose();
    }

    [TestMethod]
    public void RelationFacet_MutationsAndDeletes_UpdateBucketsAndCounts() {
        var store = openStore(out var brands, out var products, out var brandOf);
        // move a product from brand 0 to brand 1:
        var moved = products.First(p => brandOf[p.PId] == brands[0].Id);
        store.RemoveRelation(moved, p => p.Brand, brands[0]);
        store.AddRelation(moved, p => p.Brand, brands[1]);
        brandOf[moved.PId] = brands[1].Id;
        var res = store.Query<RelFacetProduct>().Facets().AddValueFacet("Brand").Execute();
        var facet = FacetOf(res, "Brand");
        foreach (var brand in brands) {
            Assert.AreEqual(brandOf.Values.Count(b => b == brand.Id),
                facet.Values.First(v => v.Value is RelFacetBrand b && b.Id == brand.Id).Count, "Wrong count for " + brand.Name + " after move");
        }
        // deleting a brand unwinds its relations: the bucket disappears (no stale buckets, unlike references)
        store.Delete(brands[2].Id);
        foreach (var key in brandOf.Where(kv => kv.Value == brands[2].Id).Select(kv => kv.Key).ToList()) brandOf[key] = null;
        var res2 = store.Query<RelFacetProduct>().Facets().AddValueFacet("Brand").Execute();
        var facet2 = FacetOf(res2, "Brand");
        Assert.IsFalse(facet2.Values.Any(v => v.Value is RelFacetBrand b && b.Id == brands[2].Id), "Deleted brand must not appear as a bucket");
        foreach (var brand in brands.Take(2)) {
            Assert.AreEqual(brandOf.Values.Count(b => b == brand.Id),
                facet2.Values.First(v => v.Value is RelFacetBrand b && b.Id == brand.Id).Count, "Wrong count for " + brand.Name + " after delete");
        }
        store.Dispose();
    }

    [TestMethod]
    public void RelationFacet_OptInGate_UnflaggedRelationsAreIgnored() {
        var store = openStore(out _, out _, out _);
        // explicitly added but not flagged: silently dropped (existing CanBeFacet contract)
        var res = store.Query<RelFacetProduct>().Facets().AddValueFacet("Supplier").Execute();
        Assert.IsFalse(res.Facets.Any(f => f.CodeName == "Supplier"), "Unflagged relation property must not produce a facet");
        // bare .Facets(): flagged relation appears among auto facets, unflagged does not
        var res2 = store.Query<RelFacetProduct>().Facets().Execute();
        Assert.IsTrue(res2.Facets.Any(f => f.CodeName == "Brand"), "Flagged relation property must appear in auto facets");
        Assert.IsTrue(res2.Facets.Any(f => f.CodeName == "Category"));
        Assert.IsFalse(res2.Facets.Any(f => f.CodeName == "Supplier"));
        store.Dispose();
    }

    [TestMethod]
    public void RelationFacet_UnparsableSelection_MatchesNothing() {
        // an unparsable selection must filter to an empty result, not silently drop the filter
        var store = openStore(out _, out _, out _);
        var res = store.Query<RelFacetProduct>().Facets()
            .AddValueFacet("Brand")
            .SetFacetValue("Brand", "not-a-guid")
            .Execute();
        Assert.AreEqual(0, res.Count());
        store.Dispose();
    }

    [TestMethod]
    public void RelationFacet_UnpublishedRelatedNode_IsSkippedNotThrown() {
        var store = openStore(out var brands, out var products, out var brandOf);
        // a related node with no published revision must not fail the facet query - it is simply not a bucket
        var draft = new RelFacetBrand { Id = Guid.NewGuid(), Name = "Draft" };
        store.Insert(draft, revisionType: RevisionType.Preliminary);
        var orphan = products.First(p => brandOf[p.PId] == null); // a product without a brand
        store.AddRelation(orphan, p => p.Brand, draft);
        var res = store.Query<RelFacetProduct>().Facets().AddValueFacet("Brand").Execute(); // must not throw
        var facet = FacetOf(res, "Brand");
        Assert.IsFalse(facet.Values.Any(v => v.Value is RelFacetBrand b && b.Id == draft.Id), "Unpublished related node must not appear as a bucket");
        foreach (var brand in brands) {
            Assert.AreEqual(brandOf.Values.Count(x => x == brand.Id),
                facet.Values.First(v => v.Value is RelFacetBrand b && b.Id == brand.Id).Count, "Published buckets must be unaffected");
        }
        store.Dispose();
    }

    [TestMethod]
    public void RelationFacet_JsonPath_ReturnsMappedBucketsAndFilters() {
        var store = openStore(out var brands, out _, out var brandOf);
        var json = store.Query<RelFacetProduct>().Facets()
            .AddValueFacet("Brand")
            .SetFacetValue("Brand", brands[0].Id)
            .EvaluateForJson();
        var res = json as ResultSetFacetsNotEnumerable<object?>;
        Assert.IsNotNull(res, "The JSON path must return a facet result set");
        var facet = res.Facets.First(f => f.CodeName == "Brand");
        Assert.IsTrue(facet.Values.All(v => v.Value is RelFacetBrand), "Relation buckets must be mapped node objects on the JSON path too");
        Assert.AreEqual(brandOf.Values.Count(b => b == brands[0].Id), res.TotalCount);
        store.Dispose();
    }

    [TestMethod]
    public void RelationFacet_EmptyCases() {
        var store = openStore(out var brands, out _, out var brandOf);
        // a brand with no relations is not a bucket:
        var lonely = new RelFacetBrand { Id = Guid.NewGuid(), Name = "Lonely" };
        store.Insert(lonely);
        var res = store.Query<RelFacetProduct>().Facets().AddValueFacet("Brand").Execute();
        Assert.IsFalse(FacetOf(res, "Brand").Values.Any(v => v.Value is RelFacetBrand b && b.Id == lonely.Id));
        // empty base set: buckets remain, all counts zero
        var res2 = store.Query<RelFacetProduct>().Where(p => p.Category == "NoSuchCategory").Facets().AddValueFacet("Brand").Execute();
        Assert.AreEqual(0, res2.SourceCount);
        Assert.IsTrue(FacetOf(res2, "Brand").Values.All(v => v.Count == 0));
        // selecting a node that exists but has no relations yields an empty result:
        var res3 = store.Query<RelFacetProduct>().Facets()
            .AddValueFacet("Brand")
            .SetFacetValue("Brand", lonely.Id)
            .Execute();
        Assert.AreEqual(0, res3.Count());
        store.Dispose();
    }
}
