using Relatude.DB.Common;
using Relatude.DB.DataStores.Indexes.Trie.CharArraySearch;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.Query.Data;
using static Relatude.Utils.WordCountOracle;

namespace Relatude.Indexes;

/// <summary>
/// Counting the words of a set of nodes out of the memory word index, checked against
/// <see cref="Relatude.Utils.WordCountOracle"/> - the counts worked out straight from the text.
/// The two must agree exactly: the whole point of reading the words out of the index is that they
/// are the words a search would find.
/// </summary>
[TestClass]
public class WordCountTests {
    static CharArrayTrie MakeTrie() => new(MinWordLength, MaxWordLength, prefixSearch: true, infixSearch: false);

    static CharArrayTrie Index(Dictionary<int, string> corpus) {
        var trie = MakeTrie();
        foreach (var doc in corpus) trie.IndexText(doc.Value, doc.Key);
        return trie;
    }

    // -----------------------------------------------------------------------
    // The index agrees with the text
    // -----------------------------------------------------------------------

    [TestMethod]
    public void WholeIndexMatchesTheText() {
        var corpus = MakeCorpus(120);
        var trie = Index(corpus);
        var options = new WordCountOptions { MaxWords = 1000 };
        var result = trie.CountWords(Subset(corpus.Keys), options);
        AssertSame(Expected(corpus, corpus.Keys, options), result.Words);
    }

    [TestMethod]
    public void SubsetMatchesTheText() {
        var corpus = MakeCorpus(120);
        var trie = Index(corpus);
        var subset = corpus.Keys.Where(id => id % 3 == 0).ToArray();
        var options = new WordCountOptions { MaxWords = 1000 };
        var result = trie.CountWords(Subset(subset), options);
        AssertSame(Expected(corpus, subset, options), result.Words);
        // and the whole-index count of every word is the one the whole corpus gives, not the subset's
        var wholeIndex = CountFromText(corpus.Values);
        foreach (var word in result.Words) Assert.AreEqual(wholeIndex[word.Word].Documents, word.DocumentsInIndex, word.Word);
    }

    /// <summary>A set small enough to stay a plain array, where IdSet.Has scans instead of bit testing.</summary>
    [TestMethod]
    public void SmallSubsetMatchesTheText() {
        var corpus = MakeCorpus(400);
        var trie = Index(corpus);
        var subset = new[] { 3, 17, 99, 400 };
        var options = new WordCountOptions { MaxWords = 1000 };
        AssertSame(Expected(corpus, subset, options), trie.CountWords(Subset(subset), options).Words);
    }

    /// <summary>A set large and dense enough to become a bit set, which Has probes a different way.</summary>
    [TestMethod]
    public void LargeSubsetMatchesTheText() {
        var corpus = MakeCorpus(1000);
        var trie = Index(corpus);
        var subset = corpus.Keys.Where(id => id % 2 == 0).ToArray();
        var options = new WordCountOptions { MaxWords = 1000 };
        AssertSame(Expected(corpus, subset, options), trie.CountWords(Subset(subset), options).Words);
    }

    [TestMethod]
    public void StopWordsAndTooShortWordsAreNotThere() {
        var corpus = MakeCorpus(50);
        var trie = Index(corpus);
        var result = trie.CountWords(Subset(corpus.Keys), new WordCountOptions { MaxWords = 1000 });
        // every document holds some of these, and none of them is indexed
        Assert.IsFalse(result.Words.Any(w => w.Word is "the" or "and"), "a stop word came back");
        Assert.IsFalse(result.Words.Any(w => w.Word.Length < MinWordLength), "a word below the index minimum length came back");
    }

    // -----------------------------------------------------------------------
    // Deindexing
    // -----------------------------------------------------------------------

    [TestMethod]
    public void DeindexedDocumentsAreGone() {
        var corpus = MakeCorpus(80);
        var trie = Index(corpus);
        // the lazy removal in HitCounts keeps a removed hit in place until the next write reconciles
        // it, so counting straight after a removal is exactly the case that has to honour it
        foreach (var id in corpus.Keys.Where(id => id % 4 == 0).ToArray()) {
            trie.DeIndexText(corpus[id], id);
            corpus.Remove(id);
        }
        var options = new WordCountOptions { MaxWords = 1000 };
        AssertSame(Expected(corpus, corpus.Keys, options), trie.CountWords(Subset(corpus.Keys), options).Words);
    }

    [TestMethod]
    public void AWordLeftWithNoDocumentsIsNotCounted() {
        var trie = MakeTrie();
        trie.IndexText("zorbak flimzel", 1);
        trie.IndexText("zorbak grunthop", 2);
        trie.DeIndexText("zorbak flimzel", 1); // flimzel now has an empty hit list the trie keeps
        var result = trie.CountWords(Subset([1, 2]), new WordCountOptions { MaxWords = 100 });
        CollectionAssert.AreEquivalent(new[] { "zorbak", "grunthop" }, result.Words.Select(w => w.Word).ToArray());
        Assert.AreEqual(1, result.Words.Single(w => w.Word == "zorbak").Documents);
    }

    // -----------------------------------------------------------------------
    // The options
    // -----------------------------------------------------------------------

