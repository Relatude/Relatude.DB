using Relatude.DB.Common;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.Query.Data;

namespace Relatude.Utils;

/// <summary>
/// The yardstick every word-counting index is held to: the same corpus, and the counts worked out
/// straight from its text with the tokenizer the indexes themselves index with. An index reading
/// its own terms backwards has to land on exactly this, whichever engine it is - that is the whole
/// claim being made, and it is what lets the memory index and the native one be compared to each
/// other by comparing both to this.
/// </summary>
public static class WordCountOracle {
    public const int MinWordLength = 3;
    public const int MaxWordLength = 20;

    /// <summary>
    /// Made-up words, plus two real stop words ("the", "and") that an index must drop on its own
    /// and "xy", too short to be indexed. Several share prefixes, so a walk over a trie has to get
    /// its buffer right where the trie compresses a tail onto one node.
    /// </summary>
    public static readonly string[] Vocabulary = [
        "zorbak", "zorbal", "zorb", "flimzel", "flimzelian", "grunthop", "snazzle", "blivort",
        "crumfex", "drazznik", "elgooth", "frendal", "glorpix", "hunkavar", "ibloon", "jostrek",
        "klumfar", "lentrop", "moogish", "narplex", "ortwist", "plorfex", "quibzar", "renstop",
        "the", "and", "xy",
    ];

    /// <summary>A corpus of made-up documents, the same one every time for a given seed.</summary>
    public static Dictionary<int, string> MakeCorpus(int documents, int seed = 1) {
        var rnd = new Random(seed);
        var corpus = new Dictionary<int, string>();
        for (var id = 1; id <= documents; id++) {
            var words = new List<string>();
            var length = rnd.Next(5, 60);
            for (var i = 0; i < length; i++) words.Add(Vocabulary[rnd.Next(Vocabulary.Length)]);
            corpus[id] = string.Join(' ', words);
        }
        return corpus;
    }

    public static IdSet Subset(IEnumerable<int> ids) => IdSet.UncachableSet(new HashSet<int>(ids));

    /// <summary>Word to (documents, occurrences), straight from the text.</summary>
    public static Dictionary<string, (int Documents, long Occurrences)> CountFromText(IEnumerable<string> texts) {
        var counts = new Dictionary<string, (int Documents, long Occurrences)>();
        foreach (var text in texts) {
            foreach (var word in IndexUtil.CleanToStrings(text, MinWordLength, MaxWordLength, out _)) {
                counts.TryGetValue(word.Key, out var soFar);
                counts[word.Key] = (soFar.Documents + 1, soFar.Occurrences + word.Value);
            }
        }
        return counts;
    }

    /// <summary>What an index must hand back for these documents of this corpus.</summary>
    public static WordCount[] Expected(Dictionary<int, string> corpus, IEnumerable<int> subset, WordCountOptions options) {
        var ids = subset.ToHashSet();
        var inSubset = CountFromText(corpus.Where(d => ids.Contains(d.Key)).Select(d => d.Value));
        var inIndex = CountFromText(corpus.Values);
        var minLength = Math.Max(MinWordLength, options.MinWordLength);
        var words = inSubset
            .Where(w => w.Key.Length >= minLength)
            .Where(w => options.Ignore == null || !options.Ignore.Contains(w.Key))
            .Where(w => w.Value.Documents >= Math.Max(1, options.MinDocuments))
            .Select(w => new WordCount(w.Key, w.Value.Documents, w.Value.Occurrences, inIndex[w.Key].Documents))
            .ToArray();
        Array.Sort(words, WordCount.Descending);
        return words.Take(Math.Max(1, options.MaxWords)).ToArray();
    }

    /// <summary>How many distinct words the subset holds, which is what DistinctWords reports.</summary>
    public static int DistinctIn(Dictionary<int, string> corpus, IEnumerable<int> subset) {
        var ids = subset.ToHashSet();
        return CountFromText(corpus.Where(d => ids.Contains(d.Key)).Select(d => d.Value)).Count;
    }

    public static void AssertSame(WordCount[] expected, WordCount[] actual, string what = "") {
        Assert.AreEqual(expected.Length, actual.Length, what + " number of words: expected ["
            + string.Join(", ", expected.Select(w => w.Word)) + "] but got [" + string.Join(", ", actual.Select(w => w.Word)) + "]");
        for (var i = 0; i < expected.Length; i++) {
            Assert.AreEqual(expected[i].Word, actual[i].Word, what + " word at " + i);
            Assert.AreEqual(expected[i].Documents, actual[i].Documents, what + " documents of " + expected[i].Word);
            Assert.AreEqual(expected[i].Occurrences, actual[i].Occurrences, what + " occurrences of " + expected[i].Word);
            Assert.AreEqual(expected[i].DocumentsInIndex, actual[i].DocumentsInIndex, what + " documents in index of " + expected[i].Word);
        }
    }
}
