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
    public void FullSizeEmbeddings_RoundTripThroughOverflowPages() {
        // 3072 dimensions (text-embedding-3-large) is 12288 bytes — far past what a single index
        // page can hold, so the value lives on a chain of overflow pages.
        static float[] large(int seed) => Enumerable.Range(0, 3072).Select(i => (i + seed) * 0.125f).ToArray();
        var dir = tempDir();
        try {
            var filePath = Path.Combine(dir, "large.db");
            using (var cache = new NativeKvEmbeddingCache(filePath)) {
                cache.Set(1ul, large(0));
                cache.SetMany(Enumerable.Range(2, 20).Select(i => Tuple.Create((ulong)i, large(i))).ToList());
                cache.Set(1ul, large(100)); // replacing one chain with another

                Assert.IsTrue(cache.TryGet(1ul, out var e));
                CollectionAssert.AreEqual(large(100), e);
            }
            using (var cache = new NativeKvEmbeddingCache(filePath)) {
                Assert.IsTrue(cache.TryGet(1ul, out var e1)); // read back from disk, not from the memory cache
                CollectionAssert.AreEqual(large(100), e1);
                for (var i = 2; i <= 21; i++) {
                    Assert.IsTrue(cache.TryGet((ulong)i, out var e), $"embedding {i} was lost");
                    CollectionAssert.AreEqual(large(i), e);
                }
                Assert.IsFalse(cache.TryGet(9999ul, out _));
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
                var hashes = engine.OpenOrCreateSortedIntIndex<ulong>("embedding-hashes");
                var embeddings = engine.OpenOrCreateSortedIntIndex<byte[]>("embeddings");
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
                Assert.AreEqual(0, engine.OpenOrCreateSortedIntIndex<ulong>("embedding-hashes").Count);
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }
}