    [TestMethod]
    public void TheCapKeepsTheCommonestAndTheOrderIsTotal() {
        var corpus = MakeCorpus(200);
        var trie = Index(corpus);
        var options = new WordCountOptions { MaxWords = 5 };
        var result = trie.CountWords(Subset(corpus.Keys), options);
        Assert.AreEqual(5, result.Words.Length);
        AssertSame(Expected(corpus, corpus.Keys, options), result.Words);
        // asking twice gives the same list in the same order, ties included
        AssertSame(result.Words, trie.CountWords(Subset(corpus.Keys), options).Words);
    }

    [TestMethod]
    public void MinimumDocumentsMinimumLengthAndIgnoreAllApply() {
        var corpus = MakeCorpus(150);
        var trie = Index(corpus);
        var options = new WordCountOptions {
            MaxWords = 1000,
            MinDocuments = 40,
            MinWordLength = 7,
            Ignore = new HashSet<string> { "grunthop", "flimzelian" },
        };
        var result = trie.CountWords(Subset(corpus.Keys), options);
        AssertSame(Expected(corpus, corpus.Keys, options), result.Words);
        Assert.IsTrue(result.Words.Length > 0, "the corpus should still have words left to count");
        Assert.IsTrue(result.Words.All(w => w.Documents >= 40 && w.Word.Length >= 7));
        Assert.IsFalse(result.Words.Any(w => w.Word is "grunthop" or "flimzelian"));
    }

    [TestMethod]
    public void DistinctWordsCountsWhatTheSetHoldsNotWhatComesBack() {
        var corpus = MakeCorpus(150);
        var trie = Index(corpus);
        var subset = corpus.Keys.Where(id => id % 5 == 0).ToArray();
        var result = trie.CountWords(Subset(subset), new WordCountOptions { MaxWords = 3 });
        Assert.AreEqual(3, result.Words.Length);
        Assert.AreEqual(DistinctIn(corpus, subset), result.DistinctWords);
    }

    [TestMethod]
    public void TheBudgetStopsTheCountAndSaysSo() {
        var corpus = MakeCorpus(300);
        var trie = Index(corpus);
        var full = trie.CountWords(Subset(corpus.Keys), new WordCountOptions { MaxWords = 1000 });
        Assert.IsFalse(full.Truncated);
        Assert.IsTrue(full.PostingsEvaluated > 0);

        var budget = full.PostingsEvaluated / 4;
        var limited = trie.CountWords(Subset(corpus.Keys), new WordCountOptions { MaxWords = 1000, MaxPostingsEvaluated = budget });
        Assert.IsTrue(limited.Truncated, "the count should have run out of budget");
        Assert.IsTrue(limited.PostingsEvaluated <= budget, "the budget was overrun: " + limited.PostingsEvaluated + " > " + budget);
        // what it did count, it counted right
        var expected = Expected(corpus, corpus.Keys, new WordCountOptions { MaxWords = 1000 }).ToDictionary(w => w.Word);
        foreach (var word in limited.Words) {
            Assert.AreEqual(expected[word.Word].Documents, word.Documents, word.Word);
            Assert.AreEqual(expected[word.Word].Occurrences, word.Occurrences, word.Word);
        }
    }

    [TestMethod]
    public void AnEmptySetHasNoWords() {
        var corpus = MakeCorpus(20);
        var trie = Index(corpus);
        var result = trie.CountWords(Subset([]), new WordCountOptions());
        Assert.AreEqual(0, result.Words.Length);
        Assert.AreEqual(0, result.DistinctWords);
        Assert.AreEqual(0, result.PostingsEvaluated);
        Assert.IsFalse(result.Truncated);
    }

    [TestMethod]
    public void ASetOfNodesThatAreNotIndexedHasNoWords() {
        var corpus = MakeCorpus(20);
        var trie = Index(corpus);
        var result = trie.CountWords(Subset([9001, 9002]), new WordCountOptions());
        Assert.AreEqual(0, result.Words.Length);
        Assert.AreEqual(0, result.DistinctWords);
        Assert.IsTrue(result.PostingsEvaluated > 0, "it still had to look at the index to find that out");
    }

    // -----------------------------------------------------------------------
    // Occurrences
    // -----------------------------------------------------------------------

    [TestMethod]
    public void OccurrencesCountRepeatsWithinADocument() {
        var trie = MakeTrie();
        trie.IndexText("zorbak zorbak zorbak snazzle", 1);
        trie.IndexText("zorbak snazzle", 2);
        var result = trie.CountWords(Subset([1, 2]), new WordCountOptions { MaxWords = 10 });
        var zorbak = result.Words.Single(w => w.Word == "zorbak");
        Assert.AreEqual(2, zorbak.Documents);
        Assert.AreEqual(4, zorbak.Occurrences);
        var snazzle = result.Words.Single(w => w.Word == "snazzle");
        Assert.AreEqual(2, snazzle.Documents);
        Assert.AreEqual(2, snazzle.Occurrences);
        // the commonest by documents first, and on a tie the commonest by occurrences
        Assert.AreEqual("zorbak", result.Words[0].Word);
    }

    [TestMethod]
    public void ADocumentRepeatingAWordPast255CountsWhatTheIndexKept() {
        var trie = MakeTrie();
        trie.IndexText(string.Join(' ', Enumerable.Repeat("zorbak", 300)), 1);
        var result = trie.CountWords(Subset([1]), new WordCountOptions());
        Assert.AreEqual(1, result.Words.Length);
        Assert.AreEqual(255, result.Words[0].Occurrences, "the index stores one byte per document per word");
    }
}
