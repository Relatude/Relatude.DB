using Relatude.DB.DataStores.Indexes.Trie.CharArraySearch;
using Relatude.DB.Common;
using Relatude.DB.IO;

namespace Tests;

[TestClass]
public class CharArrayTrieTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>Creates a trie with typical settings (prefix + infix enabled).</summary>
    static CharArrayTrie MakeTrie(bool prefix = true, bool infix = true) =>
        new(minWordLength: 3, maxWordLength: 20, prefixSearch: prefix, infixSearch: infix);

    static TermSet Exact(string word) =>
        new([new SearchTerm(word, prefix: false, infix: false, fuzzy: false)]);

    static TermSet Prefix(string word) =>
        new([new SearchTerm(word, prefix: true, infix: false, fuzzy: false)]);

    static TermSet Infix(string word) =>
        new([new SearchTerm(word, prefix: false, infix: true, fuzzy: false)]);

    static TermSet Fuzzy(string word) =>
        new([new SearchTerm(word, prefix: false, infix: false, fuzzy: true)]);

    static TermSet Multi(params (string word, bool prefix, bool infix, bool fuzzy)[] terms) =>
        new(terms.Select(t => new SearchTerm(t.word, t.prefix, t.infix, t.fuzzy)).ToArray());

    // Synthetic vocabulary – 200 made-up words covering varied lengths and patterns
    static readonly string[] Vocab =
    [
        "zorbak", "flimzel", "grunthop", "snazzle", "blivort",
        "crumfex", "drazznik", "elgooth", "frendal", "glorpix",
        "hunkavar", "ibloon", "jostrek", "klumfar", "lentrop",
        "moogish", "narplex", "ortwist", "plorfex", "quibzar",
        "renstop", "spiblunk", "trazwick", "umblotz", "vexplor",
        "worgnak", "xibflop", "yondril", "zankthr", "blorfun",
        "crimplex", "dofbling", "entrox", "flarbing", "ginkwop",
        "haxtrob", "igluntz", "jembrix", "klopfan", "lumbwick",
        "marvzol", "nomblick", "orpifax", "plundex", "quentzak",
        "romblix", "slindrop", "troxfem", "undrixp", "vorplak",
        "wamblex", "xordunk", "yimbrak", "zoltrex", "blixfar",
        "cranmop", "drubzink", "elvorth", "frondix", "glumbak",
        "hobzink", "inkworf", "jalvex", "krimble", "lorbtex",
        "muzblor", "noxflim", "oblinth", "prazzok", "quilbaf",
        "stronfex", "tuvlick", "urpzank", "vimblor", "wexflun",
        "xantrop", "yelkfan", "zimblox", "broflex", "clundix",
        "drimzok", "ebluxar", "famzick", "grimblot", "horbzak",
        "izflopp", "juznack", "klorvex", "lumbzar", "moxflip",
        "nizblop", "oblaxer", "plubzik", "quorblax", "rinflex",
        "skimblex", "trubzok", "uxblart", "vibzank", "worfblim",
        "ximblack", "yarbzok", "zuflink", "blakzor", "criftex",
        "dramblix", "elbfunk", "frobzank", "glinzop", "hulkfax",
        "imzoblar", "jibzorf", "krumblax", "livzunk", "mabzorf",
        "nufblox", "olzimble", "prafzok", "quirnblax", "rolfzink",
        "stozblim", "trufzank", "ulbfroxi", "vizblork", "wafzimb",
        "xorfblum", "yablzink", "zolbfrax", "briflunk", "clorzank",
        "dufblimz", "elvzoran", "fromzick", "globzink", "hixblorf",
        "imzolfax", "jorzblim", "kurzblank", "lufzorbi", "marzblop",
        "nozlimb", "oxblurz", "plinzorf", "qubzlork", "rimzblaf",
        "slormzik", "traxbloz", "ulzimfax", "vorzblim", "wixblorf",
        "xubzlank", "yorzblim", "zomblixar", "brixolfan", "clumbzark",
        "drumzolfax", "elbzurink", "fromlaxib", "gorzblumix", "hufzlimba",
        "imzolfabil", "jorzblimen", "kuvzlimbax", "lurboxzim", "mazblorfin",
        "norzblumix", "oxzlimfab", "plumzorfax", "qurzblimox", "rimzolfan",
        "slimbzorfax", "trumzolbix", "ulzimborfax", "vorzblumix", "wumzolfab",
        "xorzblimfax", "yumzolfab", "zumblorfax", "brixolblum", "clumzorbix",
        "drumzolfab", "elzumblorfax", "frolzimbax", "gumzolfab", "humzolfab"
    ];

    /// <summary>Builds a synthetic sentence from words at the given indices (with repetition).</summary>
    static string MakeSentence(int seed, int wordCount = 2000)
    {
        var rng = new Random(seed);
        return string.Join(" ", Enumerable.Range(0, wordCount).Select(_ => Vocab[rng.Next(Vocab.Length)]));
    }

    // -----------------------------------------------------------------------
    // Basic index / search
    // -----------------------------------------------------------------------

    [TestMethod]
    public void IndexAndExactSearch_FindsIndexedWords()
    {
        using var trie = MakeTrie();
        trie.IndexText("zorbak flimzel grunthop", nodeId: 1);

        var ids = trie.SearchIdsUnsorted(Exact("zorbak"), orSearch: false, maxWordsEval: 100);
        Assert.IsTrue(ids.Contains(1));

        ids = trie.SearchIdsUnsorted(Exact("flimzel"), orSearch: false, maxWordsEval: 100);
        Assert.IsTrue(ids.Contains(1));
    }

    [TestMethod]
    public void ExactSearch_NotIndexedWord_ReturnsEmpty()
    {
        using var trie = MakeTrie();
        trie.IndexText("zorbak flimzel", nodeId: 1);
        var ids = trie.SearchIdsUnsorted(Exact("xibflop"), orSearch: false, maxWordsEval: 100);
        Assert.AreEqual(0, ids.Count);
    }

    [TestMethod]
    public void PrefixSearch_FindsAllMatchingNodes()
    {
        using var trie = MakeTrie();
        trie.IndexText("zorbak zorbit zorbling", nodeId: 1);
        trie.IndexText("zorbak zordust", nodeId: 2);

        var ids = trie.SearchIdsUnsorted(Prefix("zorb"), orSearch: false, maxWordsEval: 100);
        Assert.IsTrue(ids.Contains(1));
        Assert.IsTrue(ids.Contains(2));
    }

    [TestMethod]
    public void InfixSearch_FindsWordsByInternalSubstring()
    {
        using var trie = MakeTrie(infix: true);
        // Index same word for two nodes: the second indexing populates the infix trie
        trie.IndexText("grunthop snazzle blorfun", nodeId: 5);
        trie.IndexText("grunthop crumfex", nodeId: 6);

        // "unth" is an infix of "grunthop"
        var ids = trie.SearchIdsUnsorted(Infix("unth"), orSearch: false, maxWordsEval: 100);
        Assert.IsTrue(ids.Contains(5) || ids.Contains(6),
            "At least one node should be found via infix search for 'unth' inside 'grunthop'.");
    }

    // -----------------------------------------------------------------------
    // Multi-document indexing
    // -----------------------------------------------------------------------

    [TestMethod]
    public void MultipleDocuments_ExactSearch_ReturnsCorrectSubset()
    {
        using var trie = MakeTrie();
        for (int i = 1; i <= 1000; i++)
            trie.IndexText(MakeSentence(seed: i * 7), nodeId: i);

        // "zorbak" appears in at least some documents; verify only those are returned
        var ids = trie.SearchIdsUnsorted(Exact("zorbak"), orSearch: false, maxWordsEval: 10000);

        foreach (var id in ids)
        {
            var text = MakeSentence(seed: id * 7);
            Assert.IsTrue(text.Contains("zorbak"),
                $"Node {id} was returned but its text does not contain 'zorbak'.");
        }
    }

    [TestMethod]
    public void SearchCount_MatchesSearchIds()
    {
        using var trie = MakeTrie();
        for (int i = 1; i <= 1000; i++)
            trie.IndexText(MakeSentence(seed: i * 3), nodeId: i);

        int count = trie.SearchCount(Exact("blorfun"), orSearch: false, maxWordsEval: 10000);
        var ids = trie.SearchIdsUnsorted(Exact("blorfun"), orSearch: false, maxWordsEval: 10000);
        Assert.AreEqual(ids.Count, count);
    }

    // -----------------------------------------------------------------------
    // De-indexing
    // -----------------------------------------------------------------------

    [TestMethod]
    public void DeIndex_SingleDocument_WordNoLongerFound()
    {
        using var trie = MakeTrie();
        const string text = "zorbak flimzel grunthop";
        trie.IndexText(text, nodeId: 1);
        trie.DeIndexText(text, nodeId: 1);

        var ids = trie.SearchIdsUnsorted(Exact("zorbak"), orSearch: false, maxWordsEval: 100);
        Assert.IsFalse(ids.Contains(1));
    }

    [TestMethod]
    public void DeIndex_OneDocument_OtherDocumentsUnaffected()
    {
        using var trie = MakeTrie();
        const string shared = "zorbak crumfex";
        trie.IndexText(shared, nodeId: 1);
        trie.IndexText(shared, nodeId: 2);

        trie.DeIndexText(shared, nodeId: 1);

        var ids = trie.SearchIdsUnsorted(Exact("zorbak"), orSearch: false, maxWordsEval: 100);
        Assert.IsFalse(ids.Contains(1), "Node 1 should have been de-indexed.");
        Assert.IsTrue(ids.Contains(2), "Node 2 should still be indexed.");
    }

    [TestMethod]
    public void DeIndex_AllDocuments_TrieReturnsEmpty()
    {
        using var trie = MakeTrie();
        for (int i = 1; i <= 1000; i++)
            trie.IndexText("zorbak flimzel", nodeId: i);
        for (int i = 1; i <= 1000; i++)
            trie.DeIndexText("zorbak flimzel", nodeId: i);

        var ids = trie.SearchIdsUnsorted(Exact("zorbak"), orSearch: false, maxWordsEval: 100);
        Assert.AreEqual(0, ids.Count);
    }

    [TestMethod]
    public void DeIndex_LargeSet_VerifyConsistency()
    {
        using var trie = MakeTrie();
        var texts = Enumerable.Range(1, 1000).ToDictionary(i => i, i => MakeSentence(seed: i));

        foreach (var kv in texts) trie.IndexText(kv.Value, nodeId: kv.Key);

        // De-index every even node
        foreach (var kv in texts.Where(kv => kv.Key % 2 == 0))
            trie.DeIndexText(kv.Value, nodeId: kv.Key);

        // Verify odd nodes still found, even nodes gone – for every vocab word
        foreach (var vocabWord in Vocab)
        {
            var ids = trie.SearchIdsUnsorted(Exact(vocabWord), orSearch: false, maxWordsEval: 10000);
            foreach (var id in ids)
            {
                Assert.IsTrue(id % 2 != 0, $"Even node {id} should have been de-indexed.");
                Assert.IsTrue(texts[id].Contains(vocabWord),
                    $"Node {id} returned for '{vocabWord}' but text does not contain it.");
            }
            // Ensure no even node with the word slipped through
            foreach (var kv in texts.Where(kv => kv.Key % 2 == 0 && kv.Value.Contains(vocabWord)))
                Assert.IsFalse(ids.Contains(kv.Key),
                    $"De-indexed node {kv.Key} still returned for '{vocabWord}'.");
        }
    }

    // -----------------------------------------------------------------------
    // OR / AND search
    // -----------------------------------------------------------------------

    [TestMethod]
    public void OrSearch_ReturnsUnionOfDocuments()
    {
        using var trie = MakeTrie();
        trie.IndexText("zorbak", nodeId: 1);
        trie.IndexText("flimzel", nodeId: 2);
        trie.IndexText("zorbak flimzel", nodeId: 3);

        var query = Multi(("zorbak", false, false, false), ("flimzel", false, false, false));
        var ids = trie.SearchIdsUnsorted(query, orSearch: true, maxWordsEval: 100);
        Assert.IsTrue(ids.Contains(1));
        Assert.IsTrue(ids.Contains(2));
        Assert.IsTrue(ids.Contains(3));
    }

    [TestMethod]
    public void AndSearch_ReturnsOnlyDocumentsWithAllTerms()
    {
        using var trie = MakeTrie();
        trie.IndexText("zorbak", nodeId: 1);
        trie.IndexText("flimzel", nodeId: 2);
        trie.IndexText("zorbak flimzel", nodeId: 3);

        var query = Multi(("zorbak", false, false, false), ("flimzel", false, false, false));
        var ids = trie.SearchIdsUnsorted(query, orSearch: false, maxWordsEval: 100);
        Assert.IsFalse(ids.Contains(1));
        Assert.IsFalse(ids.Contains(2));
        Assert.IsTrue(ids.Contains(3));
    }

    // -----------------------------------------------------------------------
    // Contains / GetHitCount
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Contains_ReturnsTrueForIndexedWord()
    {
        using var trie = MakeTrie();
        trie.IndexText("zorbak flimzel", nodeId: 1);
        Assert.IsTrue(trie.Contains("zorbak"));
    }

    [TestMethod]
    public void Contains_ReturnsFalseForNonIndexedWord()
    {
        using var trie = MakeTrie();
        trie.IndexText("zorbak", nodeId: 1);
        Assert.IsFalse(trie.Contains("xibflop"));
    }

    [TestMethod]
    public void GetHitCount_ReflectsDocumentCount()
    {
        using var trie = MakeTrie();
        trie.IndexText("zorbak", nodeId: 1);
        trie.IndexText("zorbak flimzel", nodeId: 2);
        trie.IndexText("flimzel", nodeId: 3);

        Assert.AreEqual(2, trie.GetHitCount("zorbak"));
        Assert.AreEqual(2, trie.GetHitCount("flimzel"));
    }

    [TestMethod]
    public void GetHitCount_AfterDeIndex_Decrements()
    {
        using var trie = MakeTrie();
        trie.IndexText("zorbak", nodeId: 1);
        trie.IndexText("zorbak", nodeId: 2);
        trie.DeIndexText("zorbak", nodeId: 1);

        Assert.AreEqual(1, trie.GetHitCount("zorbak"));
    }

    // -----------------------------------------------------------------------
    // GetUniqueWordCount
    // -----------------------------------------------------------------------

    [TestMethod]
    public void GetUniqueWordCount_CountsDistinctWords()
    {
        using var trie = MakeTrie();
        trie.IndexText("zorbak flimzel grunthop", nodeId: 1);
        trie.IndexText("zorbak snazzle", nodeId: 2); // "zorbak" duplicate

        // 4 unique words: zorbak, flimzel, grunthop, snazzle
        Assert.AreEqual(4, trie.GetUniqueWordCount());
    }

    // -----------------------------------------------------------------------
    // Suggest
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Suggest_ReturnsReasonableCompletions()
    {
        using var trie = MakeTrie();
        trie.IndexText("zorbak zorbling zordust", nodeId: 1);

        // "zorba" is one edit away from the indexed word "zorbak"
        var suggestions = trie.Suggest("zorba", boostCommonWords: false).ToList();
        Assert.IsTrue(suggestions.Count > 0, "Expected at least one suggestion for 'zorba' (one edit from 'zorbak').");
        foreach (var s in suggestions)
            Assert.IsTrue(s.StartsWith("zor"),
                $"Suggestion '{s}' does not start with 'zor'.");
    }

    // -----------------------------------------------------------------------
    // Scored Search
    // -----------------------------------------------------------------------

    [TestMethod]
    public void ScoredSearch_ReturnsResultsWithPositiveScores()
    {
        using var trie = MakeTrie();
        for (int i = 1; i <= 1000; i++)
            trie.IndexText(MakeSentence(seed: i * 5), nodeId: i);

        var result = trie.Search(Exact("zorbak"), out int total, sorted: true,
            skip: 0, take: 100, maxHitsEval: 10000, maxWordsEval: 10000, orSearch: false).ToList();

        Assert.AreEqual(result.Count, 100, "take:100 should return exactly 100 results.");
        Assert.IsTrue(total >= result.Count, "total should be >= returned result count.");
        foreach (var kv in result)
            Assert.IsTrue(kv.Value >= 0, $"Score for node {kv.Key} should be non-negative.");
    }

    [TestMethod]
    public void ScoredSearch_SortedResultsAreOrderedDescending()
    {
        using var trie = MakeTrie();
        for (int i = 1; i <= 1000; i++)
            trie.IndexText(MakeSentence(seed: i * 11), nodeId: i);

        var results = trie.Search(Exact("flimzel"), out _, sorted: true,
            skip: 0, take: 10000, maxHitsEval: 10000, maxWordsEval: 10000, orSearch: false).ToList();

        for (int i = 1; i < results.Count; i++)
            Assert.IsTrue(results[i - 1].Value >= results[i].Value,
                "Results should be sorted descending by score.");
    }

    [TestMethod]
    public void ScoredSearch_SkipAndTake_WorkCorrectly()
    {
        using var trie = MakeTrie();
        for (int i = 1; i <= 1000; i++)
            trie.IndexText("zorbak " + MakeSentence(seed: i, wordCount: 50), nodeId: i);

        var all = trie.Search(Exact("zorbak"), out _, sorted: false,
            skip: 0, take: 0, maxHitsEval: 100000, maxWordsEval: 1000, orSearch: false).ToList();

        var paged = trie.Search(Exact("zorbak"), out _, sorted: false,
            skip: 5, take: 10, maxHitsEval: 100000, maxWordsEval: 1000, orSearch: false).ToList();

        Assert.AreEqual(Math.Min(10, Math.Max(0, all.Count - 5)), paged.Count);
    }

    // -----------------------------------------------------------------------
    // Serialisation round-trip (WriteState / ReadState)
    // -----------------------------------------------------------------------

    [TestMethod]
    public void WriteReadState_ExactSearch_Consistent()
    {
        using var trie = MakeTrie();
        for (int i = 1; i <= 1000; i++)
            trie.IndexText(MakeSentence(seed: i * 13), nodeId: i);

        // Capture pre-serialisation results
        var before = trie.SearchIdsUnsorted(Exact("zorbak"), orSearch: false, maxWordsEval: 10000);

        // Serialise
        var ms = new MemoryStream();
        using var writer = new StoreStreamMemoryWrite("test", ms, _ => { });
        trie.WriteState(writer);

        // Deserialise into a fresh trie
        using var trie2 = MakeTrie();
        var bytes = ms.ToArray();
        using var reader = new StoreStreamMemoryRead("test", bytes, 0, () => { });
        trie2.ReadState(reader);

        var after = trie2.SearchIdsUnsorted(Exact("zorbak"), orSearch: false, maxWordsEval: 10000);

        CollectionAssert.AreEquivalent(before.ToList(), after.ToList(),
            "Search results should be identical after serialisation round-trip.");
    }

    [TestMethod]
    public void WriteReadState_UniqueWordCount_Preserved()
    {
        using var trie = MakeTrie();
        // Each unique nodeId gets its own sentence so DocWordCounts doesn't see duplicates
        for (int i = 1; i <= 1000; i++) trie.IndexText(MakeSentence(seed: i * 7), nodeId: i);

        int countBefore = trie.GetUniqueWordCount();

        var ms = new MemoryStream();
        using var writer = new StoreStreamMemoryWrite("test", ms, _ => { });
        trie.WriteState(writer);

        using var trie2 = MakeTrie();
        using var reader = new StoreStreamMemoryRead("test", ms.ToArray(), 0, () => { });
        trie2.ReadState(reader);

        Assert.AreEqual(countBefore, trie2.GetUniqueWordCount());
    }

    // -----------------------------------------------------------------------
    // CompressMemory
    // -----------------------------------------------------------------------

    [TestMethod]
    public void CompressMemory_DoesNotCorruptSearchResults()
    {
        using var trie = MakeTrie();
        for (int i = 1; i <= 1000; i++)
            trie.IndexText(MakeSentence(seed: i * 2), nodeId: i);

        var before = trie.SearchIdsUnsorted(Exact("zorbak"), orSearch: false, maxWordsEval: 10000);
        trie.CompressMemory();
        var after = trie.SearchIdsUnsorted(Exact("zorbak"), orSearch: false, maxWordsEval: 10000);

        CollectionAssert.AreEquivalent(before.ToList(), after.ToList(),
            "CompressMemory should not change search results.");
    }

    // -----------------------------------------------------------------------
    // Edge cases
    // -----------------------------------------------------------------------

    [TestMethod]
    public void IndexEmptyText_NoException()
    {
        using var trie = MakeTrie();
        trie.IndexText("", nodeId: 1);
        trie.IndexText("   ", nodeId: 2);
        Assert.AreEqual(0, trie.GetUniqueWordCount());
    }

    [TestMethod]
    public void Search_EmptyTermSet_ReturnsEmpty()
    {
        using var trie = MakeTrie();
        trie.IndexText("zorbak flimzel", nodeId: 1);
        var ids = trie.SearchIdsUnsorted(TermSet.Empty, orSearch: false, maxWordsEval: 100);
        Assert.AreEqual(0, ids.Count);
    }

    [TestMethod]
    public void WordLongerThanMaxWordLength_TruncatedAndFound()
    {
        // maxWordLength = 6 for this test
        using var trie = new CharArrayTrie(minWordLength: 3, maxWordLength: 6, prefixSearch: true, infixSearch: false);
        trie.IndexText("zorba", nodeId: 1); // 5 chars – within limit

        // A search word of 8 chars should be truncated to 6 for lookup
        var ids = trie.SearchIdsUnsorted(
            new TermSet([new SearchTerm("zorbakxx", prefix: false, infix: false, fuzzy: false)]),
            orSearch: false, maxWordsEval: 100);
        // "zorbakxx" truncated to "zorbak" – "zorba" is not "zorbak", so no match expected
        Assert.AreEqual(0, ids.Count);
    }

    [TestMethod]
    public void IndexSameDocumentTwice_DeIndexOnce_RemainsSearchable()
    {
        // Re-indexing the same node with updated text is a common workflow;
        // the test verifies partial de-index doesn't break things.
        using var trie = MakeTrie();
        trie.IndexText("zorbak flimzel", nodeId: 1);
        trie.IndexText("zorbak grunthop", nodeId: 2);
        trie.DeIndexText("zorbak flimzel", nodeId: 1);

        var ids = trie.SearchIdsUnsorted(Exact("zorbak"), orSearch: false, maxWordsEval: 100);
        Assert.IsFalse(ids.Contains(1));
        Assert.IsTrue(ids.Contains(2));

        ids = trie.SearchIdsUnsorted(Exact("flimzel"), orSearch: false, maxWordsEval: 100);
        Assert.IsFalse(ids.Contains(1));

        ids = trie.SearchIdsUnsorted(Exact("grunthop"), orSearch: false, maxWordsEval: 100);
        Assert.IsTrue(ids.Contains(2));
    }

    [TestMethod]
    public void LargeSyntheticCorpus_IndexDeIndexReindex_Consistent()
    {
        using var trie = MakeTrie();
        int nodeCount = 1000;
        var texts = Enumerable.Range(1, nodeCount).ToDictionary(i => i, i => MakeSentence(seed: i * 17));

        // Index all
        foreach (var kv in texts) trie.IndexText(kv.Value, nodeId: kv.Key);

        // De-index and re-index nodes 1-500 with new text
        var reindexed = Enumerable.Range(1, 500).ToDictionary(i => i, i => MakeSentence(seed: i * 31));
        foreach (var kv in reindexed)
        {
            trie.DeIndexText(texts[kv.Key], nodeId: kv.Key);
            trie.IndexText(kv.Value, nodeId: kv.Key);
        }

        // Spot-check: for each vocab word, returned nodes must actually contain that word
        foreach (var vocabWord in Vocab)
        {
            var ids = trie.SearchIdsUnsorted(Exact(vocabWord), orSearch: false, maxWordsEval: 10000);
            foreach (var id in ids)
            {
                var currentText = id <= 500 ? reindexed[id] : texts[id];
                Assert.IsTrue(currentText.Contains(vocabWord),
                    $"Node {id} returned for '{vocabWord}' but current text does not contain it.");
            }
        }
    }

    [TestMethod]
    public void FuzzySearch_ReturnsNearMatches()
    {
        using var trie = MakeTrie();
        trie.IndexText("zorbak flimzel grunthop snazzle", nodeId: 1);

        // "zorbak" with one char typo
        var ids = trie.SearchIdsUnsorted(Fuzzy("zorbek"), orSearch: false, maxWordsEval: 200);
        // Fuzzy should match "zorbak" (1 edit away)
        Assert.IsTrue(ids.Contains(1), "Fuzzy search for 'zorbek' should find node with 'zorbak'.");
    }
}
