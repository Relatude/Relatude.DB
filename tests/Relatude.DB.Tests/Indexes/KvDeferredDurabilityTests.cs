using Relatude.DB.Datastores.Indexes.BTreeIndex;

namespace Relatude.Indexes;

/// <summary>
/// The publish/durable split of the B+Tree engine: PublishTransaction commits to the live snapshot
/// only, MakeDurable writes the durable meta. A reopen (= crash) must roll back to the last durable
/// point exactly — published-only transactions vanish cleanly, durable ones survive completely,
/// and pages freed by published transactions must never be recycled into a state a crash falls
/// back to (the freed-page quarantine).
/// </summary>
[TestClass]
public class KvDeferredDurabilityTests {

    static string tempDir() {
        var dir = Path.Combine(Path.GetTempPath(), "RelatudeDB_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void publish(BPlusTreeStorageEngine engine, Action mutate, long timestamp) {
        engine.BeginTransaction();
        mutate();
        engine.PublishTransaction(timestamp);
    }

    [TestMethod]
    public void Publish_WithoutMakeDurable_RollsBackOnReopen() {
        var dir = tempDir();
        try {
            var filePath = Path.Combine(dir, "kv.db");
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                var index = engine.OpenOrCreateIntIndex<string>("idx");
                engine.BeginTransaction();
                index.Set(1, "durable");
                engine.CommitTransaction(10, true);

                publish(engine, () => index.Set(2, "published-only"), 20);
                Assert.AreEqual("published-only", index.GetValue(2)); // live snapshot sees it
                Assert.AreEqual(20, engine.GetTimestamp());
                // dispose without MakeDurable = crash
            }
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                var index = engine.OpenOrCreateIntIndex<string>("idx");
                Assert.AreEqual(10, engine.GetTimestamp()); // rolled back to the durable point
                Assert.AreEqual("durable", index.GetValue(1));
                Assert.IsFalse(index.ContainsKey(2));
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void MultiplePublishes_OneMakeDurable_AllSurviveReopen() {
        var dir = tempDir();
        try {
            var filePath = Path.Combine(dir, "kv.db");
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                var index = engine.OpenOrCreateIntIndex<string>("idx");
                for (int i = 0; i < 50; i++) publish(engine, () => index.Set(i, "v" + i), 100 + i);
                // overwrites and removes: frees pages across publishes, exercising the quarantine
                for (int i = 0; i < 25; i++) publish(engine, () => index.Set(i, "w" + i), 200 + i);
                for (int i = 40; i < 50; i++) publish(engine, () => index.Remove(i), 300 + i);
                engine.MakeDurable(true);
            }
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                var index = engine.OpenOrCreateIntIndex<string>("idx");
                Assert.AreEqual(349, engine.GetTimestamp());
                for (int i = 0; i < 25; i++) Assert.AreEqual("w" + i, index.GetValue(i));
                for (int i = 25; i < 40; i++) Assert.AreEqual("v" + i, index.GetValue(i));
                for (int i = 40; i < 50; i++) Assert.IsFalse(index.ContainsKey(i));
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void PublishedThenCrashed_ReopenedFileIsFullyWritable() {
        // A crash discards published state; the reopened file must then behave like the durable
        // state never moved: new writes (which reuse pages the lost publishes had allocated or
        // freed) must not corrupt anything across another reopen.
        var dir = tempDir();
        try {
            var filePath = Path.Combine(dir, "kv.db");
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                var index = engine.OpenOrCreateIntIndex<string>("idx");
                engine.BeginTransaction();
                for (int i = 0; i < 100; i++) index.Set(i, "base" + i);
                engine.CommitTransaction(10, true);
                for (int i = 0; i < 100; i++) publish(engine, () => index.Set(i, "lost" + i), 20 + i);
                // crash: no MakeDurable
            }
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                var index = engine.OpenOrCreateIntIndex<string>("idx");
                for (int i = 0; i < 100; i++) Assert.AreEqual("base" + i, index.GetValue(i));
                engine.BeginTransaction();
                for (int i = 0; i < 100; i++) index.Set(i, "next" + i);
                engine.CommitTransaction(30, true);
            }
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                var index = engine.OpenOrCreateIntIndex<string>("idx");
                Assert.AreEqual(30, engine.GetTimestamp());
                for (int i = 0; i < 100; i++) Assert.AreEqual("next" + i, index.GetValue(i));
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void CommitTransaction_IsStillDurablePerCall() {
        var dir = tempDir();
        try {
            var filePath = Path.Combine(dir, "kv.db");
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                var index = engine.OpenOrCreateIntIndex<string>("idx");
                engine.BeginTransaction();
                index.Set(1, "a");
                engine.CommitTransaction(10, true); // no explicit MakeDurable
            }
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                Assert.AreEqual(10, engine.GetTimestamp());
                Assert.AreEqual("a", engine.OpenOrCreateIntIndex<string>("idx").GetValue(1));
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void MakeDurable_WhileTransactionActive_Throws() {
        using var engine = new BPlusTreeStorageEngine(null); // memory-only is enough for the guard
        engine.OpenOrCreateIntIndex<string>("idx");
        engine.BeginTransaction();
        Assert.ThrowsException<InvalidOperationException>(() => engine.MakeDurable(true));
        engine.RollbackTransaction();
    }

    [TestMethod]
    public void MakeDurable_WithNothingPending_IsHarmless() {
        var dir = tempDir();
        try {
            var filePath = Path.Combine(dir, "kv.db");
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                var index = engine.OpenOrCreateIntIndex<string>("idx");
                engine.BeginTransaction();
                index.Set(1, "a");
                engine.CommitTransaction(10, true);
                engine.MakeDurable(true); // already durable: no-op
                engine.MakeDurable(true);
            }
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                Assert.AreEqual("a", engine.OpenOrCreateIntIndex<string>("idx").GetValue(1));
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }
}
