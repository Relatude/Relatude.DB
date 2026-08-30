using System.Globalization;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Nodes;
using Relatude.DB.Query;
using Relatude.Utils;
using static Relatude.Querying.QueryTestHelpers;

namespace Relatude.Querying;

#region facet test datamodel
[Node]
public class Product {
    [InternalIdProperty]
    public int Id { get; set; }
    [StringProperty(Indexed = true)]
    public string Category { get; set; } = "";
    [DoubleProperty(Indexed = true)]
    public double Price { get; set; }
    [IntegerProperty(Indexed = true)]
    public int Stock { get; set; }
    [DateTimeProperty(Indexed = true)]
    public DateTime Released { get; set; }
    [StringArrayProperty(Indexed = true)]
    public string[] Tags { get; set; } = [];
    [GuidArrayProperty(Indexed = true)]
    public Guid[] TagIds { get; set; } = [];
    [BooleanProperty(Indexed = true)]
    public bool Active { get; set; }
    [ReferenceProperty(Indexed = true)]
    public Reference<Brand> Brand { get; set; } = new();
}
[Node]
public class Book : Product {
    [IntegerProperty(Indexed = true)]
    public int Pages { get; set; }
}
[Node]
public class Brand {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(Indexed = true, DisplayName = true)]
    public string Name { get; set; } = "";
}
#endregion

[TestClass]
public class FacetTests {

    static readonly string[] _categories = ["Toys", "Games", "Tools", "Food"];

    // fixed guids so failures are reproducible; seeded parallel to Tags (red/blue/green/paper)
    static readonly Guid _tagRed = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly Guid _tagBlue = Guid.Parse("22222222-2222-2222-2222-222222222222");
    static readonly Guid _tagGreen = Guid.Parse("33333333-3333-3333-3333-333333333333");
    static readonly Guid _tagPaper = Guid.Parse("44444444-4444-4444-4444-444444444444");

    static NodeStore OpenProductStore(out List<Product> all, out List<Brand> brands, bool persistedIndexes = false) {
        var dm = new Datamodel();
        dm.Add<Product>();
        dm.Add<Book>();
        dm.Add<Brand>();
        var store = persistedIndexes
            ? new NodeStore(DataStoreLocal.Open(dm, new SettingsLocal() {
                UsePersistedValueIndexesByDefault = true,
                PersistedValueIndexEngine = PersistedValueIndexEngine.Native,
            }, null, null, null, null, null, () => new DB.DataStores.Indexes.IndexEngines(new DB.DataStores.Indexes.KvStore.NativeKvIndexStore(null))))
            : new NodeStore(DataStoreLocal.Open(dm));
        brands = [
            new Brand { Id = Guid.NewGuid(), Name = "Acme" },
            new Brand { Id = Guid.NewGuid(), Name = "Globex" },
            new Brand { Id = Guid.NewGuid(), Name = "Initech" },
        ];
        store.Insert(brands); // brands must exist before products reference them
        all = new List<Product>();
        for (var i = 1; i <= 60; i++) {
            all.Add(new Product {
                Category = _categories[i % 4],
                Price = i * 1.5, // 60 distinct values, enough to trigger automatic range buckets
                Stock = i % 7,
                Released = new DateTime(2020, 1, 1).AddDays(i * 11),
                Tags = i % 5 == 0 ? ["red", "red", "blue"] : (i % 2 == 0 ? ["red"] : ["green"]),
                TagIds = i % 5 == 0 ? [_tagRed, _tagRed, _tagBlue] : (i % 2 == 0 ? [_tagRed] : [_tagGreen]),
                Active = i % 3 == 0,
                Brand = new() { Id = brands[i % 3].Id },
            });
        }
        for (var i = 61; i <= 70; i++) {
            all.Add(new Book {
                Category = "Books",
                Price = i * 1.5,
                Stock = i % 7,
                Released = new DateTime(2020, 1, 1).AddDays(i * 11),
                Tags = ["paper"],
                TagIds = [_tagPaper],
                Active = i % 3 == 0,
                Brand = new() { Id = brands[i % 3].Id },
                Pages = 100 + (i % 3),
            });
        }
        store.Insert(all);
        return store;
    }

    static Facets FacetOf<T>(ResultSetFacets<T> res, string codeName)
        => res.Facets.First(f => f.CodeName == codeName);

    [TestMethod]
    public void ValueFacet_CountsAndUnpagedResult() {
        var store = OpenProductStore(out var all, out _);
        var res = store.Query<Product>().Facets().AddValueFacet("Category").Execute();
        var facet = FacetOf(res, "Category");
        Assert.IsFalse(facet.IsRangeFacet == true);
        foreach (var g in all.GroupBy(p => p.Category)) {
            var fv = facet.Values.FirstOrDefault(v => Equals(v.Value, g.Key));
            Assert.IsNotNull(fv, "Missing bucket for " + g.Key);
            Assert.AreEqual(g.Count(), fv.Count, "Wrong count for " + g.Key);
        }
        Assert.AreEqual(all.Count, res.Count()); // no .Page() given: the full result must be returned, not an empty page
        Assert.AreEqual(all.Count, res.SourceCount);
        store.Dispose();
    }

    [TestMethod]
    public void ValueFacet_SingleSelection_FiltersResultAndCountsAgainstOtherFacets() {
        var store = OpenProductStore(out var all, out _);
        var res = store.Query<Product>().Facets()
            .AddValueFacet("Category").AddValueFacet("Active")
            .SetFacetValue("Category", "Toys")
            .Execute();
        var expected = all.Where(p => p.Category == "Toys").ToList();
        Assert.AreEqual(expected.Count, res.Count());
        Assert.IsTrue(res.All(p => p.Category == "Toys"));
        // multi-select semantics: the selected facet's counts are computed WITHOUT its own selection...
        var catFacet = FacetOf(res, "Category");
        Assert.IsTrue(catFacet.Values.First(v => Equals(v.Value, "Toys")).Selected);
        Assert.AreEqual(all.Count(p => p.Category == "Games"), catFacet.Values.First(v => Equals(v.Value, "Games")).Count);
        // ...while other facets' counts reflect the selection:
        var activeFacet = FacetOf(res, "Active");
        Assert.AreEqual(expected.Count(p => p.Active), activeFacet.Values.First(v => Equals(v.Value, true)).Count);
        store.Dispose();
    }

    [TestMethod]
    public void ValueFacet_MultiSelectionIsUnion_AcrossFacetsIsIntersection() {
        var store = OpenProductStore(out var all, out _);
        var res = store.Query<Product>().Facets()
            .AddValueFacet("Category").AddValueFacet("Active")
            .SetFacetValue("Category", "Toys").SetFacetValue("Category", "Games")
            .SetFacetValue("Active", true)
            .Execute();
        var expected = all.Where(p => (p.Category == "Toys" || p.Category == "Games") && p.Active).ToList();
        Assert.AreEqual(expected.Count, res.Count());
        store.Dispose();
    }

