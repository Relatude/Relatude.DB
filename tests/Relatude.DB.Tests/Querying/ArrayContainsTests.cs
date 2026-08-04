using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Nodes;
using Relatude.DB.Query;
using Relatude.Utils;

namespace Relatude.Querying;

#region array contains test datamodel
[Node]
public class TaggedItem {
    [InternalIdProperty]
    public int Id { get; set; }
    [StringProperty(Indexed = true)]
    public string Name { get; set; } = "";
    [StringArrayProperty(Indexed = true)]
    public string[] Tags { get; set; } = [];
    [StringArrayProperty(Indexed = false)]
    public string[] Notes { get; set; } = [];
    [EnumArrayProperty(Indexed = true)]
    public Sizes[] Options { get; set; } = [];
    [GuidArrayProperty(Indexed = true)]
    public Guid[] TagIds { get; set; } = [];
    [FloatArrayProperty]
    public float[] Scores { get; set; } = [];
    [ByteArrayProperty]
    public byte[] Flags { get; set; } = [];
}
#endregion

[TestClass]
public class ArrayContainsTests {

    static readonly Guid _idRed = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly Guid _idBlue = Guid.Parse("22222222-2222-2222-2222-222222222222");
    static readonly Guid _idGreen = Guid.Parse("33333333-3333-3333-3333-333333333333");
    static readonly Guid _idUnused = Guid.Parse("99999999-9999-9999-9999-999999999999");

    // every 5th item carries an in-array duplicate; evens one tag; odds two
    static (string[] tags, Guid[] ids) tagsFor(int i) =>
        i % 5 == 0 ? (["red", "red", "blue"], new[] { _idRed, _idRed, _idBlue })
        : (i % 2 == 0 ? (["red"], new[] { _idRed }) : (["green", "blue"], new[] { _idGreen, _idBlue }));

    static Sizes[] optionsFor(int i) =>
        i % 5 == 0 ? [Sizes.Small, Sizes.Small, Sizes.Large]
        : (i % 2 == 0 ? [Sizes.Small] : [Sizes.Medium, Sizes.Large]);

    static NodeStore OpenStore(out List<TaggedItem> all, bool persistedIndexes = false) {
        var dm = new Datamodel();
        dm.Add<TaggedItem>();
        var store = persistedIndexes
            ? new NodeStore(DataStoreLocal.Open(dm, new SettingsLocal() {
                UsePersistedValueIndexesByDefault = true,
                PersistedValueIndexEngine = PersistedValueIndexEngine.Native,
            }, null, null, null, null, null, () => new DB.DataStores.Indexes.KvStore.NativeKvIndexStore(null, null)))
            : new NodeStore(DataStoreLocal.Open(dm));
        var items = new List<TaggedItem>();
        for (var i = 1; i <= 30; i++) {
            var (tags, ids) = tagsFor(i);
            items.Add(new TaggedItem {
                Name = "Item " + i,
                Tags = tags,
                Notes = tags, // same content, but on a non indexed property: exercises row evaluation
                Options = optionsFor(i),
                TagIds = ids,
                Scores = i % 2 == 0 ? [1.5f, 2.5f] : [3.5f],
                Flags = i % 2 == 0 ? [1, 2] : [3],
            });
        }
        store.Insert(items);
        all = store.Query<TaggedItem>().ToList();
        return store;
    }

    // the store must agree with compiled LINQ over the same predicate, and the predicate must
    // actually discriminate (guards against a parse bug folding it to constant true/false)
    static void assertSameItems(NodeStore store, List<TaggedItem> all, System.Linq.Expressions.Expression<Func<TaggedItem, bool>> predicate) {
        var fromStore = store.Query<TaggedItem>().Where(predicate).Execute().Select(c => c.Id).OrderBy(i => i).ToList();
        var fromLinq = all.Where(predicate.Compile()).Select(c => c.Id).OrderBy(i => i).ToList();
        CollectionAssert.AreEqual(fromLinq, fromStore, "Store and LINQ disagree for: " + predicate);
        Assert.IsTrue(fromLinq.Count > 0 && fromLinq.Count < all.Count, "Predicate does not discriminate (matched " + fromLinq.Count + " of " + all.Count + "): " + predicate);
    }

