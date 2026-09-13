using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.Query.Data;
using Relatude.DB.Query.Parsing.Expressions;
using Relatude.DB.Query.Parsing.Tokens;

namespace Relatude.Querying;

#region terminal test datamodel
[Node(TextIndex = BoolValue.True, InstantTextIndexing = BoolValue.True)]
public class TerminalItem {
    [InternalIdProperty]
    public int Id { get; set; }
    [StringProperty(Indexed = true, ExcludeFromTextIndex = true)]
    public string Category { get; set; } = "";
    [IntegerProperty(Indexed = true)]
    public int Size { get; set; }
    [GeoCoordinateProperty(Indexed = true)]
    public GeoCoordinate Location { get; set; }
    [StringProperty(IndexedByWords = true)]
    public string Body { get; set; } = "";
}
#endregion

/// <summary>
/// The terminals that answer from the ids of a result and its indexes - Buckets(), Coordinates(),
/// Words(), and SelectId() - checked against the nodes themselves. What they promise beyond the right
/// answer: no node is read to give it, a facet clause in front of them contributes its selection
/// only, and what they render as a string parses back to the same clause.
/// </summary>
[TestClass]
public class IdTerminalTests {
    static readonly string[] _categories = ["alpha", "beta", "gamma"];
    const int _count = 90;

    static NodeStore open(out Dictionary<int, TerminalItem> byId) {
        var dm = new Datamodel();
        dm.Add<TerminalItem>();
        var store = new NodeStore(DataStoreLocal.Open(dm, new SettingsLocal(), new IOProviderMemory()));
        var items = new List<TerminalItem>();
        for (var i = 0; i < _count; i++) {
            items.Add(new TerminalItem {
                Category = _categories[i % 3],
                Size = i,
                Location = i % 5 == 0 ? GeoCoordinate.Empty : new GeoCoordinate(10 + i * 0.1, 20 + i * 0.2),
                Body = "common " + _categories[i % 3] + (i % 10 == 0 ? " rare" : "") + " stopper",
            });
        }
        // written to the log at once, so the node cache can be emptied below: a node not yet in the log stays in it
        store.Insert(items, flushToDisk: true);
        byId = store.Query<TerminalItem>().Execute().ToDictionary(t => t.Id);
        Assert.AreEqual(_count, byId.Count);
        return store;
    }
    static string q(string clauses) => nameof(TerminalItem) + clauses;
    static int[] idsOf(ValueBucket bucket) => bucket.Ids.ToArray();
    static void emptyNodeCache(NodeStore store) {
        store.Datastore.Maintenance(MaintenanceAction.FlushDisk);
        store.Datastore.Maintenance(MaintenanceAction.ClearCache);
        Assert.AreEqual(0, store.Datastore.PeekCounters().NodeCacheCount, "the node cache is empty to begin with");
    }

    [TestMethod]
    public void Buckets_EveryNodeInOneBucketPerProperty() {
        var store = open(out var byId);
        try {
            var result = store.Datastore.Query(q(".Page(0, 1000).Buckets(\"TerminalItem.Category\").AddRangeBucket(\"TerminalItem.Size\")"), []) as BucketsQueryResultData;
            Assert.IsNotNull(result);
            Assert.AreEqual(_count, result.Ids.Length);
            Assert.AreEqual(_count, result.TotalCount);
            Assert.IsNull(result.Order, "no SortBy: no order");
            Assert.AreEqual(2, result.Properties.Length);

            var category = result.Properties[0];
            Assert.IsFalse(category.IsRange);
            Assert.AreEqual(3, category.Buckets.Count, "one bucket per category, none empty");
            CollectionAssert.AreEqual(_categories.OrderBy(c => c).ToArray(), category.Buckets.Select(b => (string)b.Value.Value!).ToArray(), "in value order");
            foreach (var bucket in category.Buckets) {
                var ids = idsOf(bucket);
                Assert.AreEqual(bucket.Count, ids.Length);
                Assert.IsTrue(ids.All(id => byId[id].Category == (string)bucket.Value.Value!), "every id in the bucket of its own category");
            }
            CollectionAssert.AreEquivalent(result.Ids, category.Buckets.SelectMany(idsOf).ToArray(), "the buckets partition the result");

            var size = result.Properties[1];
            Assert.IsTrue(size.IsRange, "asked for ranges");
            Assert.IsTrue(size.Buckets.Count >= 2, "ninety distinct sizes make several ranges");
            CollectionAssert.AreEquivalent(result.Ids, size.Buckets.SelectMany(idsOf).ToArray(), "the ranges together hold every node");
        } finally {
            store.Dispose();
        }
    }

