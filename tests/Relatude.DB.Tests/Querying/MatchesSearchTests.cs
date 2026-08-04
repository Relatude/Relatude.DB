using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Nodes;
using Relatude.DB.Query;
using Relatude.Utils;

namespace Relatude.Querying;

#region per property search test datamodel
// TextIndex opts the node type into the combined text index that WhereSearch uses, so the tests can
// contrast it against the per property search. InstantTextIndexing is needed because that index is
// otherwise filled by a background queue and would still be empty right after Insert - the per
// property word indexes behind MatchesSearch are always written as part of the transaction
[Node(TextIndex = BoolValue.True, InstantTextIndexing = BoolValue.True)]
public class Listing {
    [InternalIdProperty]
    public int Id { get; set; }
    [StringProperty(IndexedByWords = true)]
    public string Title { get; set; } = "";
    [StringProperty(IndexedByWords = true)]
    public string Body { get; set; } = "";
    // word searchable but deliberately not value indexed, to show the two indexes are independent
    [StringProperty(Indexed = true)]
    public string Sku { get; set; } = "";
    [IntegerProperty(Indexed = true)]
    public int Price { get; set; }
}
#endregion

[TestClass]
public class MatchesSearchTests {

    // "wool" appears in one title and a different body, so a per property search can tell them apart
    // where a search of the node's combined text index cannot
    static readonly (string title, string body)[] _listings = [
        ("Wool jacket", "Warm and waterproof outer layer"),
        ("Cotton shirt", "Light wool lining for summer"),
        ("Leather boots", "Waterproof leather with rubber sole"),
        ("Rubber gloves", "Thin gloves for washing up"),
        ("Linen trousers", "Breathable linen for warm days"),
    ];

    static NodeStore OpenStore(out List<Listing> all) {
        var dm = new Datamodel();
        dm.Add<Listing>();
        var store = new NodeStore(DataStoreLocal.Open(dm));
        var listings = new List<Listing>();
        for (var i = 0; i < _listings.Length; i++) {
            listings.Add(new Listing { Title = _listings[i].title, Body = _listings[i].body, Sku = "SKU-" + i, Price = 100 + i });
        }
        store.Insert(listings);
        all = store.Query<Listing>().ToList();
        return store;
    }

    static int[] ids(NodeStore store, System.Linq.Expressions.Expression<Func<Listing, bool>> predicate)
        => store.Query<Listing>().Where(predicate).Execute().Select(x => x.Id).OrderBy(i => i).ToArray();

    static int[] titled(List<Listing> all, params string[] titles)
        => all.Where(x => titles.Contains(x.Title)).Select(x => x.Id).OrderBy(i => i).ToArray();

    [TestMethod]
    public void MatchesSearch_FindsWordsInTheGivenProperty() {
        var store = OpenStore(out var all);
        CollectionAssert.AreEqual(titled(all, "Wool jacket"), ids(store, x => x.Title.MatchesSearch("wool")));
        CollectionAssert.AreEqual(titled(all, "Leather boots"), ids(store, x => x.Title.MatchesSearch("boots")));
        CollectionAssert.AreEqual(titled(all, "Wool jacket", "Leather boots"), ids(store, x => x.Body.MatchesSearch("waterproof")));
        Assert.AreEqual(0, store.Query<Listing>().Where(x => x.Title.MatchesSearch("nosuchword")).Count());
        store.Dispose();
    }

    [TestMethod]
    public void MatchesSearch_IsScopedToOneProperty() {
        var store = OpenStore(out var all);
        // "wool" is in one node's Title and another node's Body
        CollectionAssert.AreEqual(titled(all, "Wool jacket"), ids(store, x => x.Title.MatchesSearch("wool")));
        CollectionAssert.AreEqual(titled(all, "Cotton shirt"), ids(store, x => x.Body.MatchesSearch("wool")));
        // WhereSearch covers the node's combined text index, so it cannot make that distinction
        Assert.AreEqual(2, store.Query<Listing>().WhereSearch("wool").Count(),
            "WhereSearch searches every text indexed property, so it must find both");
        store.Dispose();
    }

    [TestMethod]
    public void MatchesSearch_ComposesWithOrAndNot() {
        var store = OpenStore(out var all);
        // the reason this is a predicate rather than a query stage: OR across two properties
        CollectionAssert.AreEqual(titled(all, "Wool jacket", "Cotton shirt"),
            ids(store, x => x.Title.MatchesSearch("wool") || x.Body.MatchesSearch("wool")));
        CollectionAssert.AreEqual(titled(all, "Wool jacket"),
            ids(store, x => x.Body.MatchesSearch("waterproof") && x.Title.MatchesSearch("wool")));
        CollectionAssert.AreEqual(titled(all, "Cotton shirt", "Leather boots", "Rubber gloves", "Linen trousers"),
            ids(store, x => !x.Title.MatchesSearch("wool")));
        store.Dispose();
    }

    [TestMethod]
    public void MatchesSearch_CombinesWithOtherIndexedFilters() {
        var store = OpenStore(out var all);
        CollectionAssert.AreEqual(titled(all, "Leather boots"),
            ids(store, x => x.Body.MatchesSearch("waterproof") && x.Price > 100));
        CollectionAssert.AreEqual(titled(all, "Wool jacket"),
            ids(store, x => x.Body.MatchesSearch("waterproof") && x.Price == 100));
        CollectionAssert.AreEqual(Array.Empty<int>(),
            ids(store, x => x.Body.MatchesSearch("waterproof") && x.Price > 1000));
        store.Dispose();
    }