    [TestMethod]
    public void StringArray_Contains_MatchesLinq() {
        foreach (var persistedIndexes in new[] { false, true }) {
            var store = OpenStore(out var all, persistedIndexes);
            assertSameItems(store, all, x => x.Tags.Contains("red"));
            assertSameItems(store, all, x => x.Tags.Contains("blue"));
            assertSameItems(store, all, x => x.Tags.Contains("green"));
            // duplicates inside one array must not duplicate the node in the result
            var reds = store.Query<TaggedItem>().Where(x => x.Tags.Contains("red")).Execute().Select(x => x.Id).ToList();
            Assert.AreEqual(reds.Distinct().Count(), reds.Count, "persistedIndexes: " + persistedIndexes);
            // a value that is not in the index matches nothing
            Assert.AreEqual(0, store.Query<TaggedItem>().Where(x => x.Tags.Contains("nosuchtag")).Count());
            store.Dispose();
        }
    }

    [TestMethod]
    public void EnumArray_Contains_MatchesLinq() {
        var store = OpenStore(out var all);
        assertSameItems(store, all, x => x.Options.Contains(Sizes.Small));
        assertSameItems(store, all, x => x.Options.Contains(Sizes.Medium));
        assertSameItems(store, all, x => x.Options.Contains(Sizes.Large));
        // the underlying int is equivalent to the enum member
        Assert.AreEqual(
            store.Query<TaggedItem>().Where(x => x.Options.Contains(Sizes.Large)).Count(),
            store.Query<TaggedItem>().Where("x => x.Options.Contains(2)").Count());
        store.Dispose();
    }

    [TestMethod]
    public void GuidArray_Contains_MatchesLinq() {
        var store = OpenStore(out var all);
        assertSameItems(store, all, x => x.TagIds.Contains(_idRed));
        assertSameItems(store, all, x => x.TagIds.Contains(_idBlue));
        assertSameItems(store, all, x => x.TagIds.Contains(_idGreen));
        Assert.AreEqual(0, store.Query<TaggedItem>().Where(x => x.TagIds.Contains(_idUnused)).Count());
        // a guid written as a string literal in a query string resolves to the same set
        Assert.AreEqual(
            store.Query<TaggedItem>().Where(x => x.TagIds.Contains(_idRed)).Count(),
            store.Query<TaggedItem>().Where("x => x.TagIds.Contains(\"" + _idRed + "\")").Count());
        store.Dispose();
    }

    [TestMethod]
    public void NonIndexedStringArray_Contains_FallsBackToRowEvaluation() {
        var store = OpenStore(out var all);
        // Notes holds the same values as Tags but is not indexed, so this can only be row evaluated
        assertSameItems(store, all, x => x.Notes.Contains("red"));
        Assert.AreEqual(
            store.Query<TaggedItem>().Where(x => x.Tags.Contains("green")).Count(),
            store.Query<TaggedItem>().Where(x => x.Notes.Contains("green")).Count(),
            "Indexed and non indexed arrays must answer Contains identically");
        Assert.AreEqual(0, store.Query<TaggedItem>().Where(x => x.Notes.Contains("nosuchtag")).Count());
        store.Dispose();
    }

    [TestMethod]
    public void FloatAndByteArray_Contains_MatchesLinq() {
        var store = OpenStore(out var all);
        assertSameItems(store, all, x => x.Scores.Contains(2.5f));
        assertSameItems(store, all, x => x.Scores.Contains(3.5f));
        assertSameItems(store, all, x => x.Flags.Contains((byte)2));
        assertSameItems(store, all, x => x.Flags.Contains((byte)3));
        // a value no array holds
        Assert.AreEqual(0, store.Query<TaggedItem>().Where(x => x.Scores.Contains(9.5f)).Count());
        store.Dispose();
    }

    [TestMethod]
    public void Contains_CombinesWithOtherFilters() {
        foreach (var persistedIndexes in new[] { false, true }) {
            var store = OpenStore(out var all, persistedIndexes);
            assertSameItems(store, all, x => x.Tags.Contains("red") && x.Options.Contains(Sizes.Large));
            assertSameItems(store, all, x => x.Tags.Contains("green") || x.Options.Contains(Sizes.Large));
            assertSameItems(store, all, x => x.Tags.Contains("red") && x.TagIds.Contains(_idBlue));
            assertSameItems(store, all, x => !x.Tags.Contains("red"));
            assertSameItems(store, all, x => x.Tags.Contains("red") && x.Name != "Item 2");
            // mixing an indexed and a non indexed array forces a split between index and row filtering
            assertSameItems(store, all, x => x.Tags.Contains("red") && x.Notes.Contains("blue"));
            store.Dispose();
        }
    }

    [TestMethod]
    public void Contains_CountsMatchFacetSelection() {
        var store = OpenStore(out var all);
        // Contains and a single value facet selection are two routes to the same set
        var viaContains = store.Query<TaggedItem>().Where(x => x.Tags.Contains("red")).Count();
        var viaFacet = store.Query<TaggedItem>().Facets().AddValueFacet("Tags").SetFacetValue("Tags", "red").Execute().Count();
        Assert.AreEqual(viaFacet, viaContains);
        Assert.AreEqual(all.Count(x => x.Tags.Contains("red")), viaContains);
        store.Dispose();
    }

