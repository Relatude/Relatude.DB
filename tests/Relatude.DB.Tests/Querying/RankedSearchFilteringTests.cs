using Relatude.DB.AI;
using Relatude.DB.AI.ISV;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.Query;
using Relatude.Utils;

namespace Relatude.Querying;

#region ranked search test datamodel
// Every type in a store shares its combined word and vector indexes, so a ranked search for one type
// reads hits of the others, and of nodes its own filters exclude. These types exist to outrank each
// other in those shared indexes. InstantTextIndexing writes the word index inside the transaction in
// the stores without an AI provider; with one, the queue fills both indexes and the tests wait for it.
[Node(TextIndex = BoolValue.True, InstantTextIndexing = BoolValue.True)]
public class RankedRecipe {
    public Guid Id { get; set; }
    [InternalIdProperty]
    public int Nid { get; set; }
    [StringProperty(DisplayName = true)]
    public string Title { get; set; } = "";
    [BooleanProperty(Indexed = true)]
    public bool Published { get; set; }
    [RelationProperty(TextIndexRelatedDisplayName = true)]
    public RankedRecipeTags.Tags Tags { get; set; } = new();
}
[Node(TextIndex = BoolValue.True, InstantTextIndexing = BoolValue.True)]
public class RankedNote {
    public Guid Id { get; set; }
    [InternalIdProperty]
    public int Nid { get; set; }
    public string Text { get; set; } = "";
}
[Node(TextIndex = BoolValue.True, InstantTextIndexing = BoolValue.True)]
public class RankedTag {
    public Guid Id { get; set; }
    [StringProperty(DisplayName = true)]
    public string Name { get; set; } = "";
    public RankedRecipeTags.Recipe Recipe { get; set; } = new();
}
public class RankedRecipeTags : OneToMany<RankedRecipe, RankedTag> {
    public class Recipe : One { }
    public class Tags : Many { }
}
#endregion

/// <summary>
/// A ranked search pages and counts the hits of the query, not the hits of the index. The indexes
/// are shared by every node type and include nodes the query's filters exclude, so the filter has to
/// be applied before the page is taken - otherwise better ranked nodes of another type, or excluded
/// nodes of the same type, take the page and are then thrown away, leaving short or empty pages and a
/// total counted over nodes the query can never return.
/// </summary>
[TestClass]
public class RankedSearchFilteringTests {

    static Datamodel datamodel() {
        var dm = new Datamodel();
        dm.Add<RankedRecipe>();
        dm.Add<RankedNote>();
        dm.Add<RankedTag>();
        dm.Add<RankedRecipeTags>();
        return dm;
    }

    static NodeStore openWordStore() => new(DataStoreLocal.Open(datamodel()));

    static NodeStore openSemanticStore() =>
        new(DataStoreLocal.Open(datamodel(), new SettingsLocal { EnableSemanticIndexByDefault = true }, null, null, null, null, AIEngine.CreateDummy()));

    static void waitForIndexing(NodeStore store) {
        while (store.Datastore.IsTaskQueueBusy()) Thread.Sleep(50);
    }

    static RankedRecipe recipe(string title, bool published = true) => new() { Title = title, Published = published };
    static RankedNote note(string text) => new() { Text = text };

    static string[] titles(IEnumerable<SearchResultHit<RankedRecipe>> hits) => [.. hits.Select(h => h.Node.Title)];

    // the text the vector index embedded for a node, which the dummy AI provider embeds to the very
    // same vector again: a search for it has similarity 1 with that node and every node like it
    static string semanticExtractOf<T>(NodeStore store, Func<T, bool> pick) where T : notnull {
        var nid = store.Query<T>().Execute().First(pick) switch {
            RankedRecipe r => r.Nid,
            RankedNote n => n.Nid,
            var other => throw new ArgumentException(other.GetType().Name),
        };
        return store.Datastore.GetTextExtract([nid], TextIndexType.SemanticTextSearch).Single().Text;
    }