    [TestMethod]
    public void Selection_MatchesTypedValuesFromStrings_UnderNorwegianCulture() {
        var culture = Thread.CurrentThread.CurrentCulture;
        try {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("nb-NO"); // "." must not be read as a thousands separator
            var store = OpenProductStore(out var all, out _);
            var res = store.Query<Product>().Facets()
                .AddValueFacet("Price")
                .SetFacetValue("Price", "4.5") // string selection against double buckets
                .Execute();
            Assert.AreEqual(all.Count(p => p.Price == 4.5), res.Count());
            var res2 = store.Query<Product>().Facets()
                .AddValueFacet("Stock")
                .SetFacetValue("Stock", "3") // string selection against int buckets
                .Execute();
            Assert.AreEqual(all.Count(p => p.Stock == 3), res2.Count());
            store.Dispose();
        } finally {
            Thread.CurrentThread.CurrentCulture = culture;
        }
    }

    [TestMethod]
    public void Selection_UnmatchedValueBecomesSelectedBucket_AndStillFilters() {
        var store = OpenProductStore(out var all, out _);
        var res = store.Query<Product>().Facets()
            .AddValueFacet("Category")
            .SetFacetValue("Category", "NoSuchCategory")
            .Execute();
        var facet = FacetOf(res, "Category");
        // the default buckets must survive, the unmatched selection is added with count 0, and the filter applies:
        Assert.IsTrue(facet.Values.Count >= _categories.Length + 1);
        var added = facet.Values.First(v => Equals(v.Value, "NoSuchCategory"));
        Assert.IsTrue(added.Selected);
        Assert.AreEqual(0, added.Count);
        Assert.AreEqual(0, res.Count());
        store.Dispose();
    }

    [TestMethod]
    public void RangeFacet_ExplicitRanges_CountAndFilter() {
        var store = OpenProductStore(out var all, out _);
        var res = store.Query<Product>().Facets()
            .AddRangeFacet("Stock", 0, 3).AddRangeFacet("Stock", 4, 6)
            .SetFacetRangeValue("Stock", 0, 3)
            .Execute();
        var facet = FacetOf(res, "Stock");
        Assert.AreEqual(true, facet.IsRangeFacet);
        Assert.AreEqual(all.Count(p => p.Stock >= 0 && p.Stock <= 3), facet.Values[0].Count);
        Assert.AreEqual(all.Count(p => p.Stock >= 4 && p.Stock <= 6), facet.Values[1].Count);
        Assert.AreEqual(all.Count(p => p.Stock >= 0 && p.Stock <= 3), res.Count());
        store.Dispose();
    }

    [TestMethod]
    public void RangeFacet_AutoGenerated_CoversAllValuesWithoutOverlap() {
        var store = OpenProductStore(out var all, out _);
        var res = store.Query<Product>().Facets().AddRangeFacet("Price").Execute();
        var facet = FacetOf(res, "Price");
        Assert.AreEqual(true, facet.IsRangeFacet);
        Assert.IsTrue(facet.Values.Count > 1);
        Assert.AreEqual(all.Count, facet.Values.Sum(v => v.Count), "Range buckets must cover every value exactly once");
        foreach (var fv in facet.Values) { // each bucket must agree with LINQ using its own bounds
            var from = (double)fv.Value!;
            var to = (double)fv.Value2!;
            var expected = all.Count(p => (fv.FromInclusive ? p.Price >= from : p.Price > from) && (fv.ToInclusive ? p.Price <= to : p.Price < to));
            Assert.AreEqual(expected, fv.Count, "Bucket " + fv.DisplayName);
        }
        store.Dispose();
    }

    [TestMethod]
    public void RangeFacet_HighCardinalityDefaultsToRanges_ExplicitValueFacetDoesNot() {
        var store = OpenProductStore(out _, out _);
        var auto = store.Query<Product>().Facets().AddFacet("Price").Execute();
        Assert.AreEqual(true, FacetOf(auto, "Price").IsRangeFacet, "60+ distinct values should auto-bucket");
        var forced = store.Query<Product>().Facets().AddValueFacet("Price").Execute();
        Assert.AreNotEqual(true, FacetOf(forced, "Price").IsRangeFacet, "AddValueFacet must force value buckets");
        store.Dispose();
    }

    [TestMethod]
    public void RangeFacet_SelectionOfGeneratedBucket_Filters() {
        var store = OpenProductStore(out var all, out _);
        var first = store.Query<Product>().Facets().AddRangeFacet("Released").Execute();
        var facet = FacetOf(first, "Released");
        Assert.AreEqual(true, facet.IsRangeFacet);
        var bucket = facet.Values.First(v => v.Count > 0);
        // select the same bucket in a second query (round-trips through the query string as text):
        var res = store.Query<Product>().Facets()
            .AddRangeFacet("Released")
            .SetFacetRangeValue("Released", bucket.Value!, bucket.Value2!)
            .Execute();
        var from = (DateTime)bucket.Value!;
        var to = (DateTime)bucket.Value2!;
        var expected = all.Count(p => (bucket.FromInclusive ? p.Released >= from : p.Released > from) && (bucket.ToInclusive ? p.Released <= to : p.Released < to));
        Assert.AreEqual(expected, res.Count());
        store.Dispose();
    }

    [TestMethod]
    public void RangeFacet_RangeCountOptionAndSingleRange() {
        var store = OpenProductStore(out var all, out _);
        var res = store.Query<Product>().Facets()
            .AddRangeFacet("Price").SetFacetOptions("Price", rangeCount: 4)
            .Execute();
        Assert.IsTrue(FacetOf(res, "Price").Values.Count <= 5, "rangeCount 4 should give at most ~4 buckets");
        var single = store.Query<Product>().Facets().AddSingleRangeFacet("Price").Execute();
        var facet = FacetOf(single, "Price");
        Assert.AreEqual(1, facet.Values.Count);
        Assert.AreEqual(all.Count, facet.Values[0].Count);
        store.Dispose();
    }

    [TestMethod]
    public void StringArrayFacet_CountsAndFilters_DuplicatesInOneNodeCountOnce() {
        var store = OpenProductStore(out var all, out _);
        var res = store.Query<Product>().Facets()
            .AddValueFacet("Tags")
            .SetFacetValue("Tags", "red")
            .Execute();
        var facet = FacetOf(res, "Tags");
        var expectedRed = all.Count(p => p.Tags.Contains("red")); // nodes with ["red","red","blue"] count once
        Assert.AreEqual(expectedRed, facet.Values.First(v => Equals(v.Value, "red")).Count);
        Assert.AreEqual(expectedRed, res.Count());
        store.Dispose();
    }

    [TestMethod]
    public void MissingBucket_CountsAndSelectsNodesWithoutTheProperty() {
        var store = OpenProductStore(out var all, out _);
        var books = all.OfType<Book>().Count();
        var res = store.Query<Product>().Facets()
            .AddFacet<Book>("Pages").SetFacetOptions<Book>("Pages", includeMissing: true)
            .Execute();
        var facet = res.Facets.First(f => f.CodeName == "Pages");
        var missing = facet.Values.First(v => v.Value == null);
        Assert.AreEqual(all.Count - books, missing.Count, "Products that are not books have no Pages value");
        var res2 = store.Query<Product>().Facets()
            .AddFacet<Book>("Pages").SetFacetOptions<Book>("Pages", includeMissing: true)
            .SetFacetMissingValue<Book>("Pages")
            .Execute();
        Assert.AreEqual(all.Count - books, res2.Count());
        store.Dispose();
    }

