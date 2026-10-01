using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.Query;
using Relatude.Utils;

namespace Relatude.Querying;

#region node meta test datamodel
// maps none of the node's own dates
[Node]
public class MetaNote {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(Indexed = true)]
    public string Title { get; set; } = "";
    [DateTimeProperty(Indexed = true)]
    public DateTime Due { get; set; }
}
// maps both dates to members of its own
[Node]
public class MetaPost {
    [PublicIdProperty]
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    [CreatedUtcProperty]
    public DateTime Created { get; set; }
    [ChangedUtcProperty]
    public DateTime Changed { get; set; }
}
// inherits the mapped members
public class MetaPostChild : MetaPost {
    public int Rank { get; set; }
}
// maps the whole meta
[Node]
public class MetaPage {
    [PublicIdProperty]
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public NodeMeta Meta { get; set; } = NodeMeta.Empty;
}
#endregion

/// <summary>
/// The node's own creation and change times are indexed system properties of every node type
/// (_createdUtc / _changedUtc on the base type), queried through NodeMeta (WhereMeta, OrderByMeta)
/// or through whatever member a class maps them to - and indexed by the default value index engine.
/// </summary>
[TestClass]
public class NodeMetaQueryTests {

    static Datamodel model() {
        var dm = new Datamodel();
        dm.Add<MetaNote>();
        dm.Add<MetaPost>();
        dm.Add<MetaPostChild>();
        dm.Add<MetaPage>();
        return dm;
    }
    static NodeStore openMemoryStore() => new(DataStoreLocal.Open(model()));

