using Relatude.DB.Common;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;

namespace Relatude.Persistence;

/// <summary>
/// The built-in disk-based text index engine (Relatude.DB.TextIndex), exercised directly at the
/// engine level: BM25 ranking and paging, and/or semantics, prefix/infix/fuzzy terms and spelling
/// suggestions (feature parity with the in-memory trie index), plus the storage behaviors that are
/// unique to it — restart durability via the segment manifest, WAL binding resets, segment merging,
/// the cache byte budget, and the opt-in deferred flush mode.
/// </summary>
[TestClass]
public class TextIndexEngineTests {

    const int MinWord = 2, MaxWord = 64;

    static string tempDir() {
        var dir = Path.Combine(Path.GetTempPath(), "RelatudeDB_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
    static IWordIndex openIndex(TextIndexEngine engine) =>
        engine.OpenWordIndex(new SetRegister(100), "w-test", "word test", new WordIndexOptions(MinWord, MaxWord, true, true));
    static void inTransaction(TextIndexEngine engine, long timestamp, Action work) {
        engine.BeginTransaction();
        work();
        engine.CommitTransaction(timestamp);
        engine.MakeDurable();
    }
    static TermSet terms(string query) => TermSet.Parse(query, MinWord, MaxWord, allowInfix: true);
    static int[] rankedIds(IWordIndex idx, string query, bool orSearch = true) =>
        [.. idx.SearchForRankedHitData(terms(query), 0, 1000, 10000, 100, orSearch, out _).Select(h => h.NodeId)];
    static int[] unrankedIds(IWordIndex idx, string query, bool orSearch = true) =>
        [.. idx.SearchForIdSetUnranked(terms(query), orSearch, 100).Enumerate().Order()];

    [TestMethod]
    public void Bm25RankingAndPaging() {
        var dir = tempDir();
        try {
            using var engine = new TextIndexEngine(dir);
            engine.SetWalFileId(Guid.NewGuid());
            var idx = openIndex(engine);
            inTransaction(engine, 1000, () => {
                idx.Add(1, "apple banana apple apple");
                idx.Add(2, "apple orange");
                idx.Add(3, "banana cherry");
            });
            var hits = idx.SearchForRankedHitData(terms("apple"), 0, 10, 1000, 100, orSearch: true, out var totalHits);
            Assert.AreEqual(2, totalHits);
            Assert.AreEqual(1, hits[0].NodeId, "three occurrences must outrank one");
            Assert.AreEqual(2, hits[1].NodeId);
            Assert.IsTrue(hits[0].Score > hits[1].Score, "scores must order with the ranking");
            Assert.IsTrue(hits[1].Score > 0, "every returned hit carries a BM25 score");
            var page0 = idx.SearchForRankedHitData(terms("apple"), 0, 1, 1000, 100, true, out var t0);
            var page1 = idx.SearchForRankedHitData(terms("apple"), 1, 1, 1000, 100, true, out _);
            Assert.AreEqual(2, t0, "totalHits counts all hits, not the page");
            Assert.AreEqual(1, page0.Single().NodeId);
            Assert.AreEqual(2, page1.Single().NodeId);
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void AndOrSemantics_RankedAndUnranked() {
        var dir = tempDir();
        try {
            using var engine = new TextIndexEngine(dir);
            engine.SetWalFileId(Guid.NewGuid());
            var idx = openIndex(engine);
            inTransaction(engine, 1000, () => {
                idx.Add(1, "apple banana");
                idx.Add(2, "apple orange");
                idx.Add(3, "banana cherry");
            });
            CollectionAssert.AreEqual(new[] { 1 }, unrankedIds(idx, "apple banana", orSearch: false));
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, unrankedIds(idx, "apple banana", orSearch: true));
            CollectionAssert.AreEqual(new[] { 1 }, rankedIds(idx, "apple banana", orSearch: false));
            CollectionAssert.AreEquivalent(new[] { 1, 2, 3 }, rankedIds(idx, "apple banana", orSearch: true));
            Assert.AreEqual(0, unrankedIds(idx, "apple nosuchword", orSearch: false).Length);
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void PrefixInfixFuzzyAndSuggest() {
        var dir = tempDir();
        try {
            using var engine = new TextIndexEngine(dir);
            engine.SetWalFileId(Guid.NewGuid());
            var idx = openIndex(engine);
            inTransaction(engine, 1000, () => {
                idx.Add(1, "waterproof jacket");
                idx.Add(2, "watermelon smoothie");
                idx.Add(3, "proofing dough");
                idx.Add(4, "banana bread");
            });
            CollectionAssert.AreEqual(new[] { 1, 2 }, unrankedIds(idx, "water*"), "prefix");
            CollectionAssert.AreEqual(new[] { 1, 3 }, unrankedIds(idx, "*proof"), "infix");
            CollectionAssert.AreEqual(new[] { 4 }, unrankedIds(idx, "bananna~"), "fuzzy");
            CollectionAssert.AreEqual(new[] { 1, 2 }, rankedIds(idx, "water*").Order().ToArray(), "ranked prefix");
            var suggestions = idx.SuggestSpelling("bananna", boostCommonWords: false).ToList();
            CollectionAssert.Contains(suggestions, "banana", "suggest");
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void RemoveAndUpdate() {
        var dir = tempDir();
        try {
            using var engine = new TextIndexEngine(dir);
            engine.SetWalFileId(Guid.NewGuid());
            var idx = openIndex(engine);
            inTransaction(engine, 1000, () => idx.Add(1, "crimson sunset"));
            inTransaction(engine, 2000, () => idx.Remove(1, "crimson sunset"));
            Assert.AreEqual(0, unrankedIds(idx, "crimson").Length, "removed doc must not match");
            Assert.AreEqual(0, rankedIds(idx, "crimson").Length);
            // update = remove old text + add new text in one transaction
            inTransaction(engine, 3000, () => idx.Add(1, "crimson sunset"));
            inTransaction(engine, 4000, () => {
                idx.Remove(1, "crimson sunset");
                idx.Add(1, "golden sunrise");
            });
            Assert.AreEqual(0, unrankedIds(idx, "crimson").Length, "old words must be gone after update");
            CollectionAssert.AreEqual(new[] { 1 }, unrankedIds(idx, "golden"));
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void Restart_KeepsDataAndPosition() {
        var dir = tempDir();
        var walId = Guid.NewGuid();
        try {
            using (var engine = new TextIndexEngine(dir)) {
                engine.SetWalFileId(walId);
                var idx = openIndex(engine);
                inTransaction(engine, 1000, () => {
                    for (var i = 1; i <= 50; i++) idx.Add(i, i % 2 == 0 ? "waterproof canvas" : "leather satchel");
                });
                Assert.AreEqual(1000, engine.GetTimestamp());
            }
            using (var engine = new TextIndexEngine(dir)) {
                var idx = openIndex(engine);
                Assert.AreEqual(walId, engine.GetWalFileId(), "WAL binding must survive a restart");
                Assert.AreEqual(1000, engine.GetTimestamp(), "durable position must survive a restart");
                Assert.AreEqual(25, unrankedIds(idx, "waterproof").Length);
                Assert.AreEqual(25, rankedIds(idx, "leather").Length);
                inTransaction(engine, 2000, () => idx.Add(1000, "unique zebra"));
            }
            using (var engine = new TextIndexEngine(dir)) {
                var idx = openIndex(engine);
                Assert.AreEqual(2000, engine.GetTimestamp());
                CollectionAssert.AreEqual(new[] { 1000 }, unrankedIds(idx, "zebra"));
                Assert.AreEqual(25, unrankedIds(idx, "waterproof").Length, "older segments must still answer");
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void ForeignWalFileId_ResetsOnceAndAdoptsTheNewId() {
        var dir = tempDir();
        var logA = Guid.NewGuid();
        var logB = Guid.NewGuid();
        try {
            // mirrors WalFileBindingTests, for this engine: indexes open first, binding check after
            using (var engines = new IndexEngines(null, new TextIndexEngine(dir))) {
                var idx = openIndex((TextIndexEngine)engines.Text!);
                engines.BindToWalFile(logA, _ => { });
                engines.BeginTransaction();
                idx.Add(1, "hello world 1");
                engines.CommitTransaction(1000);
                engines.MakeDurable(1000);
                Assert.AreEqual(1000, engines.Text!.GetTimestamp());
            }
            // the log is now a different file: the engine must reset (timestamp 0 forces the
            // rebuild), adopt the new id, and stay usable afterwards
            using (var engines = new IndexEngines(null, new TextIndexEngine(dir))) {
                var idx = openIndex((TextIndexEngine)engines.Text!);
                engines.BindToWalFile(logB, _ => { });
                Assert.AreEqual(logB, engines.Text!.GetWalFileId());
                Assert.AreEqual(0, engines.Text!.GetTimestamp());
                Assert.AreEqual(0, unrankedIds(idx, "hello").Length, "reset index must be empty");
                engines.BeginTransaction();
                idx.Add(2, "hello world 2");
                engines.CommitTransaction(2000);
                engines.MakeDurable(2000);
            }
            // next startup against the same log: the adopted binding holds, nothing resets again
            using (var engines = new IndexEngines(null, new TextIndexEngine(dir))) {
                var idx = openIndex((TextIndexEngine)engines.Text!);
                engines.BindToWalFile(logB, _ => { });
                Assert.AreEqual(2000, engines.Text!.GetTimestamp());
                CollectionAssert.AreEqual(new[] { 2 }, unrankedIds(idx, "world"));
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void ManySmallCommits_SegmentsStayMergedAndSearchable() {
        var dir = tempDir();
        try {
            using var engine = new TextIndexEngine(dir);
            engine.SetWalFileId(Guid.NewGuid());
            var idx = openIndex(engine);
            for (var i = 1; i <= 150; i++) {
                var doc = i;
                inTransaction(engine, i * 10, () => idx.Add(doc, "common word" + doc));
            }
            Assert.AreEqual(150, unrankedIds(idx, "common").Length);
            CollectionAssert.AreEqual(new[] { 37 }, unrankedIds(idx, "word37"));
            idx.SearchForRankedHitData(terms("common"), 0, 10, 100000, 100, true, out var total);
            Assert.AreEqual(150, total);
            var segFiles = Directory.GetFiles(Path.Combine(dir, "textindex"), "seg_*.bin", SearchOption.AllDirectories);
            Assert.IsTrue(segFiles.Length < 20, "the merge ladder should keep the segment count logarithmic, was " + segFiles.Length);
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void OptimizeDisk_MergesToOneSegment() {
        var dir = tempDir();
        try {
            using var engine = new TextIndexEngine(dir);
            engine.SetWalFileId(Guid.NewGuid());
            var idx = openIndex(engine);
            for (var i = 1; i <= 40; i++) {
                var doc = i;
                inTransaction(engine, i * 10, () => idx.Add(doc, "shared token" + doc));
            }
            for (var i = 1; i <= 20; i++) {
                var doc = i;
                inTransaction(engine, 400 + i * 10, () => idx.Remove(doc, "shared token" + doc));
            }
            engine.OptimizeDisk();
            var segFiles = Directory.GetFiles(Path.Combine(dir, "textindex"), "seg_*.bin", SearchOption.AllDirectories);
            Assert.AreEqual(1, segFiles.Length, "optimize must leave a single segment");
            Assert.AreEqual(600, engine.GetTimestamp(), "optimize must not move the position");
            Assert.AreEqual(20, unrankedIds(idx, "shared").Length, "only the surviving docs remain");
            CollectionAssert.AreEqual(new[] { 21 }, unrankedIds(idx, "token21"));
            Assert.AreEqual(0, unrankedIds(idx, "token7").Length, "tombstoned words are gone after the merge");
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void RetiredSegments_ReleaseTheirCachedBlocks() {
        // Cached dictionary blocks are keyed by segment id, and segment ids are never reused, so a
        // block cached from a segment that has since been merged away can never be read again.
        // Nothing but this eviction would drop it: it would sit in the shared cache until the byte
        // budget pushed it out, which on a long import is most of the cache — memory held for data
        // that no longer exists, and that no GC can reclaim.
        var dir = tempDir();
        try {
            using var engine = new TextIndexEngine(dir);
            engine.SetWalFileId(Guid.NewGuid());
            var idx = openIndex(engine);
            // equal-sized rounds, so the merge ladder retires segments as it goes
            var id = 0;
            for (var round = 0; round < 8; round++) {
                inTransaction(engine, (round + 1) * 100, () => {
                    for (var i = 0; i < 200; i++) idx.Add(++id, "shared token" + id);
                });
                unrankedIds(idx, "shared"); // reads the dictionary, so blocks land in the cache
            }
            var afterRounds = engine.GetCacheStats().Entries;
            Assert.IsTrue(afterRounds > 0, "the searches should have cached something to begin with");
            // what the segments that are still live actually need for that same search: anything
            // the cache holds beyond it came from a segment that has since been merged away
            idx.ClearCache();
            unrankedIds(idx, "shared");
            var live = engine.GetCacheStats().Entries;
            Assert.IsTrue(afterRounds <= live + 2, $"cache holds {afterRounds} entries, the live segments need {live}");

            // rounds of shrinking size never trigger the ladder, so several segments survive for
            // the explicit full merge to retire in one go
            for (var round = 0; round < 4; round++) {
                var count = 400 >> round;
                inTransaction(engine, 1000 + round * 100, () => {
                    for (var i = 0; i < count; i++) idx.Add(++id, "shared token" + id);
                });
            }
            Assert.AreEqual(id, unrankedIds(idx, "shared").Length);
            Assert.IsTrue(Directory.GetFiles(Path.Combine(dir, "textindex"), "seg_*.bin", SearchOption.AllDirectories).Length > 1,
                "the shrinking rounds should have left several segments to retire");
            Assert.IsTrue(engine.GetCacheStats().Entries > 0);

            engine.OptimizeDisk(); // merges everything into one segment, retiring the rest
            Assert.AreEqual(0, engine.GetCacheStats().Entries, "every retired segment's entries must be gone");
            Assert.AreEqual(0, engine.GetCacheStats().UsedBytes, "and the byte accounting must go with them");

            // still a working cache afterwards, reading the surviving segment
            Assert.AreEqual(id, unrankedIds(idx, "shared").Length);
            Assert.IsTrue(engine.GetCacheStats().Entries > 0);

            idx.Dispose(); // the cache belongs to the engine and outlives one index
            Assert.AreEqual(0, engine.GetCacheStats().Entries, "disposing an index must release its cache entries");
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void TinyCacheBudget_SearchesStayCorrect() {
        var dir = tempDir();
        try {
            // a budget too small to hold anything degrades to disk reads, never to wrong results
            using var engine = new TextIndexEngine(dir, new TextIndexOptions { MaxCacheBytes = 512 });
            engine.SetWalFileId(Guid.NewGuid());
            var idx = openIndex(engine);
            for (var batch = 0; batch < 5; batch++) {
                var b = batch;
                inTransaction(engine, (b + 1) * 100, () => {
                    for (var i = 1; i <= 20; i++) idx.Add(b * 20 + i, "bulk item" + (b * 20 + i) + (i % 2 == 0 ? " waterproof" : " leather"));
                });
            }
            Assert.AreEqual(100, unrankedIds(idx, "bulk").Length);
            Assert.AreEqual(50, unrankedIds(idx, "waterproof").Length);
            Assert.AreEqual(100, unrankedIds(idx, "item*").Length, "prefix scan through the block cache");
            CollectionAssert.AreEqual(new[] { 73 }, unrankedIds(idx, "item73"));
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void DeferredFlush_ReportsOlderPositionAndReplayRebuilds() {
        var dir = tempDir();
        var walId = Guid.NewGuid();
        var options = new TextIndexOptions { MemTableFlushThresholdBytes = 10L * 1024 * 1024 };
        try {
            using (var engine = new TextIndexEngine(dir, options)) {
                engine.SetWalFileId(walId);
                var idx = openIndex(engine);
                inTransaction(engine, 1000, () => idx.Add(1, "amber falcon"));
                CollectionAssert.AreEqual(new[] { 1 }, unrankedIds(idx, "amber"), "buffered writes are searchable immediately");
                Assert.AreEqual(0, engine.GetTimestamp(), "below the flush threshold the index must keep reporting its durable position");
            }
            using (var engine = new TextIndexEngine(dir, options)) {
                var idx = openIndex(engine);
                Assert.AreEqual(0, engine.GetTimestamp(), "so the startup loader replays from 0");
                Assert.AreEqual(0, unrankedIds(idx, "amber").Length, "the buffer was (by design) not durable");
                // the replay the store would now run, re-delivering the op:
                engine.BeginTransaction();
                idx.RegisterAddDuringStateLoad(1, "amber falcon");
                engine.CommitTransaction(1000);
                engine.MakeDurable();
                CollectionAssert.AreEqual(new[] { 1 }, unrankedIds(idx, "amber"));
                // a log rewrite hot-swap must flush regardless of the threshold
                engine.SetWalFileIdAndTimestamp(1500, walId);
                Assert.AreEqual(1500, engine.GetTimestamp());
            }
            using (var engine = new TextIndexEngine(dir, options)) {
                var idx = openIndex(engine);
                Assert.AreEqual(1500, engine.GetTimestamp());
                CollectionAssert.AreEqual(new[] { 1 }, unrankedIds(idx, "amber"));
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }
}
