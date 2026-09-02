using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Nodes;
using Relatude.DB.Query;
using Relatude.Utils;

namespace Relatude.Querying;

#region string StartsWith / Contains test datamodel
[Node]
public class Doc {
    [InternalIdProperty]
    public int Id { get; set; }
    [StringProperty(Indexed = true)]
    public string Title { get; set; } = "";
    [StringProperty(Indexed = false)]
    public string Summary { get; set; } = "";
    [IntegerProperty(Indexed = true)]
    public int Rank { get; set; }
}
#endregion

[TestClass]
public class StringMatchTests {

    // titles chosen so prefixes overlap, share a common substring, differ in case, and include
    // an empty value plus one holding a character above the BMP
    static readonly string[] _titles = [
        "Alpha", "Alphabet", "Alphabetical", "Alpine", "alpha lower",
        "Beta", "Betamax", "BETA UPPER", "Bread",
        "Gamma", "Gamma ray", "delta", "Delta force", "",
        "Zeta￿", "Zeta￿Z", "Zeta\U00010000",
    ];

    static NodeStore OpenStore(out List<Doc> all, bool persistedIndexes = false) {
        var dm = new Datamodel();
        dm.Add<Doc>();
        var store = persistedIndexes
            ? new NodeStore(DataStoreLocal.Open(dm, new SettingsLocal() {
                ValueIndexes = [TestEngines.NativeValue], DefaultValueIndex = TestEngines.ValueId,
            }, null, null, null, null, null, () => DB.DataStores.Indexes.IndexEngines.Single(TestEngines.ValueId, new DB.DataStores.Indexes.KvStore.NativeKvIndexStore(null))))
            : new NodeStore(DataStoreLocal.Open(dm));
        var docs = new List<Doc>();
        for (var i = 0; i < _titles.Length; i++) {
            docs.Add(new Doc { Title = _titles[i], Summary = _titles[i], Rank = i });
        }
        store.Insert(docs);
        all = store.Query<Doc>().ToList();
        return store;
    }

    // the store must agree with compiled LINQ over the same predicate
    static void assertSame(NodeStore store, List<Doc> all, System.Linq.Expressions.Expression<Func<Doc, bool>> predicate, bool mustDiscriminate = true) {
        var fromStore = store.Query<Doc>().Where(predicate).Execute().Select(c => c.Id).OrderBy(i => i).ToList();
        var fromLinq = all.Where(predicate.Compile()).Select(c => c.Id).OrderBy(i => i).ToList();
        CollectionAssert.AreEqual(fromLinq, fromStore, "Store and LINQ disagree for: " + predicate);
        if (mustDiscriminate) // guard against a parse bug reducing the predicate to constant true/false
            Assert.IsTrue(fromLinq.Count > 0 && fromLinq.Count < all.Count, "Predicate does not discriminate (matched " + fromLinq.Count + " of " + all.Count + "): " + predicate);
    }

    [TestMethod]
    public void StartsWith_MatchesLinq() {
        foreach (var persistedIndexes in new[] { false, true }) {
            var store = OpenStore(out var all, persistedIndexes);
            assertSame(store, all, x => x.Title.StartsWith("Alpha"));    // prefix of longer titles
            assertSame(store, all, x => x.Title.StartsWith("Alphabet")); // prefix that is itself a title
            assertSame(store, all, x => x.Title.StartsWith("Al"));
            assertSame(store, all, x => x.Title.StartsWith("B"));
            assertSame(store, all, x => x.Title.StartsWith("Bread"));    // whole value
            // ordinal: case must matter, exactly as for string equality
            assertSame(store, all, x => x.Title.StartsWith("alpha"));
            assertSame(store, all, x => x.Title.StartsWith("BETA"));
            Assert.AreEqual(0, store.Query<Doc>().Where(x => x.Title.StartsWith("NoSuch")).Count());
            store.Dispose();
        }
    }

