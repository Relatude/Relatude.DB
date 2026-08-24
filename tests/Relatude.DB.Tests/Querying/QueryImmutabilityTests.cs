using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Nodes;
using Relatude.DB.Query;

namespace Relatude.Querying;

#region query immutability test datamodel
// InstantTextIndexing keeps the word index inside the transaction, so the search tests
// see hits right after Insert (it is otherwise filled by a background queue)
[Node(TextIndex = BoolValue.True, InstantTextIndexing = BoolValue.True)]
public class ImmProduct {
    [InternalIdProperty]
    public int Id { get; set; }
    [PublicIdProperty]
    public Guid PId { get; set; }
    [StringProperty(Indexed = true, IndexedByWords = true)]
    public string Name { get; set; } = "";
    [StringProperty(Indexed = true)]
    public string Category { get; set; } = "";
    [IntegerProperty(Indexed = true)]
    public int Price { get; set; }
    [RelationProperty]
    public ImmBrand? Brand { get; set; }
}
[Node]
public class ImmBrand {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(DisplayName = true)]
    public string Name { get; set; } = "";
    public ImmCountry? Country { get; set; }
    public IEnumerable<ImmProduct>? Products { get; set; }
}
[Node]
public class ImmCountry {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(DisplayName = true)]
    public string Name { get; set; } = "";
    public IEnumerable<ImmBrand>? Brands { get; set; }
}
#endregion

/// <summary>
/// Query objects are immutable: every operator must return a new query and leave the original
/// unchanged, so a base query can be stored and forked (like LINQ). These tests guard the fork
/// semantics and the traps of the old mutable builder: count-then-execute poisoning, include and
/// select aliasing, and facet forks sharing selection state.
/// </summary>
[TestClass]
public class QueryImmutabilityTests {

    static readonly string[] _categories = ["Toys", "Games", "Tools"];

    // 30 products: category round-robin, price = i * 10, every 5th has no brand
    static NodeStore openStore() {
        var dm = new Datamodel();
        dm.Add<ImmProduct>(autoDeduceRelations: true);
        dm.Add<ImmBrand>(autoDeduceRelations: true);
        dm.Add<ImmCountry>(autoDeduceRelations: true);
        var store = new NodeStore(DataStoreLocal.Open(dm));
        var country = new ImmCountry { Id = Guid.NewGuid(), Name = "Norway" };
        store.Insert(country);
        var brands = new List<ImmBrand>();
        for (var i = 0; i < 3; i++) brands.Add(new ImmBrand { Id = Guid.NewGuid(), Name = "Brand " + i });
        store.Insert(brands);
        foreach (var brand in brands) store.AddRelation(brand, b => b.Country, country);
        for (var i = 1; i <= 30; i++) {
            var product = new ImmProduct { PId = Guid.NewGuid(), Name = "waterproof gadget number " + i, Category = _categories[i % 3], Price = i * 10 };
            store.Insert(product);
            if (i % 5 != 0) store.AddRelation(product, p => p.Brand, brands[i % 3]);
        }
        return store;
    }

    [TestMethod]
    public void WhereForksAreIndependent() {
        using var store = openStore();
        var baseQuery = store.Query<ImmProduct>().Where(p => p.Category == "Toys");
        var cheap = baseQuery.Where(p => p.Price < 150);
        var costly = baseQuery.Where(p => p.Price >= 150);
        // the forks partition the base set; under the old mutable builder "costly" would carry
        // BOTH price filters and return an empty set
        Assert.AreEqual(10, baseQuery.Count());
        Assert.AreEqual(baseQuery.Count(), cheap.Count() + costly.Count());
        Assert.IsTrue(cheap.Count() > 0 && costly.Count() > 0);
        // and the base query itself is still just the category filter
        Assert.AreEqual(10, baseQuery.Execute().Count);
    }

    [TestMethod]
    public void CountAndSumDoNotPoisonTheQuery() {
        using var store = openStore();
        var q = store.Query<ImmProduct>().Where(p => p.Category == "Toys");
        var before = q.ToString();
        var count = q.Count();
        var sum = q.Sum(p => p.Price);
        Assert.AreEqual(before, q.ToString()); // no ".Count()" / ".Sum(...)" left behind
        var items = q.Execute();
        Assert.AreEqual(count, items.Count);
        Assert.AreEqual(sum, items.Sum(p => p.Price));
    }

    [TestMethod]
    public void FirstOrDefaultDoesNotPoisonTheQuery() {
        using var store = openStore();
        var q = store.Query<ImmProduct>();
        Assert.IsNotNull(q.FirstOrDefault()); // internally forks a Take(1)
        Assert.AreEqual(30, q.Execute().Count);
    }

    [TestMethod]
    public void PagingForksAreIndependent() {
        using var store = openStore();
        var q = store.Query<ImmProduct>().OrderBy(p => p.Price);
        var page = q.Page(0, 5);
        var top = q.Take(3);
        var rest = q.Skip(25);
        Assert.AreEqual(5, page.Execute().Count);
        Assert.AreEqual(3, top.Execute().Count);
        Assert.AreEqual(5, rest.Execute().Count);
        Assert.AreEqual(30, q.Execute().Count); // base unchanged
    }

    [TestMethod]
    public void SelectForkLeavesBaseUsable() {
        using var store = openStore();
        var q = store.Query<ImmProduct>().Where(p => p.Category == "Toys");
        var names = q.Select(p => p.Name);
        Assert.IsFalse(q.ToString()?.Contains(".Select("));
        Assert.AreEqual(10, names.Execute().Count);
        Assert.AreEqual(10, q.Execute().Count); // base still returns full nodes
        Assert.IsTrue(q.Execute().First().Name.StartsWith("waterproof"));
    }

