using System.Text;
using Relatude.DB.Common;

namespace TextIndexBenchmarks.Harness;

/// <summary>
/// The synthetic document collection and query workload every engine is measured on. Built once
/// from a fixed seed, so all four implementations index the same texts and answer the same queries
/// in the same order.
///
/// <para>Words are drawn from a vocabulary with a skewed (zipf-like) frequency distribution: the
/// most common words land in roughly a tenth of the documents and the tail is nearly unique, which
/// is what makes some searches cheap and others expensive. Query terms are drawn from the same
/// distribution, so the query mix is dominated by common words — the costly case — exactly as a
/// real search workload is.</para>
///
/// <para>The vocabulary is nonsense syllables rather than English: it keeps every engine's
/// tokenizer and stop-word list out of the comparison (the texts are pre-cleaned with
/// <see cref="IndexUtil"/> by the indexes themselves anyway), and it makes the frequency
/// distribution a property of the generator rather than of some sample text.</para>
/// </summary>
public sealed class Corpus {
    public const int MinWordLength = 2;
    public const int MaxWordLength = 64;

    /// <summary>Node id of document <paramref name="docIndex"/>. Ids start at 1: the trie's hit
    /// lists use node id 0 as their "nothing pending" sentinel.</summary>
    public static int NodeId(int docIndex) => docIndex + 1;

    public required string[] Vocabulary { get; init; }
    /// <summary>The document texts, indexed by document number.</summary>
    public required string[] Documents { get; init; }
    /// <summary>Replacement texts for the update phase, same document numbers.</summary>
    public required string[] UpdatedDocuments { get; init; }
    /// <summary>The small delta indexed before the durability checkpoint is timed; node ids
    /// continue above the corpus.</summary>
    public required string[] PersistDeltaDocuments { get; init; }
    /// <summary>Extra documents the mixed phase inserts; node ids continue above the delta.</summary>
    public required string[] MixedDocuments { get; init; }

    public required TermSet[] TermQueries { get; init; }
    public required TermSet[] AndQueries { get; init; }
    public required TermSet[] OrQueries { get; init; }
    public required TermSet[] PrefixQueries { get; init; }
    public required TermSet[] InfixQueries { get; init; }
    public required TermSet[] FuzzyQueries { get; init; }
    public required string[] SuggestWords { get; init; }

    public int DocumentCount => Documents.Length;
    public int WordsPerDocument { get; private init; }
    public long TotalWords => (long)DocumentCount * WordsPerDocument;

    const int queryCount = 500;