    [TestMethod]
    public void StartsWith_HandlesPrefixUpperBoundEdges() {
        foreach (var persistedIndexes in new[] { false, true }) {
            var store = OpenStore(out var all, persistedIndexes);
            // a prefix ending in char.MaxValue: the range bound has to skip past it, and the value
            // holding a character above the BMP must still be found
            assertSame(store, all, x => x.Title.StartsWith("Zeta"));
            assertSame(store, all, x => x.Title.StartsWith("Zeta￿"));
            // the empty prefix has no upper bound at all: every value starts with it
            assertSame(store, all, x => x.Title.StartsWith(""), mustDiscriminate: false);
            Assert.AreEqual(all.Count, store.Query<Doc>().Where(x => x.Title.StartsWith("")).Count());
            store.Dispose();
        }
    }

    [TestMethod]
    public void StringContains_MatchesLinq() {
        foreach (var persistedIndexes in new[] { false, true }) {
            var store = OpenStore(out var all, persistedIndexes);
            assertSame(store, all, x => x.Title.Contains("lph"));   // interior substring
            assertSame(store, all, x => x.Title.Contains("a"));
            assertSame(store, all, x => x.Title.Contains("Alpha")); // also a prefix
            assertSame(store, all, x => x.Title.Contains("ta"));    // suffix of some, interior of others
            assertSame(store, all, x => x.Title.Contains(" "));
            // ordinal: case must matter
            assertSame(store, all, x => x.Title.Contains("BETA"));
            Assert.AreEqual(0, store.Query<Doc>().Where(x => x.Title.Contains("nosuch")).Count());
            store.Dispose();
        }
    }

    [TestMethod]
    public void NonIndexedString_FallsBackToRowEvaluation() {
        var store = OpenStore(out var all);
        // Summary holds the same values as Title but is not indexed
        assertSame(store, all, x => x.Summary.StartsWith("Alpha"));
        assertSame(store, all, x => x.Summary.Contains("lph"));
        Assert.AreEqual(
            store.Query<Doc>().Where(x => x.Title.StartsWith("Beta")).Count(),
            store.Query<Doc>().Where(x => x.Summary.StartsWith("Beta")).Count(),
            "Indexed and non indexed strings must answer StartsWith identically");
        Assert.AreEqual(
            store.Query<Doc>().Where(x => x.Title.Contains("eta")).Count(),
            store.Query<Doc>().Where(x => x.Summary.Contains("eta")).Count(),
            "Indexed and non indexed strings must answer Contains identically");
        store.Dispose();
    }

    [TestMethod]
    public void StringMatching_CombinesWithOtherFilters() {
        foreach (var persistedIndexes in new[] { false, true }) {
            var store = OpenStore(out var all, persistedIndexes);
            assertSame(store, all, x => x.Title.StartsWith("Alpha") && x.Rank > 0);
            assertSame(store, all, x => x.Title.StartsWith("Alpha") || x.Title.StartsWith("Beta"));
            assertSame(store, all, x => x.Title.Contains("eta") && x.Rank < 10);
            assertSame(store, all, x => x.Title.StartsWith("Alpha") && x.Title.Contains("bet"));
            assertSame(store, all, x => !x.Title.StartsWith("Alpha"));
            assertSame(store, all, x => !x.Title.Contains("a"));
            // an indexed and a non indexed string together forces a split between index and row filtering
            assertSame(store, all, x => x.Title.StartsWith("Alpha") && x.Summary.Contains("bet"));
            store.Dispose();
        }
    }

    [TestMethod]
    public void StringMatching_WorksFromQueryString() {
        var store = OpenStore(out var all);
        Assert.AreEqual(all.Count(x => x.Title.StartsWith("Alpha")),
            store.Query<Doc>().Where("x => x.Title.StartsWith(\"Alpha\")").Count());
        Assert.AreEqual(all.Count(x => x.Title.Contains("lph")),
            store.Query<Doc>().Where("x => x.Title.Contains(\"lph\")").Count());
        store.Dispose();
    }

    [TestMethod]
    public void StringMatching_AcceptsCharArgument() {
        var store = OpenStore(out var all);
        assertSame(store, all, x => x.Title.StartsWith('A'));
        assertSame(store, all, x => x.Title.Contains('m'));
        store.Dispose();
    }