    [TestMethod]
    public void Options_MinCountMaxValuesSortByCount_NeverDropSelected() {
        var store = OpenProductStore(out var all, out _);
        var res = store.Query<Product>().Facets()
            .AddValueFacet("Category").SetFacetOptions("Category", maxValues: 2, sortByCount: true)
            .SetFacetValue("Category", "Books") // the smallest bucket (10) would be trimmed without protection
            .Execute();
        var facet = FacetOf(res, "Category");
        Assert.AreEqual(2, facet.Values.Count);
        Assert.IsTrue(facet.Values.Any(v => Equals(v.Value, "Books") && v.Selected), "Selected value must survive MaxValues trimming");
        var res2 = store.Query<Product>().Facets()
            .AddValueFacet("Category").SetFacetOptions("Category", minCount: 12)
            .Execute();
        Assert.IsTrue(FacetOf(res2, "Category").Values.All(v => v.Count >= 12));
        store.Dispose();
    }

    [TestMethod]
    public void ReferenceFacet_BucketsByReferencedNodeWithDisplayNames() {
        var store = OpenProductStore(out var all, out var brands);
        var res = store.Query<Product>().Facets()
            .AddValueFacet("Brand")
            .SetFacetValue("Brand", brands[0].Id)
            .Execute();
        var facet = FacetOf(res, "Brand");
        Assert.AreEqual(brands.Count, facet.Values.Count(v => v.Count > 0 || v.Selected));
        foreach (var brand in brands) {
            var fv = facet.Values.First(v => Equals(v.Value, brand.Id));
            Assert.AreEqual(brand.Name, fv.DisplayName, "Reference buckets should show the referenced node's display name");
            Assert.AreEqual(all.Count(p => p.Brand.Id == brand.Id), fv.Count);
        }
        Assert.AreEqual(all.Count(p => p.Brand.Id == brands[0].Id), res.Count());
        store.Dispose();
    }

    [TestMethod]
    public void FacetsOnFilteredQuery_AndPaging() {
        var store = OpenProductStore(out var all, out _);
        var res = store.Query<Product>().Where(p => p.Active).Facets()
            .AddValueFacet("Category")
            .SetFacetValue("Category", "Toys")
            .Page(0, 3)
            .Execute();
        var baseSet = all.Where(p => p.Active).ToList();
        Assert.AreEqual(baseSet.Count, res.SourceCount);
        var expected = baseSet.Count(p => p.Category == "Toys");
        Assert.AreEqual(expected, res.TotalCount);
        Assert.AreEqual(Math.Min(3, expected), res.Count());
        var facet = FacetOf(res, "Category");
        Assert.AreEqual(expected, facet.Values.First(v => Equals(v.Value, "Toys")).Count);
        store.Dispose();
    }

    [TestMethod]
    public void FacetsWithNoExplicitProperties_ReturnsAllFacetableProperties() {
        var store = OpenProductStore(out _, out _);
        var res = store.Query<Product>().Facets().Execute();
        Assert.IsTrue(res.Facets.Any(f => f.CodeName == "Category"));
        Assert.IsTrue(res.Facets.Any(f => f.CodeName == "Active"));
        store.Dispose();
    }

    [TestMethod]
    public void Facets_OnArticleDatamodel() { // a different datamodel loaded into the definition
        var store = OpenStoreWithArticles(100); // persisted (native KV) value indexes
        var all = store.Query<Article>().ToList();
        var res = store.Query<Article>().Facets()
            .AddValueFacet("IntegerNum")
            .SetFacetValue("IntegerNum", 5)
            .Execute();
        Assert.AreEqual(all.Count(a => a.IntegerNum == 5), res.Count());
        var facet = res.Facets.First(f => f.CodeName == "IntegerNum");
        foreach (var g in all.GroupBy(a => a.IntegerNum)) {
            Assert.AreEqual(g.Count(), facet.Values.First(v => Equals(v.Value, g.Key)).Count);
        }
        // explicit ranges against the persisted index backend:
        var res2 = store.Query<Article>().Facets()
            .AddRangeFacet("IntegerNum", 0, 4)
            .SetFacetRangeValue("IntegerNum", 0, 4)
            .Execute();
        Assert.AreEqual(all.Count(a => a.IntegerNum >= 0 && a.IntegerNum <= 4), res2.Count());
        store.Dispose();
    }

    [TestMethod]
    public void ExpressionApi_MatchesStringNameApi() {
        var store = OpenProductStore(out var all, out _);
        var res = store.Query<Product>().Facets()
            .AddValueFacet(p => p.Category)
            .AddFacet(p => p.Active) // bool member boxes to object; the Convert node must be unwrapped
            .AddFacet<Book>(b => b.Pages)
            .SetFacetValue(p => p.Category, "Tools")
            .SetFacetOptions(p => p.Active, sortByCount: true)
            .Execute();
        Assert.AreEqual(all.Count(p => p.Category == "Tools"), res.Count());
        Assert.IsTrue(res.All(p => p.Category == "Tools"));
        var catFacet = FacetOf(res, "Category");
        Assert.IsTrue(catFacet.Values.First(v => Equals(v.Value, "Tools")).Selected);
        Assert.IsTrue(res.Facets.Any(f => f.CodeName == "Pages"), "Subtype expression overload must resolve Book.Pages");
        store.Dispose();
    }

    [TestMethod]
    public void BooleanFacet_TwoBuckets_SelectableByStringValue() {
        var store = OpenProductStore(out var all, out _);
        var res = store.Query<Product>().Facets()
            .AddValueFacet("Active")
            .SetFacetValue("Active", "true") // string selection against bool buckets
            .Execute();
        var facet = FacetOf(res, "Active");
        Assert.AreEqual(2, facet.Values.Count);
        Assert.AreEqual(all.Count(p => p.Active), facet.Values.First(v => Equals(v.Value, true)).Count);
        Assert.AreEqual(all.Count(p => !p.Active), facet.Values.First(v => Equals(v.Value, false)).Count);
        Assert.IsTrue(facet.Values.First(v => Equals(v.Value, true)).Selected);
        Assert.AreEqual(all.Count(p => p.Active), res.Count());
        store.Dispose();
    }

    [TestMethod]
    public void RangeFacet_MultiSelectionIsUnion() {
        var store = OpenProductStore(out var all, out _);
        var res = store.Query<Product>().Facets()
            .AddRangeFacet("Stock", 0, 1).AddRangeFacet("Stock", 3, 4).AddRangeFacet("Stock", 5, 6)
            .SetFacetRangeValue("Stock", 0, 1).SetFacetRangeValue("Stock", 5, 6)
            .Execute();
        var expected = all.Count(p => p.Stock is >= 0 and <= 1 or >= 5 and <= 6);
        Assert.AreEqual(expected, res.Count());
        var facet = FacetOf(res, "Stock");
        Assert.AreEqual(2, facet.Values.Count(v => v.Selected));
        store.Dispose();
    }