    [TestMethod]
    public void MatchesSearch_IsWordMatchingNotSubstring() {
        var store = OpenStore(out var all);
        // "proof" is a substring of "waterproof" but not a word in it
        CollectionAssert.AreEqual(titled(all, "Wool jacket", "Leather boots"), ids(store, x => x.Body.Contains("proof")));
        Assert.AreEqual(0, store.Query<Listing>().Where(x => x.Body.MatchesSearch("proof")).Count(),
            "MatchesSearch matches indexed words, so an interior fragment must not match");
        // a trailing wildcard asks for a prefix match, which does hit the whole word
        CollectionAssert.AreEqual(titled(all, "Wool jacket", "Leather boots"), ids(store, x => x.Body.MatchesSearch("water*")));
        store.Dispose();
    }

    [TestMethod]
    public void MatchesSearch_MultipleTermsDefaultToAnd() {
        var store = OpenStore(out var all);
        CollectionAssert.AreEqual(titled(all, "Leather boots"), ids(store, x => x.Body.MatchesSearch("waterproof leather")));
        // orSearch true widens it to either term. every setting has to be given: C# does not allow
        // omitted optional arguments inside an expression tree
        CollectionAssert.AreEqual(titled(all, "Wool jacket", "Leather boots"),
            ids(store, x => x.Body.MatchesSearch("waterproof leather", null, null, true, null)));
        store.Dispose();
    }

    [TestMethod]
    public void MatchesSearch_WorksFromQueryString() {
        var store = OpenStore(out var all);
        Assert.AreEqual(1, store.Query<Listing>().Where("x => x.Title.MatchesSearch(\"wool\")").Count());
        Assert.AreEqual(2, store.Query<Listing>().Where("x => x.Body.MatchesSearch(\"waterproof leather\", null, null, true, null)").Count());
        store.Dispose();
    }

    [TestMethod]
    public void MatchesSearch_ReflectsUpdatesAndDeletes() {
        var store = OpenStore(out var all);
        var jacket = all.Single(x => x.Title == "Wool jacket");
        store.UpdateProperty<Listing, string>(jacket.Id, x => x.Title, "Cashmere jacket");
        Assert.AreEqual(0, store.Query<Listing>().Where(x => x.Title.MatchesSearch("wool")).Count(), "the old word must be de-indexed");
        Assert.AreEqual(1, store.Query<Listing>().Where(x => x.Title.MatchesSearch("cashmere")).Count());
        store.Delete(jacket.Id);
        Assert.AreEqual(0, store.Query<Listing>().Where(x => x.Title.MatchesSearch("cashmere")).Count());
        Assert.AreEqual(1, store.Query<Listing>().Where(x => x.Body.MatchesSearch("waterproof leather")).Count());
        store.Dispose();
    }

    [TestMethod]
    public void MatchesSearch_SurvivesRestart() {
        var dir = Path.Combine(Path.GetTempPath(), "relatude-matchessearch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var store = openStoreOnDisk(dir);
            var listings = new List<Listing>();
            for (var i = 0; i < _listings.Length; i++) {
                listings.Add(new Listing { Title = _listings[i].title, Body = _listings[i].body, Sku = "SKU-" + i, Price = 100 + i });
            }
            store.Insert(listings);
            store.Dispose();

            store = openStoreOnDisk(dir);
            Assert.AreEqual(1, store.Query<Listing>().Where(x => x.Title.MatchesSearch("wool")).Count());
            Assert.AreEqual(2, store.Query<Listing>().Where(x => x.Body.MatchesSearch("waterproof")).Count());
            store.Dispose();
        } finally {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [TestMethod]
    public void MatchesSearch_WithoutWordIndex_ThrowsWithGuidance() {
        var store = OpenStore(out _);
        // Sku is value indexed but not word indexed: there is no row evaluation of a search, so this
        // must explain the fix rather than quietly matching nothing
        var ex = Assert.ThrowsException<NotSupportedException>(
            () => store.Query<Listing>().Where(x => x.Sku.MatchesSearch("SKU")).Count());
        StringAssert.Contains(ex.Message, "IndexedByWords");
        // Contains still works there, and is what the message points at
        Assert.AreEqual(5, store.Query<Listing>().Where(x => x.Sku.Contains("SKU")).Count());
        store.Dispose();
    }

    [TestMethod]
    public void MatchesSearch_OnNonStringProperty_Throws() {
        var store = OpenStore(out _);
        var ex = Assert.ThrowsException<NotSupportedException>(
            () => store.Query<Listing>().Where("x => x.Price.MatchesSearch(\"100\")").Count());
        StringAssert.Contains(ex.Message, "string");
        store.Dispose();
    }

    static NodeStore openStoreOnDisk(string dir) {
        var dm = new Datamodel();
        dm.Add<Listing>();
        return new NodeStore(DataStoreLocal.Open(dm, new SettingsLocal(), new Relatude.DB.IO.IOProviderDisk(dir), null, null, null, null, null));
    }
}