    [TestMethod]
    public void StringMatching_ReflectsUpdatesAndDeletes() {
        foreach (var persistedIndexes in new[] { false, true }) {
            var store = OpenStore(out var all, persistedIndexes);
            var renamed = all.Where(x => x.Title.StartsWith("Alpha")).ToList();
            foreach (var doc in renamed) {
                store.UpdateProperty<Doc, string>(doc.Id, x => x.Title, "Omega " + doc.Id);
                doc.Title = "Omega " + doc.Id;
            }
            store.Delete(all[0].Id);
            var remaining = all.Where(x => x.Id != all[0].Id).ToList();
            Assert.AreEqual(remaining.Count(x => x.Title.StartsWith("Alpha")),
                store.Query<Doc>().Where(x => x.Title.StartsWith("Alpha")).Count(), "persistedIndexes: " + persistedIndexes);
            Assert.AreEqual(remaining.Count(x => x.Title.StartsWith("Omega")),
                store.Query<Doc>().Where(x => x.Title.StartsWith("Omega")).Count(), "persistedIndexes: " + persistedIndexes);
            Assert.AreEqual(remaining.Count(x => x.Title.Contains("mega")),
                store.Query<Doc>().Where(x => x.Title.Contains("mega")).Count(), "persistedIndexes: " + persistedIndexes);
            store.Dispose();
        }
    }

    [TestMethod]
    public void StringMatching_SurvivesRestart() {
        foreach (var persistedIndexes in new[] { false, true }) {
            var dir = Path.Combine(Path.GetTempPath(), "relatude-stringmatch-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {
                var store = openStoreOnDisk(dir, persistedIndexes);
                var docs = new List<Doc>();
                for (var i = 0; i < _titles.Length; i++) docs.Add(new Doc { Title = _titles[i], Summary = _titles[i], Rank = i });
                store.Insert(docs);
                var truth = store.Query<Doc>().ToList();
                store.Dispose();

                store = openStoreOnDisk(dir, persistedIndexes);
                Assert.AreEqual(truth.Count(x => x.Title.StartsWith("Alpha")),
                    store.Query<Doc>().Where(x => x.Title.StartsWith("Alpha")).Count(), "persistedIndexes: " + persistedIndexes);
                Assert.AreEqual(truth.Count(x => x.Title.Contains("lph")),
                    store.Query<Doc>().Where(x => x.Title.Contains("lph")).Count(), "persistedIndexes: " + persistedIndexes);
                store.Dispose();
            } finally {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }

    [TestMethod]
    public void StringMatching_WithNonOrdinalComparison_Throws() {
        var store = OpenStore(out _);
        // matching is always ordinal, so an explicit comparison must be rejected rather than ignored
        var ex = Assert.ThrowsException<NotSupportedException>(
            () => store.Query<Doc>().Where(x => x.Title.StartsWith("alpha", StringComparison.OrdinalIgnoreCase)).Count());
        StringAssert.Contains(ex.Message, "Ordinal");
        Assert.ThrowsException<NotSupportedException>(
            () => store.Query<Doc>().Where(x => x.Title.Contains("ALPHA", StringComparison.OrdinalIgnoreCase)).Count());
        // the redundant but honest form is accepted
        Assert.AreEqual(store.Query<Doc>().Where(x => x.Title.StartsWith("Alpha")).Count(),
            store.Query<Doc>().Where(x => x.Title.StartsWith("Alpha", StringComparison.Ordinal)).Count());
        store.Dispose();
    }

    [TestMethod]
    public void StringMatching_OnUnsupportedPropertyType_Throws() {
        var store = OpenStore(out _);
        // Rank is an int: neither a string nor an array, so this must fail loudly
        var ex = Assert.ThrowsException<NotSupportedException>(
            () => store.Query<Doc>().Where("x => x.Rank.Contains(\"1\")").Count());
        StringAssert.Contains(ex.Message, "string");
        Assert.ThrowsException<NotSupportedException>(
            () => store.Query<Doc>().Where("x => x.Rank.StartsWith(\"1\")").Count());
        store.Dispose();
    }

    static NodeStore openStoreOnDisk(string dir, bool persistedIndexes) {
        var dm = new Datamodel();
        dm.Add<Doc>();
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