    [TestMethod]
    public void RangeFacet_CustomSelectedRange_AddsBucketAndFilters() {
        var store = OpenProductStore(out var all, out _);
        var res = store.Query<Product>().Facets()
            .AddRangeFacet("Price") // auto-generated buckets...
            .SetFacetRangeValue("Price", 10.3, 19.7) // ...plus a selection that matches none of them
            .Execute();
        var facet = FacetOf(res, "Price");
        var expected = all.Count(p => p.Price >= 10.3 && p.Price <= 19.7);
        Assert.AreEqual(expected, res.Count());
        var selected = facet.Values.Single(v => v.Selected);
        Assert.AreEqual(expected, selected.Count);
        Assert.IsTrue(facet.Values.Count(v => !v.Selected) > 1, "Generated buckets must survive a custom range selection");
        Assert.AreEqual(all.Count, facet.Values.Where(v => !v.Selected).Sum(v => v.Count), "Generated buckets still count the unfiltered base set");
        store.Dispose();
    }

    [TestMethod]
    public void MissingBucket_UnionWithValueSelection() {
        var store = OpenProductStore(out var all, out _);
        var res = store.Query<Product>().Facets()
            .AddFacet<Book>("Pages").SetFacetOptions<Book>("Pages", includeMissing: true)
            .SetFacetMissingValue<Book>("Pages")
            .SetFacetValue<Book>("Pages", 101)
            .Execute();
        var expected = all.Count(p => p is not Book) + all.OfType<Book>().Count(b => b.Pages == 101);
        Assert.AreEqual(expected, res.Count());
        var facet = FacetOf(res, "Pages");
        Assert.IsTrue(facet.Values.First(v => v.Value == null).Selected);
        Assert.IsTrue(facet.Values.First(v => Equals(v.Value, 101)).Selected);
        store.Dispose();
    }

    [TestMethod]
    public void Options_SortByCountOrdersDescending_DefaultSortsByValue() {
        var store = OpenProductStore(out var all, out _);
        var res = store.Query<Product>().Facets().AddValueFacet("Category").Execute();
        var byValue = FacetOf(res, "Category").Values.Select(v => (string)v.Value!).ToList();
        CollectionAssert.AreEqual(byValue.OrderBy(v => v, StringComparer.Ordinal).ToList(), byValue, "Default order is ascending by value");
        var res2 = store.Query<Product>().Facets()
            .AddValueFacet("Category").SetFacetOptions("Category", sortByCount: true)
            .Execute();
        var counts = FacetOf(res2, "Category").Values.Select(v => v.Count).ToList();
        CollectionAssert.AreEqual(counts.OrderByDescending(c => c).ToList(), counts, "sortByCount orders by descending count");
        Assert.AreEqual("Books", FacetOf(res2, "Category").Values.Last().Value, "Books is the smallest bucket and must come last");
        store.Dispose();
    }

    [TestMethod]
    public void Options_MinCountNeverDropsSelected() {
        var store = OpenProductStore(out var all, out _);
        var books = all.Count(p => p.Category == "Books");
        var res = store.Query<Product>().Facets()
            .AddValueFacet("Category").SetFacetOptions("Category", minCount: books + 1)
            .SetFacetValue("Category", "Books") // its own count is below minCount, but it is selected
            .Execute();
        var facet = FacetOf(res, "Category");
        var booksBucket = facet.Values.FirstOrDefault(v => Equals(v.Value, "Books"));
        Assert.IsNotNull(booksBucket, "Selected value must survive MinCount trimming");
        Assert.IsTrue(booksBucket.Selected);
        Assert.IsTrue(facet.Values.Where(v => !v.Selected).All(v => v.Count > books));
        Assert.AreEqual(books, res.Count());
        store.Dispose();
    }

    [TestMethod]
    public void Selection_WithoutAddingFacetFirst_StillFilters() {
        var store = OpenProductStore(out var all, out _);
        var res = store.Query<Product>().Facets()
            .SetFacetValue("Category", "Toys") // no AddFacet: all facetable properties are returned
            .Execute();
        Assert.AreEqual(all.Count(p => p.Category == "Toys"), res.Count());
        Assert.IsTrue(res.Facets.Any(f => f.CodeName == "Category"));
        Assert.IsTrue(res.Facets.Any(f => f.CodeName == "Active"));
        Assert.IsTrue(FacetOf(res, "Category").Values.First(v => Equals(v.Value, "Toys")).Selected);
        store.Dispose();
    }

    [TestMethod]
    public void Facets_OnEmptyBaseSet_AllCountsZero() {
        var store = OpenProductStore(out _, out _);
        var res = store.Query<Product>().Where(p => p.Price < 0).Facets()
            .AddValueFacet("Category").AddRangeFacet("Price")
            .Execute();
        Assert.AreEqual(0, res.SourceCount);
        Assert.AreEqual(0, res.Count());
        Assert.IsTrue(FacetOf(res, "Category").Values.All(v => v.Count == 0));
        Assert.IsTrue(FacetOf(res, "Price").Values.All(v => v.Count == 0));
        store.Dispose();
    }

    [TestMethod]
    public void SubtypeQuery_FacetsCountOnlyThatType() {
        var store = OpenProductStore(out var all, out _);
        var books = all.OfType<Book>().ToList();
        var res = store.Query<Book>().Facets().AddValueFacet("Pages").Execute();
        Assert.AreEqual(books.Count, res.SourceCount);
        var facet = FacetOf(res, "Pages");
        foreach (var g in books.GroupBy(b => b.Pages)) {
            Assert.AreEqual(g.Count(), facet.Values.First(v => Equals(v.Value, g.Key)).Count, "Wrong count for Pages = " + g.Key);
        }
        Assert.AreEqual(books.Count, facet.Values.Sum(v => v.Count));
        store.Dispose();
    }

    [TestMethod]
    public void SingleRangeFacet_SelectingWholeRange_ReturnsEverything() {
        var store = OpenProductStore(out var all, out _);
        var first = store.Query<Product>().Facets().AddSingleRangeFacet("Price").Execute();
        var bucket = FacetOf(first, "Price").Values.Single();
        var res = store.Query<Product>().Facets()
            .AddSingleRangeFacet("Price")
            .SetFacetRangeValue("Price", bucket.Value!, bucket.Value2!)
            .Execute();
        Assert.AreEqual(all.Count, res.Count());
        store.Dispose();
    }

    [TestMethod]
    public void UpdatesAndDeletes_AreReflectedInFacetCounts() {
        var store = OpenProductStore(out _, out _);
        var stored = store.Query<Product>().ToList();
        var toy = stored.First(p => p.Category == "Toys");
        var game = stored.First(p => p.Category == "Games");
        var green = stored.First(p => p.Id != game.Id && p.Tags.SequenceEqual(new[] { "green" }));
        store.UpdateProperty<Product, string>(toy.Id, p => p.Category, "Food");
        store.UpdateProperty<Product, string[]>(green.Id, p => p.Tags, ["yellow"]);
        store.Delete(game.Id);
        // mirror the changes locally and compare every bucket:
        toy.Category = "Food";
        green.Tags = ["yellow"];
        var remaining = stored.Where(p => p.Id != game.Id).ToList();
        var res = store.Query<Product>().Facets().AddValueFacet("Category").AddValueFacet("Tags").Execute();
        Assert.AreEqual(remaining.Count, res.SourceCount);
        var catFacet = FacetOf(res, "Category");
        foreach (var g in remaining.GroupBy(p => p.Category)) {
            Assert.AreEqual(g.Count(), catFacet.Values.First(v => Equals(v.Value, g.Key)).Count, "Wrong count for " + g.Key);
        }
        var tagFacet = FacetOf(res, "Tags");
        foreach (var g in remaining.SelectMany(p => p.Tags.Distinct()).GroupBy(t => t)) {
            Assert.AreEqual(g.Count(), tagFacet.Values.First(v => Equals(v.Value, g.Key)).Count, "Wrong count for tag " + g.Key);
        }
        Assert.AreEqual(1, tagFacet.Values.First(v => Equals(v.Value, "yellow")).Count);
        store.Dispose();
    }

