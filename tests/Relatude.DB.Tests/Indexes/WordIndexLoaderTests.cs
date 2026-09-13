using System.Text;
using Relatude.DB.Common;
using Relatude.DB.DataStores.Indexes.Trie.CharArraySearch;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
using Relatude.DB.Query.Data;

namespace Relatude.Indexes;

/// <summary>
/// The bulk loader (<see cref="WordIndexLoader"/>) fills an empty word index from a replayed log in
/// one pass. Whatever it builds must be indistinguishable from the index that indexing the same
/// documents one at a time would have given: the same hit counts, the same word counts, the same
/// search results and the same BM25 ranking - also when documents were removed and re-added
/// during the load, and when the result is written to a state file and read back.
/// </summary>
[TestClass]
public class WordIndexLoaderTests {
    const int MinWordLength = 2, MaxWordLength = 24;
    static CharArrayTrie MakeTrie(bool infix = false) => new(MinWordLength, MaxWordLength, prefixSearch: true, infixSearch: infix);

    record Op(int NodeId, string Text, bool Remove);

    // words that are prefixes of each other, of mixed length, with digits and non-ascii letters, so
    // every shape of the trie appears: an edge with a tail, a node with children, a node with
    // children where a word also ends
    static string[] MakeVocab(Random rng, int count) {
        const string letters = "abcdefghijklmnopqrstuvwxyzæøå0123456789";
        var vocab = new HashSet<string>();
        while (vocab.Count < count) {
            var len = 2 + rng.Next(10);
            var chars = new char[len];
            for (var i = 0; i < len; i++) chars[i] = letters[rng.Next(rng.Next(3) == 0 ? letters.Length : 6)]; // skewed to few letters, so prefixes collide
            var w = new string(chars);
            vocab.Add(w);
            if (w.Length > 3 && rng.Next(3) == 0) vocab.Add(w[..(w.Length - 1)]); // a prefix of an existing word
        }
        return vocab.ToArray();
    }
    static string MakeText(Random rng, string[] vocab, int wordCount) {
        var sb = new StringBuilder();
        for (var i = 0; i < wordCount; i++) {
            if (sb.Length > 0) sb.Append(rng.Next(6) == 0 ? ", " : " ");
            var w = vocab[(int)Math.Min(vocab.Length - 1, Math.Floor(Math.Pow(rng.NextDouble(), 3) * vocab.Length))]; // skewed, like real text
            sb.Append(rng.Next(4) == 0 ? w.ToUpperInvariant() : w); // case must not matter
            if (rng.Next(20) == 0) sb.Append(" the"); // a stop word
        }
        return sb.ToString();
    }
    static List<Op> MakeOps(Random rng, string[] vocab, int docs, bool withRemoves) {
        var ops = new List<Op>();
        var live = new Dictionary<int, string>();
        for (var id = 1; id <= docs; id++) {
            var t = MakeText(rng, vocab, 5 + rng.Next(60));
            ops.Add(new(id, t, false));
            live[id] = t;
        }
        if (!withRemoves) return ops;
        // deletes remove a document, updates remove the old text and add the new one
        foreach (var id in live.Keys.ToArray()) {
            var roll = rng.Next(10);
            if (roll < 2) {
                ops.Add(new(id, live[id], true));
                live.Remove(id);
            } else if (roll < 5) {
                ops.Add(new(id, live[id], true));
                var t = MakeText(rng, vocab, 5 + rng.Next(60));
                ops.Add(new(id, t, false));
                live[id] = t;
            }
        }
        // a second round for some, so a node goes add-remove-add-remove-add
        foreach (var id in live.Keys.Take(50).ToArray()) {
            ops.Add(new(id, live[id], true));
            var t = MakeText(rng, vocab, 5 + rng.Next(30));
            ops.Add(new(id, t, false));
            live[id] = t;
        }
        return ops;
    }
    static CharArrayTrie Incremental(List<Op> ops, bool infix = false) {
        var trie = MakeTrie(infix);
        foreach (var op in ops) {
            if (op.Remove) trie.DeIndexText(op.Text, op.NodeId);
            else trie.IndexText(op.Text, op.NodeId);
        }
        return trie;
    }
    static CharArrayTrie Bulk(List<Op> ops, bool infix = false, int workers = 1) {
        var trie = MakeTrie(infix);
        var loader = new WordIndexLoader(trie, workers);
        foreach (var op in ops) {
            if (op.Remove) loader.Remove(op.NodeId, op.Text);
            else loader.Add(op.NodeId, op.Text);
        }
        loader.Complete();
        return trie;
    }
    static TermSet Terms(string text, bool prefix = false, bool infix = false, bool fuzzy = false)
        => new(text.Split(' ').Select(w => new SearchTerm(w, prefix, infix, fuzzy)).ToArray());
    static int[] LiveIds(List<Op> ops) {
        var live = new HashSet<int>();
        foreach (var op in ops) {
            if (op.Remove) live.Remove(op.NodeId);
            else live.Add(op.NodeId);
        }
        return live.Order().ToArray();
    }