    [TestMethod]
    public void Buckets_SortByOrdersThePositionsByTheIndex() {
        var store = open(out var byId);
        try {
            var result = (BucketsQueryResultData)store.Datastore.Query(q(".Page(0, 1000).Buckets().SortBy(\"TerminalItem.Size\", true)"), [])!;
            Assert.IsNotNull(result.Order);
            CollectionAssert.AreEquivalent(Enumerable.Range(0, _count).ToArray(), result.Order, "a permutation of the positions");
            var sizes = result.Order.Select(at => byId[result.Ids[at]].Size).ToArray();
            CollectionAssert.AreEqual(sizes.OrderByDescending(s => s).ToArray(), sizes, "largest first");

            var ascending = (BucketsQueryResultData)store.Datastore.Query(q(".Page(0, 1000).Buckets().SortBy(\"TerminalItem.Size\")"), [])!;
            Assert.AreEqual(0, byId[ascending.Ids[ascending.Order![0]]].Size, "ascending puts the smallest first");
        } finally {
            store.Dispose();
        }
    }

    [TestMethod]
    public void Terminals_FollowAFacetSelectionWithoutCountingIt() {
        var store = open(out var byId);
        try {
            var selection = ".Facets().SetFacetValue(\"TerminalItem.Category\", \"alpha\")";
            var alphas = byId.Values.Where(t => t.Category == "alpha").ToArray();

            var buckets = (BucketsQueryResultData)store.Datastore.Query(q(selection + ".Page(0, 1000).Buckets().AddValueBucket(\"TerminalItem.Size\")"), [])!;
            Assert.AreEqual(alphas.Length, buckets.Ids.Length);
            Assert.AreEqual(alphas.Length, buckets.TotalCount);
            Assert.IsTrue(buckets.Ids.All(id => byId[id].Category == "alpha"), "the selection, nothing else");
            Assert.AreEqual(alphas.Length, buckets.Properties[0].Buckets.Count, "every size its own bucket");

            var paged = (BucketsQueryResultData)store.Datastore.Query(q(selection + ".Page(0, 10).Buckets()"), [])!;
            Assert.AreEqual(10, paged.Ids.Length, "a page on the facet clause pages the selection");
            Assert.AreEqual(alphas.Length, paged.TotalCount);

            var ids = store.Datastore.Query(q(selection + ".SelectId()"), []) as ICollectionData;
            Assert.IsNotNull(ids);
            Assert.AreEqual(alphas.Length, ids.Count, "SelectId on a facet clause is the ids of its selection");

            var words = (WordsQueryResultData)store.Datastore.Query(q(selection + ".Words(\"TerminalItem.Body\")"), [])!;
            Assert.AreEqual(alphas.Length, words.TotalCount);
            Assert.AreEqual(alphas.Length, words.Words.Words.Single(w => w.Word == "alpha").Documents);
            Assert.IsFalse(words.Words.Words.Any(w => w.Word == "beta"), "no word of a node outside the selection");
        } finally {
            store.Dispose();
        }
    }

    [TestMethod]
    public void Coordinates_TheLocatedNodesWithTheirGroups() {
        var store = open(out var byId);
        try {
            var result = (CoordinatesQueryResultData)store.Datastore.Query(q(".Page(0, 1000).Coordinates(\"TerminalItem.Location\").AddValueBucket(\"TerminalItem.Category\")"), [])!;
            var located = byId.Values.Where(t => !t.Location.IsEmpty).ToArray();
            Assert.AreEqual(located.Length, result.Count);
            Assert.AreEqual(located.Length, result.Ids.Length);
            Assert.AreEqual(_count, result.Read, "how many nodes were looked at");
            Assert.AreEqual(_count, result.TotalCount);
            for (var i = 0; i < result.Ids.Length; i++) {
                var truth = byId[result.Ids[i]].Location;
                Assert.AreEqual(truth.Latitude, result.Coordinates[i].Latitude, 1e-6);
                Assert.AreEqual(truth.Longitude, result.Coordinates[i].Longitude, 1e-6);
            }
            var category = result.Properties.Single();
            Assert.AreEqual(3, category.Buckets.Count);
            Assert.AreEqual(_count, category.Buckets.Sum(b => b.Count), "the buckets hold every node of the collection, located or not");
            var locatedIds = result.Ids.ToHashSet();
            foreach (var bucket in category.Buckets)
                Assert.IsTrue(idsOf(bucket).Where(locatedIds.Contains).All(id => byId[id].Category == (string)bucket.Value.Value!));
        } finally {
            store.Dispose();
        }
    }

