using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Indexes.KvStore;
using Relatude.DB.Datastores.Indexes.BTreeIndex;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.Query;
using Relatude.Utils;

namespace Relatude.Indexes;

#region large array test datamodel
[Node]
public class BulkTagged {
    [InternalIdProperty]
    public int Id { get; set; }
    [StringProperty(Indexed = true)]
    public string Name { get; set; } = "";
    [StringArrayProperty(Indexed = true)]
    public string[] Tags { get; set; } = [];
    [GuidArrayProperty(Indexed = true)]
    public Guid[] TagIds { get; set; } = [];
    [EnumArrayProperty(Indexed = true)]
    public Sizes[] Options { get; set; } = [];
}
#endregion

/// <summary>
/// The persisted array indexes of the native KV engine, which pack a node's whole array into one
/// binary value. They live on the engine's hash layout, so a packed array is not bound by the page
/// size the way a sorted index's values are: these tests cover arrays far past that old ~1 KB cap
/// (which used to throw on insert), and the migration of a store still holding the sorted layout.
/// </summary>
[TestClass]
public class KvArrayIndexLayoutTests {

    // Well past one page even before the per-element overhead: ~19 KB of tags, 6.4 KB of guids and
    // 8 KB of enum values per node, so every array spans an overflow chain of several pages.
    const int _tagCount = 400;
    const int _guidCount = 400;
    const int _optionCount = 2000;

    static string tag(int item, int i) => $"tag-{item}-{i}-" + new string('x', 30);
    static string[] tagsFor(int item) => Enumerable.Range(0, _tagCount).Select(i => tag(item, i)).ToArray();
    static Guid[] guidsFor(int item) => Enumerable.Range(0, _guidCount).Select(i => guid(item, i)).ToArray();
    static Guid guid(int item, int i) => new($"{item:x8}-0000-0000-0000-{i:x12}");
    static Sizes[] optionsFor(int item) => Enumerable.Range(0, _optionCount).Select(i => (Sizes)((item + i) % 3)).ToArray();