    [TestMethod]
    public void Contains_WorksAfterUpdatesAndDeletes() {
        foreach (var persistedIndexes in new[] { false, true }) {
            var store = OpenStore(out var all, persistedIndexes);
            var victims = all.Take(8).ToList();
            string[][] combos = [["yellow"], ["yellow", "red"], [], ["red", "red"]];
            for (var i = 0; i < victims.Count; i++) {
                var combo = combos[i % combos.Length];
                store.UpdateProperty<TaggedItem, string[]>(victims[i].Id, x => x.Tags, combo);
                victims[i].Tags = combo;
            }
            store.Delete(victims[0].Id);
            var remaining = all.Where(x => x.Id != victims[0].Id).ToList();
            foreach (var tag in new[] { "red", "blue", "green", "yellow" }) {
                var expected = remaining.Count(x => x.Tags.Contains(tag));
                var actual = store.Query<TaggedItem>().Where("x => x.Tags.Contains(\"" + tag + "\")").Count();
                Assert.AreEqual(expected, actual, "Wrong count for " + tag + ", persistedIndexes: " + persistedIndexes);
            }
            store.Dispose();
        }
    }

    [TestMethod]
    public void Contains_SurvivesRestart() {
        foreach (var persistedIndexes in new[] { false, true }) {
            var dir = Path.Combine(Path.GetTempPath(), "relatude-arraycontains-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {
                var store = openStoreOnDisk(dir, persistedIndexes);
                var items = new List<TaggedItem>();
                for (var i = 1; i <= 20; i++) {
                    var (tags, ids) = tagsFor(i);
                    items.Add(new TaggedItem { Name = "Item " + i, Tags = tags, TagIds = ids, Options = optionsFor(i) });
                }
                store.Insert(items);
                var truth = store.Query<TaggedItem>().ToList();
                store.Dispose();

                store = openStoreOnDisk(dir, persistedIndexes);
                foreach (var tag in new[] { "red", "blue", "green" }) {
                    var expected = truth.Count(x => x.Tags.Contains(tag));
                    var actual = store.Query<TaggedItem>().Where("x => x.Tags.Contains(\"" + tag + "\")").Count();
                    Assert.AreEqual(expected, actual, "Wrong count for " + tag + " after restart, persistedIndexes: " + persistedIndexes);
                }
                Assert.AreEqual(truth.Count(x => x.TagIds.Contains(_idRed)),
                    store.Query<TaggedItem>().Where(x => x.TagIds.Contains(_idRed)).Count(), "persistedIndexes: " + persistedIndexes);
                Assert.AreEqual(truth.Count(x => x.Options.Contains(Sizes.Large)),
                    store.Query<TaggedItem>().Where(x => x.Options.Contains(Sizes.Large)).Count(), "persistedIndexes: " + persistedIndexes);
                store.Dispose();
            } finally {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }

    [TestMethod]
    public void Contains_OnNonArrayProperty_Throws() {
        var store = OpenStore(out _);
        // Name is a string, not an array: this must fail loudly rather than silently match nothing
        var ex = Assert.ThrowsException<NotSupportedException>(
            () => store.Query<TaggedItem>().Where(x => x.Name.Contains("Item")).Count());
        StringAssert.Contains(ex.Message, "array");
        store.Dispose();
    }

    [TestMethod]
    public void Contains_OnUnknownPropertyPath_Throws() {
        var store = OpenStore(out _);
        var ex = Assert.ThrowsException<Exception>(
            () => store.Query<TaggedItem>().Where("x => x.NoSuchProperty.Contains(\"red\")").Count());
        StringAssert.Contains(ex.Message, "NoSuchProperty");
        store.Dispose();
    }

    static NodeStore openStoreOnDisk(string dir, bool persistedIndexes) {
        var dm = new Datamodel();
        dm.Add<TaggedItem>();
        if (persistedIndexes) {
            var settings = new SettingsLocal {
                UsePersistedValueIndexesByDefault = true,
                PersistedValueIndexEngine = PersistedValueIndexEngine.Native,
            };
            return new NodeStore(DataStoreLocal.Open(dm, settings, new Relatude.DB.IO.IOProviderDisk(dir), null, null, null, null,
                () => new DB.DataStores.Indexes.KvStore.NativeKvIndexStore(dir, null)));
        }
        return new NodeStore(DataStoreLocal.Open(dm, new SettingsLocal(), new Relatude.DB.IO.IOProviderDisk(dir), null, null, null, null, null));
    }
}