    public static Corpus Build(int documentCount, int wordsPerDocument, int vocabularySize) {
        var rnd = new Random(20260809);
        var vocabulary = buildVocabulary(rnd, vocabularySize);

        var documents = new string[documentCount];
        var updated = new string[Math.Min(documentCount, 20_000)];
        var persistDelta = new string[Math.Clamp(documentCount / 100, 100, 1_000)];
        var mixed = new string[Math.Clamp(documentCount / 4, 1_000, 25_000)];
        var sb = new StringBuilder(wordsPerDocument * 12);
        for (var i = 0; i < documents.Length; i++) documents[i] = nextDocument(rnd, vocabulary, wordsPerDocument, sb);
        for (var i = 0; i < updated.Length; i++) updated[i] = nextDocument(rnd, vocabulary, wordsPerDocument, sb);
        for (var i = 0; i < persistDelta.Length; i++) persistDelta[i] = nextDocument(rnd, vocabulary, wordsPerDocument, sb);
        for (var i = 0; i < mixed.Length; i++) mixed[i] = nextDocument(rnd, vocabulary, wordsPerDocument, sb);

        // Query terms come from the same distribution as the corpus, so most queries hit common
        // words. Each query class gets its own draws but a shared seed sequence, so the engines
        // all see the identical stream.
        var term = new TermSet[queryCount];
        var and = new TermSet[queryCount];
        var or = new TermSet[queryCount];
        var prefix = new TermSet[queryCount];
        var infix = new TermSet[queryCount];
        var fuzzy = new TermSet[queryCount];
        var suggest = new string[queryCount];
        for (var i = 0; i < queryCount; i++) {
            var w = nextWord(rnd, vocabulary);
            term[i] = parse(w);
            and[i] = parse(nextWord(rnd, vocabulary) + " " + nextWord(rnd, vocabulary));
            or[i] = parse(nextWord(rnd, vocabulary) + " " + nextWord(rnd, vocabulary) + " " + nextWord(rnd, vocabulary));
            // 4 leading chars of a common word: matches a whole family of words, which is what a
            // type-ahead prefix search looks like
            prefix[i] = parse(w[..Math.Min(4, w.Length)] + "*");
            // 4 chars from the middle, so the match cannot also be found by a prefix scan
            var mid = w.Length > 6 ? w.Substring(2, 4) : w;
            infix[i] = parse("*" + mid);
            var typo = mutate(rnd, nextWord(rnd, vocabulary));
            fuzzy[i] = parse(typo + "~");
            suggest[i] = typo;
        }
        return new Corpus {
            Vocabulary = vocabulary,
            Documents = documents,
            UpdatedDocuments = updated,
            PersistDeltaDocuments = persistDelta,
            MixedDocuments = mixed,
            TermQueries = term,
            AndQueries = and,
            OrQueries = or,
            PrefixQueries = prefix,
            InfixQueries = infix,
            FuzzyQueries = fuzzy,
            SuggestWords = suggest,
            WordsPerDocument = wordsPerDocument,
        };
    }

    static TermSet parse(string query) => TermSet.Parse(query, MinWordLength, MaxWordLength, allowInfix: true);

    static string nextDocument(Random rnd, string[] vocabulary, int words, StringBuilder sb) {
        sb.Clear();
        for (var w = 0; w < words; w++) {
            if (w > 0) sb.Append(' ');
            sb.Append(vocabulary[zipf(rnd, vocabulary.Length)]);
        }
        return sb.ToString();
    }
    static string nextWord(Random rnd, string[] vocabulary) => vocabulary[zipf(rnd, vocabulary.Length)];

    /// <summary>
    /// Rank of the next word: <c>n·u³</c>, a cheap skew that gives the head of the vocabulary most
    /// of the draws without the extreme concentration of a true 1/r zipf (where the single most
    /// common word would end up in nearly every document, making "common term" searches degenerate
    /// into full scans for every engine alike).
    /// </summary>
    static int zipf(Random rnd, int n) {
        var u = rnd.NextDouble();
        return (int)(n * u * u * u);
    }

    /// <summary>A one-character substitution: a plausible typo, one edit away from a real word.</summary>
    static string mutate(Random rnd, string word) {
        var chars = word.ToCharArray();
        var at = rnd.Next(chars.Length);
        var c = (char)('a' + rnd.Next(26));
        chars[at] = chars[at] == c ? (char)('a' + (c - 'a' + 1) % 26) : c;
        return new string(chars);
    }

    static string[] buildVocabulary(Random rnd, int count) {
        // 2-6 syllables of two letters: 4-12 character words, all above the minimum word length
        string[] syllables = ["ka", "ro", "mi", "ta", "lu", "ve", "so", "ni", "pa", "de", "gu", "fi", "ze", "bo", "ry", "ha", "ju", "ne", "qi", "wo"];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(count);
        var sb = new StringBuilder(12);
        while (result.Count < count) {
            sb.Clear();
            var length = 2 + rnd.Next(5);
            for (var i = 0; i < length; i++) sb.Append(syllables[rnd.Next(syllables.Length)]);
            var word = sb.ToString();
            // a word the tokenizer would drop is not a word this benchmark can search for
            if (StopWords.Contains(word)) continue;
            if (seen.Add(word)) result.Add(word);
        }
        return [.. result];
    }
}
