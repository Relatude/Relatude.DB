using Relatude.DB.AI;
using Relatude.DB.Datastores.Indexes.BTreeIndex;

namespace Relatude.AI;

/// <summary>
/// The file-backed embedding cache over the B+Tree engine: set/get round-trips through disk
/// and reopen, overwrites, ClearAll, and the wipe-and-start-fresh recovery when the file
/// holds the older two-index int-keyed layout.
/// </summary>
[TestClass]
public class NativeKvEmbeddingCacheTests {

    static string tempDir() {
        var dir = Path.Combine(Path.GetTempPath(), "RelatudeDB_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static float[] embedding(int seed) => [seed, seed + 0.5f, -seed];

    [TestMethod]
    public void SetAndTryGet_RoundTripThroughDiskAndReopen() {
        var dir = tempDir();
        try {
            var filePath = Path.Combine(dir, "cache.db");
            using (var cache = new NativeKvEmbeddingCache(filePath)) {
                cache.Set(1ul, embedding(1));
                cache.SetMany([
                    Tuple.Create(ulong.MaxValue, embedding(2)),
                    Tuple.Create(long.MaxValue + 1ul, embedding(3)), // above long.MaxValue: needs the unsigned key encoding
                ]);
                cache.Set(1ul, embedding(4)); // overwrite must replace, not duplicate

                Assert.IsTrue(cache.TryGet(1ul, out var e));
                CollectionAssert.AreEqual(embedding(4), e);
                Assert.IsFalse(cache.TryGet(2ul, out _));
            }
            using (var cache = new NativeKvEmbeddingCache(filePath)) {
                Assert.IsTrue(cache.TryGet(1ul, out var e1));
                CollectionAssert.AreEqual(embedding(4), e1);
                Assert.IsTrue(cache.TryGet(ulong.MaxValue, out var e2));
                CollectionAssert.AreEqual(embedding(2), e2);
                Assert.IsTrue(cache.TryGet(long.MaxValue + 1ul, out var e3));
                CollectionAssert.AreEqual(embedding(3), e3);

                cache.ClearAll();
                Assert.IsFalse(cache.TryGet(1ul, out _));
                cache.Set(5ul, embedding(5)); // still usable after ClearAll
                Assert.IsTrue(cache.TryGet(5ul, out _));
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void OldTwoIndexLayout_IsWipedAndCacheStartsFresh() {
        var dir = tempDir();
        try {
            var filePath = Path.Combine(dir, "cache.db");
            // the layout the cache used before it moved to a single ulong-keyed index
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                var hashes = engine.OpenOrCreateIntIndex<ulong>("embedding-hashes");
                var embeddings = engine.OpenOrCreateIntIndex<byte[]>("embeddings");
                engine.BeginTransaction();
                hashes.Set(0, 42ul);
                embeddings.Set(0, [1, 2, 3, 4]);
                engine.CommitTransaction(1, true);
            }
            using (var cache = new NativeKvEmbeddingCache(filePath)) {
                Assert.IsFalse(cache.TryGet(42ul, out _)); // old entries are gone, not migrated
                cache.Set(42ul, embedding(1));
                Assert.IsTrue(cache.TryGet(42ul, out var e));
                CollectionAssert.AreEqual(embedding(1), e);
            }
            using (var engine = new BPlusTreeStorageEngine(filePath)) {
                // the wipe must also have removed the old hash index, so the name is free again
                Assert.AreEqual(0, engine.OpenOrCreateIntIndex<ulong>("embedding-hashes").Count);
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }
}