    [TestMethod]
    public void UpdatesAndDeletes_PersistedBackend_AreReflectedInFacetCounts() {
        // persisted (native KV) value indexes serve per-value id sets from an in-memory cache;
        // updates and deletes must evict exactly the touched values so counts stay correct
        var store = OpenProductStore(out _, out _, persistedIndexes: true);
        var stored = store.Query<Product>().ToList();
        void verify() {
            var res = store.Query<Product>().Facets().AddValueFacet("Category").AddValueFacet("Active").Execute();
            var catFacet = FacetOf(res, "Category");
            foreach (var g in stored.GroupBy(p => p.Category))
                Assert.AreEqual(g.Count(), catFacet.Values.First(v => Equals(v.Value, g.Key)).Count, "Wrong count for " + g.Key);
            var activeFacet = FacetOf(res, "Active");
            Assert.AreEqual(stored.Count(p => p.Active), activeFacet.Values.First(v => Equals(v.Value, true)).Count);
        }
        verify(); // populates the per-value cache
        var toy = stored.First(p => p.Category == "Toys");
        store.UpdateProperty<Product, string>(toy.Id, p => p.Category, "Food");
        toy.Category = "Food";
        verify();
        var game = stored.First(p => p.Category == "Games");
        store.Delete(game.Id);
        stored.Remove(game);
        verify();
        store.Dispose();
    }

    [TestMethod]
    public void StringArrayIndex_InternedArrays_SurviveUpdateChurnAndDeletes() {
        // the string array index normalizes node arrays into a reference counted intern table;
        // this cycles nodes through shared/unique/empty combinations so interned arrays are
        // repeatedly created, released to zero, and their slots reused - counts and selections
        // must stay exactly in sync with a plain LINQ ground truth throughout
        var store = OpenProductStore(out _, out _);
        churnTagsAndVerify(store);
    }

    [TestMethod]
    public void StringArrayIndex_InternedArrays_PersistedBackend_SurviveUpdateChurnAndDeletes() {
        // same churn against the persisted (native KV) string array index, whose in-memory mirror
        // uses the same intern table
        var store = OpenProductStore(out _, out _, persistedIndexes: true);
        churnTagsAndVerify(store);
    }

    static void churnTagsAndVerify(NodeStore store) {
        var stored = store.Query<Product>().ToList();
        var victims = stored.Take(20).ToList();
        string[][] combos = [["x1"], ["x1", "y2"], [], ["y2", "x1"], ["z3"], ["x1", "y2"]];
        foreach (var combo in combos) {
            foreach (var p in victims) {
                store.UpdateProperty<Product, string[]>(p.Id, x => x.Tags, combo);
                p.Tags = combo;
            }
        }
        // stagger: leave every victim on a different combination, some sharing, some empty
        for (var i = 0; i < victims.Count; i++) {
            var combo = combos[i % combos.Length];
            store.UpdateProperty<Product, string[]>(victims[i].Id, x => x.Tags, combo);
            victims[i].Tags = combo;
        }
        store.Delete(victims[0].Id);
        store.Delete(victims[7].Id);
        var remaining = stored.Where(p => p.Id != victims[0].Id && p.Id != victims[7].Id).ToList();

        var res = store.Query<Product>().Facets().AddValueFacet("Tags").Execute();
        var tagFacet = FacetOf(res, "Tags");
        Assert.AreEqual(remaining.Count, res.SourceCount);
        foreach (var g in remaining.SelectMany(p => p.Tags.Distinct()).GroupBy(t => t)) {
            Assert.AreEqual(g.Count(), tagFacet.Values.First(v => Equals(v.Value, g.Key)).Count, "Wrong count for tag " + g.Key);
        }
        foreach (var fv in tagFacet.Values.Where(v => v.Value != null)) { // no stale buckets with wrong counts either
            Assert.AreEqual(remaining.Count(p => p.Tags.Contains((string)fv.Value!)), fv.Count, "Stale count for tag " + fv.Value);
        }
        // selection exercises FilterInValues over the same index state:
        var sel = store.Query<Product>().Facets().AddValueFacet("Tags").SetFacetValue("Tags", "x1").Execute();
        Assert.AreEqual(remaining.Count(p => p.Tags.Contains("x1")), sel.Count());
        store.Dispose();
    }

    [TestMethod]
    public void GuidArrayFacet_CountsAndFilters_DuplicatesInOneNodeCountOnce() {
        var store = OpenProductStore(out var all, out _);
        var res = store.Query<Product>().Facets()
            .AddValueFacet("TagIds")
            .SetFacetValue("TagIds", _tagRed)
            .Execute();
        var facet = FacetOf(res, "TagIds");
        var expectedRed = all.Count(p => p.TagIds.Contains(_tagRed)); // nodes with [red, red, blue] count once
        Assert.AreEqual(expectedRed, facet.Values.First(v => Equals(v.Value, _tagRed)).Count);
        Assert.AreEqual(expectedRed, res.Count());
        // the same selection given as a string must coerce to the Guid buckets:
        var res2 = store.Query<Product>().Facets()
            .AddValueFacet("TagIds")
            .SetFacetValue("TagIds", _tagRed.ToString())
            .Execute();
        Assert.AreEqual(expectedRed, res2.Count());
        Assert.IsTrue(FacetOf(res2, "TagIds").Values.First(v => Equals(v.Value, _tagRed)).Selected);
        store.Dispose();
    }

    [TestMethod]
    public void GuidArrayFacet_UnparsableSelection_MatchesNothing() {
        // an unparsable selection must filter to an empty result, not silently drop the filter
        var store = OpenProductStore(out _, out _);
        var res = store.Query<Product>().Facets()
            .AddValueFacet("TagIds")
            .SetFacetValue("TagIds", "not-a-guid")
            .Execute();
        Assert.AreEqual(0, res.Count());
        store.Dispose();
    }

    [TestMethod]
    public void GuidArrayIndex_InternedArrays_SurviveUpdateChurnAndDeletes() {
        // same churn as the string array variant: interned Guid[] combinations are repeatedly
        // created, released to zero, and their slots reused - counts and selections must stay
        // exactly in sync with a plain LINQ ground truth throughout
        var store = OpenProductStore(out _, out _);
        churnTagIdsAndVerify(store);
    }

    [TestMethod]
    public void GuidArrayIndex_InternedArrays_PersistedBackend_SurviveUpdateChurnAndDeletes() {
        // same churn against the persisted (native KV) guid array index, whose in-memory mirror
        // uses the same intern table
        var store = OpenProductStore(out _, out _, persistedIndexes: true);
        churnTagIdsAndVerify(store);
    }