    // ---- word search: semanticRatio 0 ----

    [TestMethod]
    public void WordSearch_PageIsTakenAfterTheTypeFilter() {
        var store = openWordStore();
        // the notes repeat the word and outrank every recipe in the shared word index
        for (var i = 0; i < 12; i++) store.Insert(note("apple apple apple apple"));
        store.Insert([recipe("Apple pie"), recipe("Apple crumble with cream"), recipe("Baked apple"), recipe("Pear tart")]);

        var page = store.Query<RankedRecipe>().Search("apple", 0).Page(0, 2).Execute();
        Assert.AreEqual(2, page.Count, "the page must be filled with recipes, not emptied by better ranked notes");
        Assert.AreEqual(3, page.TotalCount, "the total counts the recipes that match, not every node in the index that does");
        Assert.IsTrue(titles(page).All(t => t.Contains("apple", StringComparison.OrdinalIgnoreCase)));
        store.Dispose();
    }

    [TestMethod]
    public void WordSearch_PageIsTakenAfterTheWhereFilter() {
        var store = openWordStore();
        // unpublished recipes outrank the published ones, and the filter excludes them
        for (var i = 0; i < 8; i++) store.Insert(recipe("apple apple apple apple", published: false));
        store.Insert([recipe("Apple pie"), recipe("Baked apple"), recipe("Pear tart")]);

        var page = store.Query<RankedRecipe>().Where(r => r.Published).Search("apple", 0).Page(0, 2).Execute();
        CollectionAssert.AreEquivalent(new[] { "Apple pie", "Baked apple" }, titles(page));
        Assert.AreEqual(2, page.TotalCount);
        store.Dispose();
    }

    [TestMethod]
    public void WordSearch_PagesAreWindowsOverOneRanking() {
        var store = openWordStore();
        for (var i = 0; i < 12; i++) store.Insert(note("apple apple apple apple"));
        store.Insert([recipe("Apple apple apple pie"), recipe("Apple apple crumble"), recipe("Baked apple"), recipe("Pear tart")]);

        var all = titles(store.Query<RankedRecipe>().Search("apple", 0).Page(0, 100).Execute());
        Assert.AreEqual(3, all.Length);
        var paged = new List<string>();
        for (var p = 0; p < 3; p++) {
            var page = store.Query<RankedRecipe>().Search("apple", 0).Page(p, 1).Execute();
            Assert.AreEqual(1, page.Count, "page " + p);
            Assert.AreEqual(3, page.TotalCount, "page " + p);
            paged.AddRange(titles(page));
        }
        CollectionAssert.AreEqual(all, paged, "consecutive pages must be the ranking cut into pieces, in order");
        Assert.AreEqual(0, store.Query<RankedRecipe>().Search("apple", 0).Page(3, 1).Execute().Count, "past the last hit");
        store.Dispose();
    }

    // ---- semantic search: semanticRatio 1 ----

    [TestMethod]
    public void SemanticSearch_PageIsTakenAfterTheTypeFilter() {
        var store = openSemanticStore();
        for (var i = 0; i < 10; i++) store.Insert(note("the same note"));
        store.Insert([recipe("Apple pie"), recipe("Baked apple"), recipe("Pear tart"), recipe("Plum cake")]);
        waitForIndexing(store);
        assertSemanticSearchIsFilteredBeforePaging(store);
        store.Dispose();
    }

