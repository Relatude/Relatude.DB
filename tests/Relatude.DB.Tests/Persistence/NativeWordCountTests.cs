using Relatude.DB.Common;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Indexes.Trie.CharArraySearch;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.Query.Data;
using static Relatude.Utils.WordCountOracle;

namespace Relatude.Persistence;

/// <summary>
/// Counting the words of a set of nodes out of the native (disk) text index. Held to the same
/// yardstick as the memory index - <see cref="Relatude.Utils.WordCountOracle"/>, the counts worked
/// out straight from the text - and, where it matters, compared directly against the memory index
/// counting the same corpus. Two engines, one answer.
///
/// The storage-specific part is what this adds over the memory tests: the same word living in
/// several segments at once, tombstones cancelling older segments, ops still sitting in the
/// memtable, and the read cache being left exactly as the walk found it.
/// </summary>
[TestClass]
public class NativeWordCountTests {

    static string tempDir() {
        var dir = Path.Combine(Path.GetTempPath(), "RelatudeDB_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
    static IWordIndex openIndex(TextIndexEngine engine) =>
        engine.OpenWordIndex(new SetRegister(100), "w-test", "word test", new WordIndexOptions(MinWordLength, MaxWordLength, true, false));
    static void inTransaction(TextIndexEngine engine, long timestamp, Action work) {
        engine.BeginTransaction();
        work();
        engine.CommitTransaction(timestamp);
        engine.MakeDurable();
    }
    /// <summary>The same corpus in the memory index, to compare engine against engine.</summary>
    static CharArrayTrie memoryIndex(Dictionary<int, string> corpus) {
        var trie = new CharArrayTrie(MinWordLength, MaxWordLength, prefixSearch: true, infixSearch: false);
        foreach (var doc in corpus) trie.IndexText(doc.Value, doc.Key);
        return trie;
    }
    static WordCountSet count(IWordIndex index, IEnumerable<int> subset, WordCountOptions options) {
        var counter = (IWordCountIndex)index;
        Assert.IsTrue(counter.CanCountWords, "the native index must be able to count words");
        return counter.CountWords(Subset(subset), options);
    }

    // -----------------------------------------------------------------------
    // The index agrees with the text, and with the other engine
    // -----------------------------------------------------------------------

    [TestMethod]
    public void OneSegmentMatchesTheTextAndTheMemoryIndex() {
        var dir = tempDir();
        try {
            var corpus = MakeCorpus(120);
            using var engine = new TextIndexEngine(dir);
            engine.SetWalFileId(Guid.NewGuid());
            var index = openIndex(engine);
            inTransaction(engine, 1000, () => { foreach (var doc in corpus) index.Add(doc.Key, doc.Value); });

            var options = new WordCountOptions { MaxWords = 1000 };
            var native = count(index, corpus.Keys, options);
            AssertSame(Expected(corpus, corpus.Keys, options), native.Words, "native:");
            AssertSame(memoryIndex(corpus).CountWords(Subset(corpus.Keys), options).Words, native.Words, "against the memory index:");
            Assert.AreEqual(DistinctIn(corpus, corpus.Keys), native.DistinctWords);
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void SubsetMatchesTheTextAndTheMemoryIndex() {
        var dir = tempDir();
        try {
            var corpus = MakeCorpus(200);
            using var engine = new TextIndexEngine(dir);
            engine.SetWalFileId(Guid.NewGuid());
            var index = openIndex(engine);
            inTransaction(engine, 1000, () => { foreach (var doc in corpus) index.Add(doc.Key, doc.Value); });

            var subset = corpus.Keys.Where(id => id % 3 == 0).ToArray();
            var options = new WordCountOptions { MaxWords = 1000 };
            var native = count(index, subset, options);
            AssertSame(Expected(corpus, subset, options), native.Words, "native:");
            AssertSame(memoryIndex(corpus).CountWords(Subset(subset), options).Words, native.Words, "against the memory index:");
        } finally {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// One document per transaction, so the same word ends up in dozens of segments and the walk has
    /// to merge its postings across all of them - the case a per-term lookup would get right by
    /// accident and a badly written scan would double count.
    /// </summary>
    [TestMethod]
    public void ManySegmentsMatchTheText() {
        var dir = tempDir();
        try {
            var corpus = MakeCorpus(60, seed: 7);
            using var engine = new TextIndexEngine(dir);
            engine.SetWalFileId(Guid.NewGuid());
            var index = openIndex(engine);
            var at = 0L;
            foreach (var doc in corpus) {
                at += 10;
                var d = doc;
                inTransaction(engine, at, () => index.Add(d.Key, d.Value));
            }
            var options = new WordCountOptions { MaxWords = 1000 };
            var native = count(index, corpus.Keys, options);
            AssertSame(Expected(corpus, corpus.Keys, options), native.Words, "native:");
            AssertSame(memoryIndex(corpus).CountWords(Subset(corpus.Keys), options).Words, native.Words, "against the memory index:");
        } finally {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>Ops that are still in the memtable, never flushed to a segment, count too.</summary>
    [TestMethod]
    public void UnflushedWritesCount() {
        var dir = tempDir();
        try {
            var corpus = MakeCorpus(40, seed: 11);
            using var engine = new TextIndexEngine(dir);
            engine.SetWalFileId(Guid.NewGuid());
            var index = openIndex(engine);
            // half of it on disk, half of it still in the memtable
            var onDisk = corpus.Where(d => d.Key % 2 == 0).ToArray();
            inTransaction(engine, 1000, () => { foreach (var doc in onDisk) index.Add(doc.Key, doc.Value); });
            engine.BeginTransaction();
            foreach (var doc in corpus.Where(d => d.Key % 2 == 1)) index.Add(doc.Key, doc.Value);
            engine.CommitTransaction(2000);

            var options = new WordCountOptions { MaxWords = 1000 };
            AssertSame(Expected(corpus, corpus.Keys, options), count(index, corpus.Keys, options).Words, "native:");
        } finally {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// Removals land as tombstones in a newer segment than the adds they cancel, so a walk that
    /// merely concatenated the segments would report the removed documents as still holding the word.
    /// </summary>
    [TestMethod]
    public void RemovedDocumentsAreGoneAcrossSegments() {
        var dir = tempDir();
        try {
            var corpus = MakeCorpus(80, seed: 3);
            using var engine = new TextIndexEngine(dir);
            engine.SetWalFileId(Guid.NewGuid());
            var index = openIndex(engine);
            inTransaction(engine, 1000, () => { foreach (var doc in corpus) index.Add(doc.Key, doc.Value); });
            var removed = corpus.Keys.Where(id => id % 4 == 0).ToArray();
            inTransaction(engine, 2000, () => { foreach (var id in removed) index.Remove(id, corpus[id]); });
            foreach (var id in removed) corpus.Remove(id);

            var options = new WordCountOptions { MaxWords = 1000 };
            var native = count(index, corpus.Keys, options);
            AssertSame(Expected(corpus, corpus.Keys, options), native.Words, "native:");
            AssertSame(memoryIndex(corpus).CountWords(Subset(corpus.Keys), options).Words, native.Words, "against the memory index:");
        } finally {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>A document updated in place: remove + add of the same node, newest wins.</summary>
    [TestMethod]
    public void UpdatedDocumentsCountOnce() {
        var dir = tempDir();
        try {
            using var engine = new TextIndexEngine(dir);
            engine.SetWalFileId(Guid.NewGuid());
            var index = openIndex(engine);
            inTransaction(engine, 1000, () => index.Add(1, "zorbak zorbak snazzle"));
            inTransaction(engine, 2000, () => {
                index.Remove(1, "zorbak zorbak snazzle");
                index.Add(1, "zorbak grunthop");
            });
            var words = count(index, [1], new WordCountOptions { MaxWords = 100 }).Words;
            CollectionAssert.AreEquivalent(new[] { "zorbak", "grunthop" }, words.Select(w => w.Word).ToArray());
            var zorbak = words.Single(w => w.Word == "zorbak");
            Assert.AreEqual(1, zorbak.Documents);
            Assert.AreEqual(1, zorbak.Occurrences, "the newest version holds it once, not three times");
        } finally {
            Directory.Delete(dir, true);
        }
    }

    // -----------------------------------------------------------------------
    // What it costs
    // -----------------------------------------------------------------------

    /// <summary>
    /// The requirement that shaped the walk: counting words must cost nothing to anything else. It
    /// reads its dictionary blocks and postings with no cache at all, so the shared read cache comes
    /// out exactly as it went in - otherwise one word cloud would push out the blocks searches live
    /// on, and every search after it would pay for it.
    /// </summary>
    [TestMethod]
    public void CountingLeavesTheReadCacheAlone() {
        var dir = tempDir();
        try {
            var corpus = MakeCorpus(300, seed: 5);
            using var engine = new TextIndexEngine(dir);
            engine.SetWalFileId(Guid.NewGuid());
            var index = openIndex(engine);
            inTransaction(engine, 1000, () => { foreach (var doc in corpus) index.Add(doc.Key, doc.Value); });

            // a search first, so the cache holds the working set a real database would have
            index.SearchForIdSetUnranked(TermSet.Parse("zorbak", MinWordLength, MaxWordLength, true), true, 100);
            var before = engine.GetCacheStats();
            Assert.IsTrue(before.Entries > 0, "the search should have cached something to be disturbed");

            var counted = count(index, corpus.Keys, new WordCountOptions { MaxWords = 200 });
            Assert.IsTrue(counted.Words.Length > 0, "the count should have found words");

            var after = engine.GetCacheStats();
            Assert.AreEqual(before.Entries, after.Entries, "counting words must not add to the read cache");
            Assert.AreEqual(before.UsedBytes, after.UsedBytes, "counting words must not grow the read cache");
        } finally {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void TheBudgetStopsTheCountAndSaysSo() {
        var dir = tempDir();
        try {
            var corpus = MakeCorpus(200, seed: 9);
            using var engine = new TextIndexEngine(dir);
            engine.SetWalFileId(Guid.NewGuid());
            var index = openIndex(engine);
            inTransaction(engine, 1000, () => { foreach (var doc in corpus) index.Add(doc.Key, doc.Value); });

            var full = count(index, corpus.Keys, new WordCountOptions { MaxWords = 1000 });
            Assert.IsFalse(full.Truncated);
            Assert.IsTrue(full.PostingsEvaluated > 0);

            var budget = full.PostingsEvaluated / 4;
            var limited = count(index, corpus.Keys, new WordCountOptions { MaxWords = 1000, MaxPostingsEvaluated = budget });
            Assert.IsTrue(limited.Truncated, "the count should have run out of budget");
            Assert.IsTrue(limited.PostingsEvaluated <= budget, "the budget was overrun: " + limited.PostingsEvaluated + " > " + budget);
            var expected = Expected(corpus, corpus.Keys, new WordCountOptions { MaxWords = 1000 }).ToDictionary(w => w.Word);
            foreach (var word in limited.Words) {
                Assert.AreEqual(expected[word.Word].Documents, word.Documents, word.Word);
                Assert.AreEqual(expected[word.Word].Occurrences, word.Occurrences, word.Word);
            }
        } finally {
            Directory.Delete(dir, true);
        }
    }

    // -----------------------------------------------------------------------
    // Engines that cannot do it
    // -----------------------------------------------------------------------

    /// <summary>
    /// Every word index is wrapped in <see cref="OptimizedWordIndex"/>, so a type test for the
    /// capability would be true for all of them and false for none. The wrapper has to answer for
    /// what it wraps - this is Lucene and SQLite, which are not asked to walk their terms.
    /// </summary>
    [TestMethod]
    public void AWrappedIndexThatCannotCountSaysSo() {
        var wrapped = new OptimizedWordIndex(new IndexWithoutWordCounting());
        Assert.IsFalse(((IWordCountIndex)wrapped).CanCountWords);
        Assert.ThrowsException<NotSupportedException>(() => ((IWordCountIndex)wrapped).CountWords(Subset([1]), new WordCountOptions()));
    }

    /// <summary>An engine's index that does not implement <see cref="IWordCountIndex"/>.</summary>
    sealed class IndexWithoutWordCounting : IWordIndex {
        public string UniqueKey => "no-counting";
        public string FriendlyName => "index without word counting";
        public long PersistedTimestamp => 0;
        public void Add(int nodeId, object value) { }
        public void Remove(int nodeId, object value) { }
        public void RegisterAddDuringStateLoad(int nodeId, object value) { }
        public void RegisterRemoveDuringStateLoad(int nodeId, object value) { }
        public IdSet SearchForIdSetUnranked(TermSet search, bool orSearch, int maxWordsEval) => IdSet.Empty;
        public List<RawSearchHit> SearchForRankedHitData(TermSet search, int pageIndex, int pageSize, int maxHitsEvaluated, int maxWordsEvaluated, bool orSearch, out int totalHits) {
            totalHits = 0;
            return [];
        }
        public IEnumerable<string> SuggestSpelling(string query, bool boostCommonWords) => [];
        public void WriteNewTimestampDueToRewriteHotswap(long newTimestamp, Guid walFileId) { }
        public void SaveStateForMemoryIndexes(long logTimestamp, Guid walFileId) { }
        public void ReadStateForMemoryIndexes(Guid walFileId) { }
        public void FlagFirstCommit() { }
        public void ClearCache() { }
        public void CompressMemory() { }
        public void Dispose() { }
    }
}