    static void churnTagIdsAndVerify(NodeStore store) {
        var x1 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        var y2 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
        var z3 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
        var stored = store.Query<Product>().ToList();
        var victims = stored.Take(20).ToList();
        Guid[][] combos = [[x1], [x1, y2], [], [y2, x1], [z3], [x1, y2]];
        foreach (var combo in combos) {
            foreach (var p in victims) {
                store.UpdateProperty<Product, Guid[]>(p.Id, x => x.TagIds, combo);
                p.TagIds = combo;
            }
        }
        // stagger: leave every victim on a different combination, some sharing, some empty
        for (var i = 0; i < victims.Count; i++) {
            var combo = combos[i % combos.Length];
            store.UpdateProperty<Product, Guid[]>(victims[i].Id, x => x.TagIds, combo);
            victims[i].TagIds = combo;
        }
        store.Delete(victims[0].Id);
        store.Delete(victims[7].Id);
        var remaining = stored.Where(p => p.Id != victims[0].Id && p.Id != victims[7].Id).ToList();

        var res = store.Query<Product>().Facets().AddValueFacet("TagIds").Execute();
        var tagFacet = FacetOf(res, "TagIds");
        Assert.AreEqual(remaining.Count, res.SourceCount);
        foreach (var g in remaining.SelectMany(p => p.TagIds.Distinct()).GroupBy(t => t)) {
            Assert.AreEqual(g.Count(), tagFacet.Values.First(v => Equals(v.Value, g.Key)).Count, "Wrong count for tag id " + g.Key);
        }
        foreach (var fv in tagFacet.Values.Where(v => v.Value != null)) { // no stale buckets with wrong counts either
            Assert.AreEqual(remaining.Count(p => p.TagIds.Contains((Guid)fv.Value!)), fv.Count, "Stale count for tag id " + fv.Value);
        }
        // selection exercises FilterInValues over the same index state:
        var sel = store.Query<Product>().Facets().AddValueFacet("TagIds").SetFacetValue("TagIds", x1).Execute();
        Assert.AreEqual(remaining.Count(p => p.TagIds.Contains(x1)), sel.Count());
        store.Dispose();
    }