    [TestMethod]
    public void SelectIdForkLeavesBaseUsable() {
        using var store = openStore();
        var q = store.Query<ImmProduct>();
        var ids = q.SelectId();
        Assert.AreEqual(30, ids.Execute().Count);
        Assert.AreEqual(30, q.Execute().Count);
        Assert.IsFalse(q.ToString()?.Contains(".SelectId("));
    }

    [TestMethod]
    public void IncludeForkLeavesBaseWithoutInclude() {
        using var store = openStore();
        var q = store.Query<ImmProduct>().Where(p => p.Price == 10); // product 1, has a brand
        var withBrand = q.Include(p => p.Brand);
        Assert.IsFalse(q.ToString()?.Contains(".Include("));
        Assert.IsTrue(withBrand.ToString()?.Contains(".Include("));
        Assert.IsNull(q.First().Brand); // not loaded without include
        Assert.IsNotNull(withBrand.First().Brand);
        Assert.IsFalse(q.ToString()?.Contains(".Include(")); // still clean after executing the fork
    }

    [TestMethod]
    public void ThenIncludeForksAreIndependent() {
        using var store = openStore();
        var q = store.Query<ImmProduct>().Where(p => p.Price == 10);
        var withBrand = q.Include(p => p.Brand);
        var brandOnly = withBrand.ToString();
        var withCountry = withBrand.ThenInclude(b => b!.Country);
        Assert.AreEqual(brandOnly, withBrand.ToString()); // ThenInclude forked, did not extend the original
        Assert.AreNotEqual(withBrand.ToString(), withCountry.ToString());
        var shallow = withBrand.First();
        Assert.IsNotNull(shallow.Brand);
        Assert.IsNull(shallow.Brand!.Country);
        var deep = withCountry.First();
        Assert.IsNotNull(deep.Brand);
        Assert.IsNotNull(deep.Brand!.Country);
    }

    [TestMethod]
    public void TwoThenIncludesFromTheSameIncludeAreIndependent() {
        using var store = openStore();
        var withBrand = store.Query<ImmProduct>().Where(p => p.Price == 10).Include(p => p.Brand);
        var withCountry = withBrand.ThenInclude(b => b!.Country);
        var withProducts = withBrand.ThenInclude(b => b!.Products!);
        Assert.AreNotEqual(withCountry.ToString(), withProducts.ToString());
        var a = withCountry.First();
        Assert.IsNotNull(a.Brand!.Country);
        Assert.IsNull(a.Brand!.Products); // the sibling fork's branch is not on this query
        var b = withProducts.First();
        Assert.IsNull(b.Brand!.Country);
        Assert.IsNotNull(b.Brand!.Products);
    }

    [TestMethod]
    public void FacetForksAreIndependent() {
        using var store = openStore();
        var fq = store.Query<ImmProduct>().Facets().AddFacet(p => p.Category);
        var toys = fq.SetFacetValue(p => p.Category, "Toys");
        var options = fq.SetFacetOptions(p => p.Category, maxValues: 1);
        // the unselected base still counts everything...
        var baseResult = fq.Execute();
        Assert.AreEqual(30, baseResult.TotalCount);
        Assert.AreEqual(3, baseResult.Facets.Single().Values.Count);
        Assert.IsFalse(baseResult.Facets.Single().Values.Any(v => v.Selected));
        // ...the selection fork filters...
        var toysResult = toys.Execute();
        Assert.AreEqual(10, toysResult.TotalCount);
        Assert.IsTrue(toysResult.Facets.Single().Values.Single(v => (string)v.Value! == "Toys").Selected);
        // ...the options fork trims values without touching the others
        Assert.AreEqual(1, options.Execute().Facets.Single().Values.Count);
        Assert.AreEqual(3, fq.Execute().Facets.Single().Values.Count); // base still untrimmed and unselected
    }

    [TestMethod]
    public void SearchForksAreIndependent() {
        using var store = openStore();
        var search = store.Query<ImmProduct>().Search("gadget");
        var top = search.Top(1);
        Assert.AreEqual(1, top.Execute().Count);
        Assert.IsTrue(search.Execute().Count > 1); // base search unaffected by the Top fork
    }

    [TestMethod]
    public void WhereSearchForkLeavesBaseUnfiltered() {
        using var store = openStore();
        var q = store.Query<ImmProduct>();
        var hits = q.WhereSearch("gadget");
        Assert.AreEqual(30, hits.Execute().Count); // every product matches
        Assert.IsFalse(q.ToString()?.Contains(".WhereSearch("));
        Assert.AreEqual(30, q.Execute().Count);
    }

    [TestMethod]
    public void QueryOfObjectsForksAreIndependent() {
        using var store = openStore();
        var prices = store.Query<ImmProduct>().Select(p => p.Price);
        // (the engine does not support Take/Page after Select yet, so the fork is an OrderBy)
        var ordered = prices.OrderBy(p => p, descending: true);
        Assert.AreEqual(300, ordered.Execute().First());
        Assert.IsFalse(prices.ToString().Contains(".OrderBy(")); // base projection unchanged
        Assert.AreEqual(30, prices.Execute().Count);
        var count = prices.Count();
        Assert.AreEqual(30, count);
        Assert.AreEqual(30, prices.Execute().Count); // count did not poison it
    }
}