    [TestMethod]
    public void SemanticSearch_PageIsTakenAfterTheTypeFilter_NativeVectorEngine() {
        var folder = Path.Combine(Path.GetTempPath(), "RelatudeDB_RankedSearchFilteringTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try {
            var settings = TestEngines.Settings(vector: IndexEngineTypes.IVS);
            settings.EnableSemanticIndexByDefault = true;
            var engineFolder = Path.Combine(folder, "indexes");
            Func<IndexEngines> engines = () => IndexEngines.Single(vectorId: TestEngines.VectorId, vector: new ISVEngine(engineFolder));
            var store = new NodeStore(DataStoreLocal.Open(datamodel(), settings, new IOProviderDisk(Path.Combine(folder, "db")), null, null, null, AIEngine.CreateDummy(), engines));
            for (var i = 0; i < 10; i++) store.Insert(note("the same note"));
            store.Insert([recipe("Apple pie"), recipe("Baked apple"), recipe("Pear tart"), recipe("Plum cake")]);
            waitForIndexing(store);
            assertSemanticSearchIsFilteredBeforePaging(store);
            store.Dispose();
        } finally {
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    static void assertSemanticSearchIsFilteredBeforePaging(NodeStore store) {
        // similarity 1 with all ten notes, so they hold the top of the shared vector index
        var search = semanticExtractOf<RankedNote>(store, _ => true);

        // -1 lets every recipe through; the notes above them must not take the page
        var page = store.Query<RankedRecipe>().Search(search, 1, -1).Page(0, 3).Execute();
        Assert.AreEqual(3, page.Count, "the page must be filled with recipes, not emptied by better ranked notes");
        Assert.AreEqual(4, page.TotalCount);
        CollectionAssert.AreEqual(titles(store.Query<RankedRecipe>().Search(search, 1, -1).Page(0, 100).Execute()).Take(3).ToArray(), titles(page));

        // only the notes are this similar: no recipe matches, and the notes are not counted
        var none = store.Query<RankedRecipe>().Search(search, 1, 0.9f).Execute();
        Assert.AreEqual(0, none.Count);
        Assert.AreEqual(0, none.TotalCount, "the total must not count nodes of another type");
        Assert.AreEqual(10, store.Query<RankedNote>().Search(search, 1, 0.9f).Execute().TotalCount);
    }

    [TestMethod]
    public void SemanticSearch_PageIsTakenAfterTheWhereFilter() {
        var store = openSemanticStore();
        // the unpublished recipes share one text, and the search is that text
        for (var i = 0; i < 6; i++) store.Insert(recipe("the same draft", published: false));
        store.Insert([recipe("Apple pie"), recipe("Baked apple"), recipe("Pear tart")]);
        waitForIndexing(store);
        var search = semanticExtractOf<RankedRecipe>(store, r => !r.Published);

        var page = store.Query<RankedRecipe>().Where(r => r.Published).Search(search, 1, -1).Page(0, 2).Execute();
        Assert.AreEqual(2, page.Count);
        Assert.AreEqual(3, page.TotalCount);
        Assert.IsTrue(titles(page).All(t => t != "the same draft"));
        store.Dispose();
    }

    // ---- the text the vector index embeds ----

    [TestMethod]
    public void SemanticExtract_LabelsEachValueWithItsPropertyName() {
        var store = openWordStore();
        var pie = recipe("Apple pie");
        var dessert = new RankedTag { Name = "Dessert" };
        store.Insert(pie);
        store.Insert(dessert);
        store.AddRelation(pie, r => r.Tags, dessert);
        store.Insert(note("Remember the cinnamon"));

        var recipeText = semanticExtractOf<RankedRecipe>(store, _ => true);
        var noteText = semanticExtractOf<RankedNote>(store, _ => true);
        // the labels were once the DisplayName flag of the property, "True:" and "False:"
        StringAssert.StartsWith(recipeText, "Title: Apple pie");
        StringAssert.Contains(recipeText, "Tags:");
        StringAssert.Contains(recipeText, "Dessert");
        StringAssert.StartsWith(noteText, "Text: Remember the cinnamon");
        foreach (var text in new[] { recipeText, noteText }) {
            Assert.IsFalse(text.Contains("True:") || text.Contains("False:"), "a label must name the property: " + text);
        }
        store.Dispose();
    }
}