    [TestMethod]
    public void GuidArray_ValuesAndFacets_SurviveRestart() {
        // disk-backed round trip: node values travel through the WAL (GuidArrayPropertyModel
        // byte format) and, on the persisted variant, the index mirror reloads from the backend
        foreach (var persistedIndexes in new[] { false, true }) {
            var dir = Path.Combine(Path.GetTempPath(), "relatude-guidarray-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {
                var store = openProductStoreOnDisk(dir, persistedIndexes);
                var brands = new List<Brand> { new() { Id = Guid.NewGuid(), Name = "Acme" } };
                store.Insert(brands);
                var all = new List<Product>();
                for (var i = 1; i <= 30; i++) {
                    all.Add(new Product {
                        Category = _categories[i % 4],
                        TagIds = i % 5 == 0 ? [_tagRed, _tagRed, _tagBlue] : (i % 2 == 0 ? [_tagRed] : [_tagGreen]),
                        Brand = new() { Id = brands[0].Id },
                    });
                }
                store.Insert(all);
                var truth = store.Query<Product>().ToList(); // re-read for ids
                store.Dispose();

                store = openProductStoreOnDisk(dir, persistedIndexes);
                var reloaded = store.Query<Product>().ToList();
                Assert.AreEqual(truth.Count, reloaded.Count, "persistedIndexes: " + persistedIndexes);
                foreach (var p in truth) {
                    var r = reloaded.First(x => x.Id == p.Id);
                    CollectionAssert.AreEqual(p.TagIds, r.TagIds, "TagIds must round-trip exactly (order and duplicates), persistedIndexes: " + persistedIndexes);
                }
                var res = store.Query<Product>().Facets()
                    .AddValueFacet("TagIds")
                    .SetFacetValue("TagIds", _tagRed)
                    .Execute();
                var facet = FacetOf(res, "TagIds");
                var expectedRed = truth.Count(p => p.TagIds.Contains(_tagRed));
                Assert.AreEqual(expectedRed, facet.Values.First(v => Equals(v.Value, _tagRed)).Count, "persistedIndexes: " + persistedIndexes);
                Assert.AreEqual(expectedRed, res.Count(), "persistedIndexes: " + persistedIndexes);
                store.Dispose();
            } finally {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }

    static NodeStore openProductStoreOnDisk(string dir, bool persistedIndexes) {
        var dm = new Datamodel();
        dm.Add<Product>();
        dm.Add<Book>();
        dm.Add<Brand>();
        if (persistedIndexes) {
            var settings = new SettingsLocal {
                UsePersistedValueIndexesByDefault = true,
                PersistedValueIndexEngine = PersistedValueIndexEngine.Native,
            };
            return new NodeStore(DataStoreLocal.Open(dm, settings, new Relatude.DB.IO.IOProviderDisk(dir), null, null, null, null,
                () => new DB.DataStores.Indexes.IndexEngines(new DB.DataStores.Indexes.KvStore.NativeKvIndexStore(dir))));
        }
        return new NodeStore(DataStoreLocal.Open(dm, new SettingsLocal(), new Relatude.DB.IO.IOProviderDisk(dir), null, null, null, null, null));
    }

    [TestMethod]
    public void EvaluateForJson_IncludesFacetsAndFiltering() {
        // the JSON path used to evaluate the base query WITHOUT the facet clauses
        var store = OpenProductStore(out var all, out _);
        var json = store.Query<Product>().Facets()
            .AddValueFacet("Category")
            .SetFacetValue("Category", "Toys")
            .EvaluateForJson();
        var res = json as ResultSetFacetsNotEnumerable<object?>;
        Assert.IsNotNull(res, "The JSON path must return a facet result set");
        var facet = res.Facets.First(f => f.CodeName == "Category");
        Assert.IsTrue(facet.Values.First(v => Equals(v.Value, "Toys")).Selected, "Selections must survive the JSON path");
        Assert.AreEqual(all.Count(p => p.Category == "Toys"), res.TotalCount, "Facet filtering must apply on the JSON path");
        store.Dispose();
    }

    [TestMethod]
    public void MultiSelection_ResultIsIndependentOfSelectionOrder() {
        // selection filters are internally reordered by estimated effect (most selective first);
        // that must never change the result, whatever order the facets were added in
        var store = OpenProductStore(out var all, out var brands);
        var brandId = brands[0].Id;
        var res1 = store.Query<Product>().Facets()
            .AddValueFacet("Category").AddValueFacet("Active").AddValueFacet("Brand")
            .SetFacetValue("Category", "Toys").SetFacetValue("Active", true).SetFacetValue("Brand", brandId)
            .Execute();
        var res2 = store.Query<Product>().Facets()
            .AddValueFacet("Brand").AddValueFacet("Active").AddValueFacet("Category")
            .SetFacetValue("Brand", brandId).SetFacetValue("Active", true).SetFacetValue("Category", "Toys")
            .Execute();
        var expected = all.Count(p => p.Category == "Toys" && p.Active && p.Brand.Id == brandId);
        Assert.AreEqual(expected, res1.Count());
        Assert.AreEqual(expected, res2.Count());
        foreach (var res in new[] { res1, res2 }) {
            // drill-sideways: a selected facet's counts ignore its own selection but respect the others
            var catFacet = FacetOf(res, "Category");
            Assert.AreEqual(all.Count(p => p.Category == "Games" && p.Active && p.Brand.Id == brandId),
                catFacet.Values.First(v => Equals(v.Value, "Games")).Count);
            Assert.AreEqual(all.Count(p => p.Category == "Toys" && p.Active && p.Brand.Id == brandId),
                catFacet.Values.First(v => Equals(v.Value, "Toys")).Count);
        }
        store.Dispose();
    }

    [TestMethod]
    public void MultiSelection_EmptyMatch_ShortCircuitsAndKeepsSidewaysCounts() {
        // a selection matching nothing empties the running set early (remaining filters are
        // skipped); the drill-sideways counts of the other facets must still be correct
        var store = OpenProductStore(out var all, out _);
        var res = store.Query<Product>().Facets()
            .AddValueFacet("Category").AddValueFacet("Active")
            .SetFacetValue("Category", "NoSuchCategory").SetFacetValue("Active", true)
            .Execute();
        Assert.AreEqual(0, res.Count());
        // Active counts against the (empty) category selection...
        var activeFacet = FacetOf(res, "Active");
        Assert.AreEqual(0, activeFacet.Values.First(v => Equals(v.Value, true)).Count);
        // ...while Category counts ignore their own selection and see only Active = true:
        var catFacet = FacetOf(res, "Category");
        Assert.AreEqual(all.Count(p => p.Category == "Toys" && p.Active),
            catFacet.Values.First(v => Equals(v.Value, "Toys")).Count);
        store.Dispose();
    }

    [TestMethod]
    public async Task ExecuteAsync_MatchesExecute() {
        var store = OpenProductStore(out var all, out _);
        var res = await store.Query<Product>().Facets()
            .AddValueFacet("Category")
            .SetFacetValue("Category", "Food")
            .ExecuteAsync();
        Assert.AreEqual(all.Count(p => p.Category == "Food"), res.Count());
        Assert.AreEqual(all.Count(p => p.Category == "Food"), FacetOf(res, "Category").Values.First(v => Equals(v.Value, "Food")).Count);
        store.Dispose();
    }
}

#region facet attribute settings test datamodel
public enum GadgetKind { Basic = 1, Pro = 2, Max = 3 }
[Node]
public class Gadget {
    [InternalIdProperty]
    public int Id { get; set; }
    [IntegerProperty(Indexed = true)]
    public GadgetKind Kind { get; set; }
    [DoubleProperty(Indexed = true, FacetRangeCount = 4)]
    public double Score { get; set; }
    [StringProperty(Indexed = true)]
    public string Line { get; set; } = "";
    [StringProperty(Indexed = true, NotFacet = true)]
    public string Serial { get; set; } = "";
}
#endregion

[TestClass]
public class FacetAttributeSettingsTests {

    static NodeStore OpenGadgetStore(out List<Gadget> all) {
        var dm = new Datamodel();
        dm.Add<Gadget>();
        var store = new NodeStore(DataStoreLocal.Open(dm));
        all = new List<Gadget>();
        for (var i = 1; i <= 12; i++) {
            all.Add(new Gadget {
                Kind = (GadgetKind)(i % 3 + 1),
                Score = i * 2.5, // distinct values, but fewer than the automatic range threshold
                Line = "L" + i % 3,
                Serial = "SN" + i,
            });
        }
        store.Insert(all);
        return store;
    }

    static Facets FacetOf<T>(ResultSetFacets<T> res, string codeName)
        => res.Facets.First(f => f.CodeName == codeName);

    [TestMethod]
    public void NotFacet_ExcludesIndexedPropertyFromFaceting() {
        var store = OpenGadgetStore(out _);
        var res = store.Query<Gadget>().Facets().Execute(); // no adds: every facet-capable property
        Assert.IsTrue(res.Facets.Any(f => f.CodeName == "Line"), "An indexed property must facet by default");
        Assert.IsFalse(res.Facets.Any(f => f.CodeName == "Serial"), "NotFacet must exclude the property from default facets");
        var explicitRes = store.Query<Gadget>().Facets().AddValueFacet("Serial").AddValueFacet("Line").Execute();
        Assert.IsTrue(explicitRes.Facets.Any(f => f.CodeName == "Line"));
        Assert.IsFalse(explicitRes.Facets.Any(f => f.CodeName == "Serial"), "NotFacet must drop explicit facet requests too");
        store.Dispose();
    }

    [TestMethod]
    public void FacetRangeCount_OnDoubleAttribute_DrivesDefaultRangeBuckets() {
        var store = OpenGadgetStore(out var all);
        // 12 distinct values would give value buckets by default; FacetRangeCount = 4 must force ranges:
        var facet = FacetOf(store.Query<Gadget>().Facets().AddFacet("Score").Execute(), "Score");
        Assert.IsTrue(facet.IsRangeFacet == true, "FacetRangeCount on the attribute must switch the default to range buckets");
        Assert.IsTrue(facet.Values.Count is >= 2 and <= 5, "Bucket count must follow the attribute (4, soft cap), got " + facet.Values.Count);
        Assert.AreEqual(all.Count, facet.Values.Sum(v => v.Count), "Contiguous buckets must cover every value");
        // an explicit value facet still overrides the model setting:
        var valueFacet = FacetOf(store.Query<Gadget>().Facets().AddValueFacet("Score").Execute(), "Score");
        Assert.IsFalse(valueFacet.IsRangeFacet == true);
        Assert.AreEqual(all.Select(g => g.Score).Distinct().Count(), valueFacet.Values.Count);
        store.Dispose();
    }

    [TestMethod]
    public void ScalarEnumFacet_ShowsEnumNames_AndResolvesNameSelections() {
        var store = OpenGadgetStore(out var all);
        var facet = FacetOf(store.Query<Gadget>().Facets().AddValueFacet("Kind").Execute(), "Kind");
        foreach (var (value, name) in new[] { (1, "Basic"), (2, "Pro"), (3, "Max") }) {
            var fv = facet.Values.FirstOrDefault(v => Equals(v.Value, value));
            Assert.IsNotNull(fv, "Missing bucket for " + name);
            Assert.AreEqual(name, fv.DisplayName, "Scalar enum buckets must show the enum name, like enum arrays");
            Assert.AreEqual(all.Count(g => (int)g.Kind == value), fv.Count);
        }
        // selection with a typed enum value:
        var sel = store.Query<Gadget>().Facets().AddValueFacet("Kind").SetFacetValue("Kind", GadgetKind.Pro).Execute();
        Assert.AreEqual(all.Count(g => g.Kind == GadgetKind.Pro), sel.Count());
        Assert.IsTrue(sel.All(g => g.Kind == GadgetKind.Pro));
        // selection with the enum NAME string (as a facet UI would send it):
        var selByName = store.Query<Gadget>().Facets().AddValueFacet("Kind").SetFacetValue("Kind", "Pro").Execute();
        Assert.AreEqual(all.Count(g => g.Kind == GadgetKind.Pro), selByName.Count());
        Assert.IsTrue(selByName.All(g => g.Kind == GadgetKind.Pro));
        store.Dispose();
    }

    [TestMethod]
    public void DateTimeRangeFacet_DefaultsToLinearCalendarBuckets() {
        // dates default to RangePowerBase = 1: uniform calendar steps, not the general 1.8 power
        // curve (which anchors at the minimum and would give the OLDEST dates the finest buckets)
        var dm = new Datamodel();
        dm.Add<Product>();
        dm.Add<Brand>();
        var store = new NodeStore(DataStoreLocal.Open(dm));
        var all = new List<Product>();
        for (var i = 1; i <= 70; i++) all.Add(new Product { Released = new DateTime(2020, 1, 1).AddDays(i * 11) });
        store.Insert(all);
        var facet = FacetOf(store.Query<Product>().Facets().AddRangeFacet("Released").Execute(), "Released");
        Assert.IsTrue(facet.IsRangeFacet == true);
        Assert.AreEqual(all.Count, facet.Values.Sum(v => v.Count));
        // interior boundaries (every bucket start except the first, which is the real minimum)
        // must be aligned calendar steps with one uniform stride:
        var bounds = facet.Values.Skip(1).Select(v => (DateTime)v.Value!).ToList();
        Assert.IsTrue(bounds.Count > 1, "Expected several range buckets, got " + facet.Values.Count);
        Assert.IsTrue(bounds.All(b => b.Day == 1 && b.TimeOfDay == TimeSpan.Zero), "Interior boundaries must be aligned month starts");
        var monthIndex = bounds.Select(b => b.Year * 12 + b.Month).ToList();
        var step = monthIndex[1] - monthIndex[0];
        for (var i = 1; i < monthIndex.Count; i++) Assert.AreEqual(step, monthIndex[i] - monthIndex[i - 1], "Bucket strides must be uniform");
        store.Dispose();
    }

    [TestMethod]
    public void ScalarEnumFacet_ManyDistinctValues_StaysValueBuckets() {
        // 30 distinct underlying values exceed the automatic range threshold; enums must still
        // facet as one bucket per value (like enum arrays), never as int ranges
        var dm = new Datamodel();
        dm.Add<Gadget>();
        var store = new NodeStore(DataStoreLocal.Open(dm));
        var all = new List<Gadget>();
        for (var i = 1; i <= 30; i++) all.Add(new Gadget { Kind = (GadgetKind)i, Score = i, Line = "L", Serial = "SN" + i });
        store.Insert(all);
        var facet = FacetOf(store.Query<Gadget>().Facets().AddFacet("Kind").Execute(), "Kind");
        Assert.IsFalse(facet.IsRangeFacet == true, "Enum facets must never switch to range buckets automatically");
        Assert.AreEqual(30, facet.Values.Count);
        Assert.AreEqual("Pro", facet.Values.First(v => Equals(v.Value, 2)).DisplayName); // defined values show their name...
        Assert.IsNull(facet.Values.First(v => Equals(v.Value, 7)).ExplicitDisplayName); // ...undefined values keep the generated fallback
        store.Dispose();
    }
}

#region high cardinality facet test datamodel
[Node]
public class Ticket {
    [InternalIdProperty]
    public int Id { get; set; }
    [StringProperty(Indexed = true)]
    public string Code { get; set; } = ""; // one distinct value per node
    [StringProperty(Indexed = true)]
    public string Status { get; set; } = ""; // a handful of distinct values
    [StringProperty(Indexed = true)]
    public string Group { get; set; } = ""; // exactly at the limit
    [IntegerProperty(Indexed = true)]
    public int Number { get; set; } // one distinct value per node, but bucketed into ranges
    [StringArrayProperty(Indexed = true)]
    public string[] Labels { get; set; } = []; // one distinct element per node
    [GuidArrayProperty(Indexed = true)]
    public Guid[] LabelIds { get; set; } = [];
}
#endregion

// Automatic facets (a .Facets() query that names no property) drop properties whose buckets are one
// per unique value once there are too many of them - a facet with thousands of string or guid
// buckets is useless in a UI and expensive to build. Range bucketed and explicitly named facets are
// never dropped.
[TestClass]
public class AutomaticFacetCardinalityTests {

    const int _limit = 100; // Property.MaxAutomaticFacetValues (internal to the store assembly)

    static Guid labelId(int i) => Guid.Parse("00000000-0000-0000-0000-" + i.ToString("D12"));

    static NodeStore openTicketStore(int count) {
        var dm = new Datamodel();
        dm.Add<Ticket>();
        var store = new NodeStore(DataStoreLocal.Open(dm));
        var all = new List<Ticket>();
        for (var i = 1; i <= count; i++) {
            all.Add(new Ticket {
                Code = "C" + i,
                Status = "S" + (i % 3),
                Group = "G" + (i % _limit),
                Number = i,
                Labels = ["L" + i],
                LabelIds = [labelId(i)],
            });
        }
        store.Insert(all);
        return store;
    }
    static Facets FacetOf<T>(ResultSetFacets<T> res, string codeName)
        => res.Facets.First(f => f.CodeName == codeName);

    [TestMethod]
    public void AutomaticFacets_SkipPropertiesWithTooManyValues() {
        var store = openTicketStore(_limit + 50);
        var names = store.Query<Ticket>().Facets().Execute().Facets.Select(f => f.CodeName).ToList();
        Assert.IsFalse(names.Contains("Code"), "A string property with one value per node must not be an automatic facet");
        Assert.IsFalse(names.Contains("Labels"), "A string array property with one element per node must not be an automatic facet");
        Assert.IsFalse(names.Contains("LabelIds"), "A guid array property with one element per node must not be an automatic facet");
        Assert.IsTrue(names.Contains("Status"));
        Assert.IsTrue(names.Contains("Group"), "Exactly at the limit is still a facet");
        store.Dispose();
    }

    [TestMethod]
    public void AutomaticFacets_KeepRangeBucketedProperties() {
        // the limit is about one bucket per unique value; a range facet has as many buckets as it
        // was asked for, whatever the cardinality of the property
        var store = openTicketStore(_limit + 50);
        var res = store.Query<Ticket>().Facets().Execute();
        var number = FacetOf(res, "Number");
        Assert.IsTrue(number.IsRangeFacet == true);
        Assert.IsTrue(number.Values.Count <= _limit);
        Assert.AreEqual(_limit + 50, number.Values.Sum(v => v.Count));
        store.Dispose();
    }

    [TestMethod]
    public void AutomaticFacets_KeepPropertiesAtTheLimit() {
        var store = openTicketStore(_limit); // every property now holds exactly the limit
        var res = store.Query<Ticket>().Facets().Execute();
        var names = res.Facets.Select(f => f.CodeName).ToList();
        Assert.IsTrue(names.Contains("Code"));
        Assert.IsTrue(names.Contains("Labels"));
        Assert.IsTrue(names.Contains("LabelIds"));
        Assert.AreEqual(_limit, FacetOf(res, "Code").Values.Count);
        store.Dispose();
    }

    [TestMethod]
    public void ExplicitFacets_AreNeverSkipped() {
        var store = openTicketStore(_limit + 50);
        var res = store.Query<Ticket>().Facets()
            .AddValueFacet("Code")
            .AddFacet("Labels")
            .Execute();
        Assert.AreEqual(_limit + 50, FacetOf(res, "Code").Values.Count);
        Assert.AreEqual(_limit + 50, FacetOf(res, "Labels").Values.Count);
        Assert.AreEqual(2, res.Facets.Count()); // naming any facet turns the automatic selection off
        // and a selection on such a facet still filters:
        var filtered = store.Query<Ticket>().Facets()
            .AddValueFacet("Code")
            .SetFacetValue("Code", "C7")
            .Execute();
        Assert.AreEqual(1, filtered.Count());
        store.Dispose();
    }
}