    // inserted one by one with the clock moving in between, so creation order is insertion order
    static List<Guid> insertNotes(NodeStore store, int count) {
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++) {
            var note = new MetaNote { Id = Guid.NewGuid(), Title = "note " + i, Due = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(-i) };
            store.Insert(note);
            ids.Add(note.Id);
            Thread.Sleep(2);
        }
        return ids;
    }
    static List<Guid> ids<T>(IQueryOfNodes<T, T> query) => [.. query.SelectId().Execute()];
    static List<Guid> only(IEnumerable<Guid> ids, IEnumerable<Guid> keep) {
        var set = keep.ToHashSet();
        return [.. ids.Where(set.Contains)];
    }

    [TestMethod]
    public void OrderByMeta_SortsATypeThatMapsNoDates() {
        using var store = openMemoryStore();
        var inserted = insertNotes(store, 20);

        CollectionAssert.AreEqual(inserted, ids(store.Query<MetaNote>().OrderByMeta(m => m.CreatedUtc)), "created, oldest first");
        CollectionAssert.AreEqual(Enumerable.Reverse(inserted).ToList(), ids(store.Query<MetaNote>().OrderByMetaDescending(m => m.CreatedUtc)), "created, newest first");
        CollectionAssert.AreEqual(inserted, ids(store.Query<MetaNote>().OrderByMeta(m => m.ChangedUtc)), "changed, never updated");

        // an update moves the node to the end of the change order and leaves its creation where it was
        var first = store.Query<MetaNote>(inserted[0]).Execute().Single();
        first.Title = "edited";
        store.Update(first);
        var changedOrder = inserted.Skip(1).Append(inserted[0]).ToList();
        CollectionAssert.AreEqual(changedOrder, ids(store.Query<MetaNote>().OrderByMeta(m => m.ChangedUtc)), "changed order after an update");
        CollectionAssert.AreEqual(inserted, ids(store.Query<MetaNote>().OrderByMeta(m => m.CreatedUtc)), "created order after an update");

        // a deleted node leaves the indexes
        store.Delete(inserted[5]);
        CollectionAssert.AreEqual(changedOrder.Where(id => id != inserted[5]).ToList(), ids(store.Query<MetaNote>().OrderByMeta(m => m.ChangedUtc)), "after a delete");
    }

    [TestMethod]
    public void WhereMeta_FiltersByTheNodesOwnDates() {
        using var store = openMemoryStore();
        var before = insertNotes(store, 10);
        var mid = DateTime.UtcNow;
        Thread.Sleep(2);
        var after = insertNotes(store, 7);

        var query = store.Query<MetaNote>().WhereMeta(m => m.CreatedUtc > mid);
        StringAssert.Contains(query.ToString(), "m => m._createdUtc > ", "a meta lambda renders as the node's system property");
        Assert.AreEqual(after.Count, query.Count());
        Assert.AreEqual(before.Count, store.Query<MetaNote>().WhereMeta(m => m.CreatedUtc < mid).Count());
        Assert.AreEqual(after.Count, store.Query<MetaNote>().WhereMeta(m => mid <= m.ChangedUtc).Count(), "constant on the left");
        CollectionAssert.AreEquivalent(after, ids(store.Query<MetaNote>().WhereMeta(m => m.CreatedUtc >= mid && m.ChangedUtc >= mid)));
        // combined with a filter on the node's own properties, then sorted
        var titled = store.Query<MetaNote>().Where(x => x.Title == "note 3").WhereMeta(m => m.CreatedUtc > mid).OrderByMetaDescending(m => m.ChangedUtc);
        CollectionAssert.AreEqual(new[] { after[3] }, ids(titled));
        // the same filter written as a query string, against the system property by its name
        Assert.AreEqual(after.Count, store.Query<MetaNote>().Where("n => n._createdUtc > " + mid.Ticks).Count());
    }

    [TestMethod]
    public void BaseType_SortsNodesOfEveryType() {
        using var store = openMemoryStore();
        var inserted = new List<Guid>();
        for (var i = 0; i < 12; i++) {
            Guid id;
            if (i % 2 == 0) { var n = new MetaNote { Id = Guid.NewGuid(), Title = "n" + i }; store.Insert(n); id = n.Id; }
            else { var p = new MetaPage { Id = Guid.NewGuid(), Title = "p" + i }; store.Insert(p); id = p.Id; }
            inserted.Add(id);
            Thread.Sleep(2);
        }
        var all = store.QueryType(NodeConstants.BaseNodeTypeId);
        CollectionAssert.AreEqual(inserted, only(ids(all.OrderByMeta(m => m.CreatedUtc)), inserted), "every type in one order");
        CollectionAssert.AreEqual(Enumerable.Reverse(inserted).ToList(), only(ids(all.OrderByMetaDescending(m => m.ChangedUtc)), inserted));
    }

    [TestMethod]
    public void MappedDateMembers_QueryTheSameIndex() {
        using var store = openMemoryStore();
        var random = new Random(7);
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var posts = new List<MetaPost>();
        for (var i = 0; i < 30; i++) {
            var created = start.AddHours(random.Next(10_000));
            MetaPost post = i % 3 == 0 ? new MetaPostChild { Rank = i } : new MetaPost();
            post.Id = Guid.NewGuid();
            post.Title = "post " + i;
            post.Created = created;
            post.Changed = created.AddMinutes(random.Next(100_000));
            posts.Add(post);
        }
        store.Insert(posts);

        var byChanged = posts.OrderBy(p => p.Changed).Select(p => p.Id).ToList();
        CollectionAssert.AreEqual(byChanged, ids(store.Query<MetaPost>().OrderBy(x => x.Changed)), "by the mapped member");
        CollectionAssert.AreEqual(byChanged, ids(store.Query<MetaPost>().OrderByMeta(m => m.ChangedUtc)), "by the meta");
        var byCreatedDesc = posts.OrderByDescending(p => p.Created).Select(p => p.Id).ToList();
        CollectionAssert.AreEqual(byCreatedDesc, ids(store.Query<MetaPost>().OrderByDescending(x => x.Created)));

        var cutoff = start.AddHours(5_000);
        CollectionAssert.AreEquivalent(posts.Where(p => p.Created >= cutoff).Select(p => p.Id).ToList(), ids(store.Query<MetaPost>().Where(x => x.Created >= cutoff)));

        // the subclass inherits the mapped members: they are read back, filtered and sorted by
        var children = posts.OfType<MetaPostChild>().ToList();
        var fromStore = store.Query<MetaPostChild>().OrderBy(x => x.Created).Execute().ToList();
        CollectionAssert.AreEqual(children.OrderBy(c => c.Created).Select(c => c.Created).ToList(), fromStore.Select(c => c.Created).ToList(), "the created member is read back on the subclass");
        Assert.AreEqual(children.Count(c => c.Created >= cutoff), store.Query<MetaPostChild>().Where(x => x.Created >= cutoff).Count());
    }

    [TestMethod]
    public void MappedNodeMeta_QueriesTheSameIndex() {
        using var store = openMemoryStore();
        for (var i = 0; i < 15; i++) {
            store.Insert(new MetaPage { Id = Guid.NewGuid(), Title = "page " + i });
            Thread.Sleep(2);
        }
        var pages = store.Query<MetaPage>().OrderByDescending(x => x.Meta.ChangedUtc).Execute().ToList();
        Assert.AreEqual(15, pages.Count);
        for (var i = 1; i < pages.Count; i++) Assert.IsTrue(pages[i - 1].Meta.ChangedUtc >= pages[i].Meta.ChangedUtc, "newest first");
        var mid = pages[7].Meta.CreatedUtc;
        Assert.AreEqual(pages.Count(p => p.Meta.CreatedUtc > mid), store.Query<MetaPage>().Where(x => x.Meta.CreatedUtc > mid).Count());
        // selected like any other value
        var created = store.Query<MetaPage>().OrderBy(x => x.Meta.CreatedUtc).Select(x => x.Meta.CreatedUtc).Execute().ToList();
        CollectionAssert.AreEqual(pages.Select(p => p.Meta.CreatedUtc).OrderBy(d => d).ToList(), created);
    }

    [TestMethod]
    public void UnindexedMetaMembers_AreRefused() {
        using var store = openMemoryStore();
        var refused = 0;
        try { _ = store.Query<MetaNote>().WhereMeta(m => m.DisplayName == "x"); } catch (NotSupportedException) { refused++; }
        try { _ = store.Query<MetaPage>().OrderBy(x => x.Meta.CultureId); } catch (NotSupportedException) { refused++; }
        Assert.AreEqual(2, refused);
    }

    [TestMethod]
    public void SystemDates_CannotBeWritten() {
        using var store = openMemoryStore();
        var note = new MetaNote { Id = Guid.NewGuid(), Title = "x" };
        store.Insert(note);
        var created = ids(store.Query<MetaNote>().WhereMeta(m => m.CreatedUtc > DateTime.MinValue));
        try {
            store.Execute(store.CreateTransaction().UpdateProperty(note.Id, NodeConstants.SystemCreatedUtcPropertyId, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            Assert.Fail("writing the node's creation time must be refused");
        } catch (Exception err) when (err is not AssertFailedException) {
            Assert.IsTrue(err.ToString().Contains("maintained by the database"), err.ToString());
        }
        CollectionAssert.AreEqual(created, ids(store.Query<MetaNote>().WhereMeta(m => m.CreatedUtc > DateTime.MinValue)));
    }

    [TestMethod]
    public void NodeWithRevisions_StaysInTheIndexes() {
        using var store = openMemoryStore();
        var inserted = insertNotes(store, 5);
        store.Execute(store.CreateTransaction().EnableRevisions(inserted[2]));
        CollectionAssert.AreEqual(inserted, ids(store.Query<MetaNote>().OrderByMeta(m => m.CreatedUtc)), "a revision container keeps its creation time");
        CollectionAssert.AreEqual(inserted, ids(store.Query<MetaNote>().OrderByMeta(m => m.ChangedUtc)));
    }

    // the system dates are indexed by the same engine as any other value index: the default one
    static IIndex innerIndexOf(NodeStore store, Guid propertyId) {
        var index = ((DataStoreLocal)store.Datastore)._definition.Properties[propertyId].AllIndexes.Single();
        return index is OptimizedValueIndex<DateTime> optimized ? optimized.DequeueAndGetInner() : index;
    }
    static NodeStore openDiskStore(string dir, string engine) {
        var settings = TestEngines.Settings(value: engine);
        return new NodeStore(DataStoreLocal.Open(model(), settings, new IOProviderDisk(dir), null, null, null, null, TestEngines.Factory(dir, settings)));
    }

    [TestMethod]
    [DataRow("Memory")]
    [DataRow("Native")]
    [DataRow("Sqlite")]
    public void SystemDateIndexes_UseTheDefaultValueEngineAndSurviveRestart(string engine) {
        var dir = Path.Combine(Path.GetTempPath(), "RelatudeDB_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            List<Guid> inserted;
            using (var store = openDiskStore(dir, engine)) {
                inserted = insertNotes(store, 12);
                var dueIndex = innerIndexOf(store, store.Datastore.Datamodel.NodeTypes.Values.Single(t => t.CodeName == nameof(MetaNote)).AllPropertiesByName[nameof(MetaNote.Due)].Id);
                var changedIndex = innerIndexOf(store, NodeConstants.SystemChangedUtcPropertyId);
                var createdIndex = innerIndexOf(store, NodeConstants.SystemCreatedUtcPropertyId);
                Assert.AreEqual(dueIndex.GetType(), changedIndex.GetType(), "same engine as an ordinary indexed DateTime property");
                Assert.AreEqual(dueIndex.GetType(), createdIndex.GetType());
                if (engine == "Memory") Assert.IsInstanceOfType<ValueIndex<DateTime>>(changedIndex);
                else Assert.IsNotInstanceOfType<ValueIndex<DateTime>>(changedIndex);
                CollectionAssert.AreEqual(inserted, ids(store.Query<MetaNote>().OrderByMeta(m => m.ChangedUtc)));
            }
            using (var store = openDiskStore(dir, engine)) {
                CollectionAssert.AreEqual(inserted, ids(store.Query<MetaNote>().OrderByMeta(m => m.ChangedUtc)), "after restart (" + engine + ")");
                var extra = new MetaNote { Id = Guid.NewGuid(), Title = "extra" };
                store.Insert(extra);
                CollectionAssert.AreEqual(inserted.Append(extra.Id).ToList(), ids(store.Query<MetaNote>().OrderByMeta(m => m.CreatedUtc)), "a write after restart (" + engine + ")");
            }
        } finally {
            try { Directory.Delete(dir, true); } catch { } // sqlite files can linger briefly on windows
        }
    }
}