    static string tempDir() {
        var dir = Path.Combine(Path.GetTempPath(), "RelatudeDB_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static Datamodel datamodel() {
        var dm = new Datamodel();
        dm.Add<BulkTagged>();
        return dm;
    }

    static NodeStore openStore(string dir) {
        var settings = new SettingsLocal {
            ValueIndexes = [TestEngines.NativeValue], DefaultValueIndex = TestEngines.ValueId,
        };
        return new NodeStore(DataStoreLocal.Open(datamodel(), settings, new IOProviderDisk(dir), null, null, null, null,
            () => IndexEngines.Single(TestEngines.ValueId, new NativeKvIndexStore(dir))));
    }

    static void insertItems(NodeStore store, int count) {
        var items = new List<BulkTagged>();
        for (var item = 1; item <= count; item++) {
            items.Add(new BulkTagged {
                Name = "Item " + item,
                Tags = tagsFor(item),
                TagIds = guidsFor(item),
                Options = optionsFor(item),
            });
        }
        store.Insert(items);
    }

    /// <summary>Every array must be queryable through its index, for each of the three element types.</summary>
    static void assertAllQueryable(NodeStore store, int count) {
        Assert.AreEqual(count, store.Query<BulkTagged>().Count());
        for (var item = 1; item <= count; item++) {
            // an element from the middle of each array: only a fully round-tripped value matches
            Assert.AreEqual(1, store.Query<BulkTagged>().Where(x => x.Tags.Contains(tag(item, _tagCount / 2))).Count(),
                $"item {item}: string array element not indexed");
            Assert.AreEqual(1, store.Query<BulkTagged>().Where(x => x.TagIds.Contains(guid(item, _guidCount / 2))).Count(),
                $"item {item}: guid array element not indexed");
        }
        // the enum array holds every member across all items
        Assert.AreEqual(count, store.Query<BulkTagged>().Where(x => x.Options.Contains(Sizes.Medium)).Count());
        Assert.AreEqual(0, store.Query<BulkTagged>().Where(x => x.Tags.Contains("nosuchtag")).Count());
        Assert.AreEqual(0, store.Query<BulkTagged>().Where(x => x.TagIds.Contains(Guid.Empty)).Count());

        // and the arrays themselves come back whole, not just their indexed elements
        foreach (var loaded in store.Query<BulkTagged>().ToList()) {
            var item = int.Parse(loaded.Name.Split(' ')[1]);
            CollectionAssert.AreEqual(tagsFor(item), loaded.Tags, $"item {item}: string array did not round-trip");
            CollectionAssert.AreEqual(guidsFor(item), loaded.TagIds, $"item {item}: guid array did not round-trip");
            CollectionAssert.AreEqual(optionsFor(item), loaded.Options, $"item {item}: enum array did not round-trip");
        }
    }

    [TestMethod]
    public void LargeArrays_PersistAndSurviveRestart() {
        const int count = 12;
        var dir = tempDir();
        try {
            using (var store = openStore(dir)) {
                insertItems(store, count);
                assertAllQueryable(store, count);
            }
            using (var store = openStore(dir)) {
                // read back from the index file, not from the mirror built during the insert
                assertAllQueryable(store, count);

                // updates and deletes must replace and release the packed values, not corrupt them
                var victim = store.Query<BulkTagged>().Where(x => x.Name == "Item 3").Execute().Single();
                store.UpdateProperty<BulkTagged, string[]>(victim.Id, x => x.Tags, ["small", "again"]);
                var deleted = store.Query<BulkTagged>().Where(x => x.Name == "Item 4").Execute().Single();
                store.Delete(deleted.Id);

                Assert.AreEqual(0, store.Query<BulkTagged>().Where(x => x.Tags.Contains(tag(3, 0))).Count());
                Assert.AreEqual(1, store.Query<BulkTagged>().Where(x => x.Tags.Contains("small")).Count());
                Assert.AreEqual(0, store.Query<BulkTagged>().Where(x => x.Tags.Contains(tag(4, 0))).Count());
                Assert.AreEqual(1, store.Query<BulkTagged>().Where(x => x.Tags.Contains(tag(5, 0))).Count());
            }
            using (var store = openStore(dir)) {
                Assert.AreEqual(count - 1, store.Query<BulkTagged>().Count());
                Assert.AreEqual(1, store.Query<BulkTagged>().Where(x => x.Tags.Contains("small")).Count());
                Assert.AreEqual(0, store.Query<BulkTagged>().Where(x => x.Tags.Contains(tag(4, 0))).Count());
                Assert.AreEqual(1, store.Query<BulkTagged>().Where(x => x.TagIds.Contains(guid(5, 7))).Count());
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void ArrayIndexes_RebuildFromTheLogWhenTheIndexFileIsGone() {
        // The migration off the sorted layout leans on this: an array index that reports timestamp 0
        // is repopulated by the startup replay. Losing the whole index file is the same situation for
        // every index at once, and is worth holding on its own.
        const int count = 6;
        var dir = tempDir();
        try {
            using (var store = openStore(dir)) insertItems(store, count);

            var kvFolder = Path.Combine(dir, FileKeyUtility.IndexEngine_NativeKvFolderKey);
            Directory.Delete(kvFolder, true);

            using (var store = openStore(dir)) assertAllQueryable(store, count);
            using (var store = openStore(dir)) assertAllQueryable(store, count); // and the rebuild was persisted
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void LegacySortedArrayIndex_IsDroppedAndRebuiltOnTheHashLayout() {
        var dir = tempDir();
        try {
            // A store from before the layout move: its packed arrays sat in a SORTED index named by
            // the property's unique key alone. Stand one in, holding an entry no query may ever see.
            var uniqueKey = tagsPropertyUniqueKey();
            var kvFolder = Path.Combine(dir, FileKeyUtility.IndexEngine_NativeKvFolderKey);
            Directory.CreateDirectory(kvFolder);
            var kvFile = Path.Combine(kvFolder, FileKeyUtility.IndexEngine_NativeKvFileKey);
            using (var engine = new BPlusTreeStorageEngine(kvFile)) {
                var legacy = engine.OpenOrCreateSortedIntIndex<byte[]>(uniqueKey);
                engine.BeginTransaction();
                legacy.Set(1, [1, 2, 3]);
                engine.CommitTransaction(1, true);
            }

            const int count = 4;
            using (var store = openStore(dir)) {
                insertItems(store, count);
                assertAllQueryable(store, count);
            }
            using (var store = openStore(dir)) assertAllQueryable(store, count);

            using (var engine = new BPlusTreeStorageEngine(kvFile)) {
                // the arrays now live in a hash index under the suffixed name...
                var moved = engine.OpenOrCreateIntHashIndex<byte[]>(NativeKvArrayIndexName.For(uniqueKey));
                Assert.AreNotEqual(0, moved.GetTimestamp(), "the hash-layout index was never created — is the unique key derived correctly?");
                Assert.AreEqual(count, moved.Count);
                // ...and the sorted index that held them is gone, freed by the DeleteUnopenedIndexes
                // pass that runs after every open (a surviving one would still hold its entry)
                Assert.AreEqual(0, engine.OpenOrCreateSortedIntIndex<byte[]>(uniqueKey).Count);
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// The KV index name the Tags array index used before the layout move: IndexFactory.getUniqueKey
    /// is the property's guid, plus a culture and sub key where either applies — neither does here.
    /// </summary>
    static string tagsPropertyUniqueKey() {
        var property = datamodel().NodeTypes.Values
            .SelectMany(t => t.Properties.Values)
            .First(p => p.CodeName == nameof(BulkTagged.Tags));
        return property.Id.ToString();
    }
}