    static void AssertSameIndex(CharArrayTrie expected, CharArrayTrie actual, string[] vocab, int[] liveIds, bool infix) {
        foreach (var w in vocab) Assert.AreEqual(expected.GetHitCount(w), actual.GetHitCount(w), "hit count of " + w);
        var options = new WordCountOptions { MaxWords = int.MaxValue };
        AssertSameWords(expected.CountWords(IdSet.UncachableSet(liveIds), options), actual.CountWords(IdSet.UncachableSet(liveIds), options));
        var subset = liveIds.Where(i => i % 3 == 0).ToArray();
        AssertSameWords(expected.CountWords(IdSet.UncachableSet(subset), options), actual.CountWords(IdSet.UncachableSet(subset), options));
        var rng = new Random(7);
        for (var n = 0; n < 60; n++) {
            var w1 = vocab[rng.Next(vocab.Length)];
            var w2 = vocab[rng.Next(vocab.Length)];
            AssertSameIds(expected, actual, Terms(w1));
            AssertSameIds(expected, actual, Terms(w1[..Math.Min(w1.Length, 3)], prefix: true));
            AssertSameIds(expected, actual, Terms(w1, fuzzy: true));
            if (infix && w1.Length > 3) AssertSameIds(expected, actual, Terms(w1[1..^1], infix: true));
            AssertSameIds(expected, actual, Terms(w1 + " " + w2), orSearch: true);
            AssertSameIds(expected, actual, Terms(w1 + " " + w2), orSearch: false);
            AssertSameRanking(expected, actual, Terms(w1 + " " + w2));
        }
    }
    static void AssertSameWords(WordCountSet e, WordCountSet a) {
        Assert.AreEqual(e.IndexDocuments, a.IndexDocuments, "documents in index");
        Assert.AreEqual(e.DistinctWords, a.DistinctWords, "distinct words");
        // both are in the total order of WordCount.Descending, so they compare as lists
        CollectionAssert.AreEqual(
            e.Words.Select(w => (w.Word, w.Documents, w.Occurrences, w.DocumentsInIndex)).ToList(),
            a.Words.Select(w => (w.Word, w.Documents, w.Occurrences, w.DocumentsInIndex)).ToList());
    }
    static void AssertSameIds(CharArrayTrie e, CharArrayTrie a, TermSet terms, bool orSearch = false) {
        // no cap on the words evaluated: a cap would cut the fuzzy and prefix expansions in the
        // order the trie happens to visit its children, and the two tries order them differently
        CollectionAssert.AreEquivalent(e.SearchIdsUnsorted(terms, orSearch, int.MaxValue).ToList(), a.SearchIdsUnsorted(terms, orSearch, int.MaxValue).ToList(), terms.ToString());
    }
    static void AssertSameRanking(CharArrayTrie e, CharArrayTrie a, TermSet terms) {
        var er = e.Search(terms, out var et, sorted: true, 0, 0, int.MaxValue, int.MaxValue, orSearch: true).ToList();
        var ar = a.Search(terms, out var at, sorted: true, 0, 0, int.MaxValue, int.MaxValue, orSearch: true).ToList();
        Assert.AreEqual(et, at, "total hits of " + terms);
        Assert.AreEqual(er.Count, ar.Count, "hits of " + terms);
        // equal scores may come in either order, so compare in (score, id) order
        var es = er.OrderByDescending(x => x.Value).ThenBy(x => x.Key).ToList();
        var @as = ar.OrderByDescending(x => x.Value).ThenBy(x => x.Key).ToList();
        for (var i = 0; i < es.Count; i++) {
            Assert.AreEqual(es[i].Key, @as[i].Key, "ranked id " + i + " of " + terms);
            Assert.AreEqual(es[i].Value, @as[i].Value, 1e-9, "score " + i + " of " + terms);
        }
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public void BulkLoad_EqualsIncrementalIndexing(int workers) {
        var rng = new Random(1);
        var vocab = MakeVocab(rng, 3000);
        var ops = MakeOps(rng, vocab, 2000, withRemoves: false);
        var expected = Incremental(ops);
        var actual = Bulk(ops, workers: workers);
        Assert.AreEqual(expected.GetUniqueWordCount(), actual.GetUniqueWordCount());
        AssertSameIndex(expected, actual, vocab, LiveIds(ops), infix: false);
    }

    // removes and re-adds are the operations whose order matters: with several workers they are
    // gathered out of order and put back in order by their sequence numbers when merged
    [DataTestMethod]
    [DataRow(1)]
    [DataRow(3)]
    [DataRow(8)]
    public void BulkLoad_EqualsIncrementalIndexing_WithRemovesAndReAdds(int workers) {
        for (var seed = 2; seed < 6; seed++) {
            var rng = new Random(seed);
            var vocab = MakeVocab(rng, 2000);
            var ops = MakeOps(rng, vocab, 1500, withRemoves: true);
            Assert.IsTrue(ops.Any(o => o.Remove));
            AssertSameIndex(Incremental(ops), Bulk(ops, workers: workers), vocab, LiveIds(ops), infix: false);
        }
    }

    [TestMethod]
    public void BulkLoad_EqualsIncrementalIndexing_WithInfixSearch() {
        var rng = new Random(3);
        var vocab = MakeVocab(rng, 800);
        var ops = MakeOps(rng, vocab, 500, withRemoves: true);
        AssertSameIndex(Incremental(ops, infix: true), Bulk(ops, infix: true, workers: 3), vocab, LiveIds(ops), infix: true);
    }

    // every operation of one node in one worker, or spread over several: both must come out the same
    [DataTestMethod]
    [DataRow(1)]
    [DataRow(4)]
    public void BulkLoad_NodeAddedRemovedAndReAdded_EndsWithItsLastText(int workers) {
        var ops = new List<Op> {
            new(1, "alpha beta", false),
            new(2, "beta gamma", false),
            new(1, "alpha beta", true),
            new(1, "gamma delta delta", false),
            new(2, "beta gamma", true),
            new(2, "epsilon", false),
            new(2, "epsilon", true),
            new(3, "alpha", false),
        };
        var trie = Bulk(ops, workers: workers);
        Assert.AreEqual(0, trie.GetHitCount("beta"), "beta was only in removed texts");
        Assert.AreEqual(0, trie.GetHitCount("epsilon"));
        CollectionAssert.AreEquivalent(new[] { 3 }, trie.SearchIdsUnsorted(Terms("alpha"), false, 10).ToList());
        CollectionAssert.AreEquivalent(new[] { 1 }, trie.SearchIdsUnsorted(Terms("gamma"), false, 10).ToList());
        CollectionAssert.AreEquivalent(new[] { 1 }, trie.SearchIdsUnsorted(Terms("delta"), false, 10).ToList());
        var ranked = trie.Search(Terms("delta"), out var total, sorted: true, 0, 0, int.MaxValue, int.MaxValue, orSearch: true).ToList();
        Assert.AreEqual(1, total);
        AssertSameIndex(Incremental(ops), trie, ["alpha", "beta", "gamma", "delta", "epsilon"], LiveIds(ops), infix: false);
    }

    [TestMethod]
    public void BulkLoad_WordLeftWithoutHits_IsNotKept() {
        var trie = MakeTrie();
        var loader = new WordIndexLoader(trie, 2);
        loader.Add(1, "alpha beta");
        loader.Add(2, "beta gamma");
        loader.Remove(1, "alpha beta");
        loader.Complete();
        Assert.IsFalse(trie.Contains("alpha"));
        Assert.AreEqual(1, trie.GetHitCount("beta"));
        Assert.AreEqual(1, trie.GetHitCount("gamma"));
        Assert.AreEqual(2, trie.GetUniqueWordCount());
        CollectionAssert.AreEquivalent(new[] { 2 }, trie.SearchIdsUnsorted(Terms("beta"), false, 10).ToList());
        Assert.AreEqual(0, trie.SearchIdsUnsorted(Terms("alpha"), false, 10).Count);
    }

    [TestMethod]
    public void BulkLoad_WithNothingGathered_LeavesTheIndexEmpty() {
        var trie = MakeTrie();
        var loader = new WordIndexLoader(trie, 2);
        loader.Add(1, "");
        loader.Complete();
        Assert.IsTrue(trie.IsEmpty);
        Assert.AreEqual(0, trie.SearchIdsUnsorted(Terms("anything"), false, 10).Count);
    }

    [TestMethod]
    public void BulkLoad_RequiresAnEmptyIndex_AndOneLoadPerLoader() {
        var trie = MakeTrie();
        trie.IndexText("alpha", 1);
        Assert.ThrowsException<InvalidOperationException>(() => new WordIndexLoader(trie));
        var empty = MakeTrie();
        var loader = new WordIndexLoader(empty, 2);
        loader.Add(1, "alpha");
        loader.Complete();
        Assert.ThrowsException<InvalidOperationException>(() => loader.Add(2, "beta"));
        Assert.ThrowsException<InvalidOperationException>(loader.Complete);
    }

    [TestMethod]
    public void BulkLoad_Disposed_StopsWithoutBuilding() {
        var trie = MakeTrie();
        var loader = new WordIndexLoader(trie, 2);
        for (var i = 1; i <= 500; i++) loader.Add(i, "alpha beta gamma " + i);
        loader.Dispose();
        Assert.IsTrue(trie.IsEmpty);
        Assert.ThrowsException<InvalidOperationException>(() => loader.Add(1, "alpha"));
    }

    [TestMethod]
    public void BulkLoadedIndex_RoundTripsThroughItsStateFile() {
        var rng = new Random(4);
        var vocab = MakeVocab(rng, 1500);
        var ops = MakeOps(rng, vocab, 800, withRemoves: true);
        var bulk = Bulk(ops, workers: 3);
        var io = new IOProviderMemory();
        string[] key = ["wordindex.bin"];
        using (var stream = io.OpenAppend(key)) bulk.WriteState(stream);
        var read = MakeTrie();
        using (var stream = io.OpenRead(key, 0)) read.ReadState(stream);
        AssertSameIndex(Incremental(ops), read, vocab, LiveIds(ops), infix: false);
    }

    [TestMethod]
    public void BulkLoadedIndex_TakesFurtherDocumentsLikeAnyOther() {
        var rng = new Random(5);
        var vocab = MakeVocab(rng, 1500);
        var ops = MakeOps(rng, vocab, 800, withRemoves: false);
        var expected = Incremental(ops);
        var actual = Bulk(ops, workers: 3);
        var more = MakeOps(new Random(6), vocab, 200, withRemoves: true).Select(o => o with { NodeId = o.NodeId + 10000 }).ToList();
        foreach (var op in more) {
            if (op.Remove) {
                expected.DeIndexText(op.Text, op.NodeId);
                actual.DeIndexText(op.Text, op.NodeId);
            } else {
                expected.IndexText(op.Text, op.NodeId);
                actual.IndexText(op.Text, op.NodeId);
            }
        }
        AssertSameIndex(expected, actual, vocab, LiveIds(ops.Concat(more).ToList()), infix: false);
    }
}
