using System.Globalization;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Nodes;
using Relatude.DB.Query;
using Relatude.DB.Utils;
using static Tests.QueryTestHelpers;

namespace Tests;

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

    static NodeStore OpenProductStore(out List<Product> all, out List<Brand> brands) {
        var dm = new Datamodel();
        dm.Add<Product>();
        dm.Add<Book>();
        dm.Add<Brand>();
        var store = new NodeStore(DataStoreLocal.Open(dm));
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
                Active = i % 3 == 0,
                Brand = new() { Id = brands[i % 3].Id },
                Pages = 100 + (i % 3),
            });
        }
        store.Insert(all);
        return store;
    }

    static Facets FacetOf<T>(Relatude.DB.Query.ResultSetFacets<T> res, string codeName)
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