    [TestMethod]
    public void Words_CountsOutOfTheWordIndex() {
        var store = open(out _);
        try {
            var result = (WordsQueryResultData)store.Datastore.Query(q(".Words(\"TerminalItem.Body\", 10).IgnoreWords(\"stopper\")"), [])!;
            Assert.AreEqual(_count, result.TotalCount);
            var words = result.Words.Words.ToDictionary(w => w.Word);
            Assert.AreEqual(_count, words["common"].Documents);
            Assert.AreEqual(_count / 10, words["rare"].Documents);
            Assert.AreEqual(_count / 3, words["alpha"].Documents);
            Assert.IsFalse(words.ContainsKey("stopper"), "ignored");
            Assert.IsTrue(result.Count <= 10);
            Assert.AreEqual("common", result.Words.Words[0].Word, "commonest first");
        } finally {
            store.Dispose();
        }
    }

    /// <summary>
    /// The point of the terminals: a collection of nodes is read in full before a query returns it,
    /// which for a type of a few hundred thousand nodes is the whole cost of a picture of it. A
    /// terminal answers from the ids and the indexes, so nothing enters the node cache.
    /// </summary>
    [TestMethod]
    public void Terminals_ReadNoNode() {
        var store = open(out _);
        try {
            emptyNodeCache(store);
            store.Datastore.Query(q(".Page(0, 1000).Buckets(\"TerminalItem.Category\").AddRangeBucket(\"TerminalItem.Size\").SortBy(\"TerminalItem.Size\")"), []);
            store.Datastore.Query(q(".Page(0, 1000).Coordinates(\"TerminalItem.Location\").AddValueBucket(\"TerminalItem.Category\")"), []);
            store.Datastore.Query(q(".Words(\"TerminalItem.Body\")"), []);
            store.Datastore.Query(q(".Facets().SetFacetValue(\"TerminalItem.Category\", \"alpha\").Page(0, 1000).Buckets(\"TerminalItem.Size\")"), []);
            store.Datastore.Query(q(".SelectId()"), []);
            Assert.AreEqual(0, store.Datastore.PeekCounters().NodeCacheCount, "the terminals answer from the indexes alone");

            store.Datastore.Query(q(".Page(0, 1000)"), []);
            Assert.AreEqual(_count, store.Datastore.PeekCounters().NodeCacheCount, "where a collection of nodes is read in full before it is returned");
        } finally {
            store.Dispose();
        }
    }

    [TestMethod]
    public void Terminals_RenderToTheSameClause() {
        var store = open(out _);
        try {
            var dm = store.Datastore.Datamodel;
            string[] queries = [
                q(".Page(0, 1000).Buckets(\"TerminalItem.Category\").AddRangeBucket(\"TerminalItem.Size\").SetBucketOptions(10, false).SortBy(\"TerminalItem.Size\", true)"),
                q(".Page(0, 1000).Coordinates(\"TerminalItem.Location\").AddValueBucket(\"TerminalItem.Category\")"),
                q(".Words(\"TerminalItem.Body\", 10, 2).IgnoreWords(\"stopper\", \"common\")"),
            ];
            foreach (var query in queries) {
                var text = ExpressionTreeBuilder.Build(TokenParser.Parse(query, []), dm).ToString();
                var again = ExpressionTreeBuilder.Build(TokenParser.Parse(text!, []), dm).ToString();
                Assert.AreEqual(text, again, query);
                // and the rendering is the same query: the same answer
                var a = (ICollectionData)store.Datastore.Query(query, [])!;
                var b = (ICollectionData)store.Datastore.Query(text!, [])!;
                Assert.AreEqual(a.Count, b.Count, query);
                Assert.AreEqual(a.TotalCount, b.TotalCount, query);
            }
        } finally {
            store.Dispose();
        }
    }
}
