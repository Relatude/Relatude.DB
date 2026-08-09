using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.Nodes;
using Relatude.Utils;

namespace Relatude.Transactions;

[TestClass]
public class BulkInsertCacheTests {

    static List<object> articles(int from, int count)
        => Enumerable.Range(from, count).Select(i => (object)new Article { Id = i, Name = "Article " + i }).ToList();

    [TestMethod]
    public void BulkInsert_NodesAreReadableBeforeAndAfterFlush() {
        using var store = Helper.Open();
        store.BulkInsert(articles(1, 50));

        Assert.AreEqual("Article 7", store.Get<Article>(7).Name); // before the log write
        store.Datastore.Maintenance(MaintenanceAction.FlushDisk);
        Assert.AreEqual("Article 7", store.Get<Article>(7).Name); // read back from the log
        Assert.AreEqual(50, store.Query<Article>().Count());
    }

    [TestMethod]
    public void BulkInsert_DoesNotLeaveNodesInCache() {
        using var store = Helper.Open();
        store.BulkInsert(articles(1, 50), flushToDisk: true);

        Assert.AreEqual(0, store.Datastore.GetInfo().NodeCacheCount);
    }

    [TestMethod]
    public void Insert_LeavesNodesInCache() {
        using var store = Helper.Open();
        store.Insert(articles(1, 50), flushToDisk: true);

        Assert.AreEqual(50, store.Datastore.GetInfo().NodeCacheCount);
    }

    [TestMethod]
    public void BulkInsert_RemovedNodeIsGone() {
        using var store = Helper.Open();
        store.BulkInsert(articles(1, 10));
        store.Delete(store.Get<Article>(3), flushToDisk: true);

        Assert.AreEqual(9, store.Query<Article>().Count());
    }
}

[TestClass]
public class CacheEvictionTests {

    [TestMethod]
    public void Cache_EvictsLeastRecentlyUsedFirst() {
        var cache = new Cache<int, string>(1000);
        for (var i = 0; i < 9; i++) cache.Set(i, "v" + i, 100);
        for (var i = 0; i < 5; i++) cache.TryGet(i, out _); // 0-4 are now the most recently used, 5-8 the least

        cache.Set(9, "v9", 100); // reaches max, so the cache is reduced to below half of max

        Assert.IsTrue(cache.Size <= 500);
        for (var i = 5; i < 9; i++) Assert.IsFalse(cache.Contains(i), "least recently used " + i + " survived");
        foreach (var i in new[] { 3, 4, 9 }) Assert.IsTrue(cache.Contains(i), "recently used " + i + " was evicted");
    }

    [TestMethod]
    public void Cache_NeverEvictsZeroSizedEntries() {
        var cache = new Cache<int, string>(1000);
        for (var i = 0; i < 10; i++) cache.Set(i, "v" + i, 0);
        for (var i = 10; i < 30; i++) cache.Set(i, "v" + i, 100);

        for (var i = 0; i < 10; i++) Assert.IsTrue(cache.Contains(i));
        Assert.AreEqual(10, cache.CountZeroSize);
    }

    [TestMethod]
    public void Cache_SizeIsTrackedThroughUpdatesAndRemovals() {
        var cache = new Cache<int, string>(1_000_000);
        cache.Set(1, "a", 100);
        cache.Set(2, "b", 200);
        Assert.AreEqual(300, cache.Size);

        cache.TryUpdateSize(1, 50);
        Assert.AreEqual(250, cache.Size);

        cache.Clear_EvenIf0Size(2);
        Assert.AreEqual(50, cache.Size);
        Assert.AreEqual(1, cache.Count);

        cache.Set(1, "a2", 10); // overwrite of an existing key
        Assert.AreEqual(10, cache.Size);
        Assert.AreEqual(1, cache.Count);
    }

    [TestMethod]
    public void Cache_ClearAllKeepsZeroSizedEntries() {
        var cache = new Cache<int, string>(1_000_000);
        cache.Set(1, "pinned", 0);
        cache.Set(2, "sized", 100);

        cache.ClearAll_NotSize0();

        Assert.IsTrue(cache.Contains(1));
        Assert.IsFalse(cache.Contains(2));
        Assert.AreEqual(0, cache.Size);
        Assert.AreEqual(1, cache.Count);
    }

    [TestMethod]
    public void Cache_HalfSizeDropsOldestHalf() {
        var cache = new Cache<int, string>(1_000_000);
        for (var i = 0; i < 10; i++) cache.Set(i, "v" + i, 100);

        cache.HalfSize();

        Assert.IsTrue(cache.Size <= 500);
        for (var i = 6; i < 10; i++) Assert.IsTrue(cache.Contains(i), "newest " + i + " was evicted");
        Assert.IsFalse(cache.Contains(0));
    }
}
