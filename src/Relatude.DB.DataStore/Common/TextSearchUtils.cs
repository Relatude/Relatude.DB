using System.Runtime.InteropServices;
using System.Text;
namespace Relatude.DB.Common;

// chars that are LetterOrDigit are included
// chars that are "DEVIDERS" are replaced with space ( word separator )
// chars that are neither "DEVIDERS"  nor LetterOrDigit are ignored ( melts words together )
// SPACE is word separator
// TO clean a search expression, use SearchUtil...
// TO clean a text for indexing, use IndexUtil...

// Main difference is that SearchUtil allows wildcards and fuzzy characters
// IndexUtil has more optimization for speed as it is called on larger texts

public static class SearchConst {
    public const char WILDCARD = '*';
    public const char FUZZY = '~';
    public const char DEVIDER = ' ';
    public const string DEVIDERS = " \t\n\r,.=/\\?&|+-*()[]{}:";
    public const string KEEP = "'"; // keep apostrophes, as in "O'Connor" or "it's"
    public static bool Keep(char c) => char.IsLetterOrDigit(c) || SearchConst.KEEP.Contains(c);
}
public static class SearchUtil {
    /// <summary>
    /// The search text with its operators removed. A wildcard or a fuzzy marker is an instruction to
    /// the word index; an embedding model has no idea what they mean and only sees noise in the
    /// middle of the query, so the semantic half of a search is given the plain words instead.
    /// </summary>
    public static string StripOperators(string search) {
        if (string.IsNullOrEmpty(search)) return search;
        if (!search.Contains(SearchConst.WILDCARD) && !search.Contains(SearchConst.FUZZY)) return search; // by far the common case
        var sb = new StringBuilder(search.Length);
        foreach (var c in search) if (c != SearchConst.WILDCARD && c != SearchConst.FUZZY) sb.Append(c);
        return sb.ToString();
    }
    public static string Clean(string search, int minWordLength, int maxWordLength) {
        if (string.IsNullOrWhiteSpace(search)) return string.Empty;
        var sb = new StringBuilder();
        search = search.ToLowerInvariant(); // must match the invariant lowercasing used when indexing
        foreach (var c in search) {
            if (SearchConst.Keep(c) || c == SearchConst.WILDCARD || c == SearchConst.FUZZY) sb.Append(c);
            else if (SearchConst.DEVIDERS.Contains(c)) sb.Append(SearchConst.DEVIDER);
        }
        var terms = sb.ToString().Split(SearchConst.DEVIDER, StringSplitOptions.RemoveEmptyEntries);
        sb = new StringBuilder();
        foreach (var term in terms) {
            var termWithOutSpecialChars = term
                .Replace(SearchConst.WILDCARD.ToString(), string.Empty)
                .Replace(SearchConst.FUZZY.ToString(), string.Empty);
            if (termWithOutSpecialChars.Length < minWordLength) continue;
            if (StopWords.Contains(term)) continue;
            if (sb.Length > 0) sb.Append(SearchConst.DEVIDER);
            if (term.Length > maxWordLength) sb.Append(term[..maxWordLength]);
            else sb.Append(term);
        }
        return sb.ToString();
    }
}
public static class IndexUtil {
    // A bit messy, but optimized for speed as this is called on large texts and all docs.
    // A single table lookup per character does both the classification and the lowercasing:
    //   0 = not determined yet, 1 = replace with space ( word separator ), 2 = ignore
    //   anything else = the lowercased character to include
    // A character that is kept is a letter, digit or apostrophe, so its lowercase is never 0, 1 or 2.
    const char UNSET = '\0', DIVIDER = '\u0001', IGNORE = '\u0002';
    // invariant, the cache is process wide and must not depend on the current thread culture
    static char classify(char c) => SearchConst.Keep(c) ? char.ToLowerInvariant(c) : SearchConst.DEVIDERS.Contains(c) ? DIVIDER : IGNORE;
    static readonly char[] _ascii = buildAscii(); // 256 bytes, stays in L1 and covers almost all text
    static readonly char[] _map = new char[char.MaxValue + 1]; // filled lazily, U+FFFF is a legal char in a string
    static char[] buildAscii() {
        var table = new char[128];
        for (var i = 0; i < table.Length; i++) table[i] = classify((char)i);
        return table;
    }
    static char map(char c) {
        if (c < 128) return _ascii[c];
        var m = _map[c];
        return m == UNSET ? _map[c] = classify(c) : m;
    }
    public static Dictionary<char[], byte> Clean(string text, int minWordLength, int maxWordLength, out int wordCount) {
        // rough guess at the number of distinct words, saves the dictionary a handful of rehashing growths
        var wc = new Dictionary<char[], byte>(Math.Clamp(text.Length / 10, 8, 2048), CharArrayComparer.Instance);
        var count = 0;
        var buffer = new char[maxWordLength];
        // Reusable lookup keys, one per word length. Each word is copied into the key array of its
        // own length and looked up with it. Only when the word turns out to be new does the
        // dictionary take ownership of the array and the slot get a fresh one, so repeated words
        // and stop words allocate nothing at all.
        var pool = new char[maxWordLength + 1][];
        var pos = 0;
        foreach (var c in text.AsSpan()) {
            var m = map(c);
            if (m > IGNORE) { // by far the most common case: a character of the word itself
                if (pos < maxWordLength) buffer[pos++] = m;
            } else if (m == DIVIDER) { // add word
                if (pos >= minWordLength) add(buffer, pos, pool, wc, ref count);
                pos = 0;
            }
        }
        if (pos >= minWordLength) add(buffer, pos, pool, wc, ref count);
        wordCount = count;
        return wc;
        static void add(char[] buffer, int length, char[][] pool, Dictionary<char[], byte> wc, ref int count) {
            var word = pool[length] ??= new char[length];
            buffer.AsSpan(0, length).CopyTo(word);
            if (StopWords.Contains(word)) return;
            count++;
            ref var occurrences = ref CollectionsMarshal.GetValueRefOrAddDefault(wc, word, out var existed);
            if (existed) {
                if (occurrences < byte.MaxValue) occurrences++;
            } else {
                occurrences = 1;
                pool[length] = null!; // the dictionary owns this array now
            }
        }
    }
    // Same as Clean above, but the words come back as strings. Faster than cleaning into char[]
    // and converting afterwards: the framework string dictionary has a hand tuned ordinal comparer,
    // where a Dictionary<char[],..> pays an interface call for every hash and every comparison.
    public static Dictionary<string, byte> CleanToStrings(string text, int minWordLength, int maxWordLength, out int wordCount) {
        var wc = new Dictionary<string, byte>(Math.Clamp(text.Length / 10, 8, 2048)); // default comparer on purpose, it is the fast one
        var count = 0;
        var buffer = new char[maxWordLength];
        var pos = 0;
        foreach (var c in text.AsSpan()) {
            var m = map(c);
            if (m > IGNORE) {
                if (pos < maxWordLength) buffer[pos++] = m;
            } else if (m == DIVIDER) {
                if (pos >= minWordLength) add(buffer, pos, wc, ref count);
                pos = 0;
            }
        }
        if (pos >= minWordLength) add(buffer, pos, wc, ref count);
        wordCount = count;
        return wc;
        static void add(char[] buffer, int length, Dictionary<string, byte> wc, ref int count) {
            var word = buffer.AsSpan(0, length);
            if (StopWords.Contains(word)) return; // rejected straight out of the buffer, no string is ever made for it
            count++;
            // A repeated word does allocate a string that is thrown away again. Avoiding that with a
            // memo of already seen words was measured and came out slower: the allocation is cheaper
            // than the extra probe needed to find the existing instance.
            ref var occurrences = ref CollectionsMarshal.GetValueRefOrAddDefault(wc, new string(word), out var existed);
            if (existed) {
                if (occurrences < byte.MaxValue) occurrences++;
            } else {
                occurrences = 1;
            }
        }
    }
    public static string Clean(string text, int minWordLength, int maxWordLength) {
        var sb = new StringBuilder(text.Length);
        var buffer = new char[maxWordLength];
        var pool = new char[maxWordLength + 1][]; // see Clean above, here the keys are only needed for the stop word lookup
        var pos = 0;
        foreach (var c in text.AsSpan()) {
            var m = map(c);
            if (m > IGNORE) {
                if (pos < maxWordLength) buffer[pos++] = m;
            } else if (m == DIVIDER) {
                if (pos >= minWordLength) append(buffer, pos, pool, sb);
                pos = 0;
            }
        }
        if (pos >= minWordLength) append(buffer, pos, pool, sb);
        return sb.ToString();
        static void append(char[] buffer, int length, char[][] pool, StringBuilder sb) {
            var word = pool[length] ??= new char[length];
            buffer.AsSpan(0, length).CopyTo(word);
            if (StopWords.Contains(word)) return;
            if (sb.Length > 0) sb.Append(SearchConst.DEVIDER);
            sb.Append(buffer, 0, length);
        }
    }
}
class CharArrayComparer : IEqualityComparer<char[]> {
    public static IEqualityComparer<char[]> Instance = new CharArrayComparer();
    public bool Equals(char[]? x, char[]? y) {
        if (x == null) throw new ArgumentNullException("x");
        if (y == null) throw new ArgumentNullException("y");
        if (x.Length != y.Length) return false;
        for (var i = 0; i < x.Length; i++) if (x[i] != y[i]) return false;
        return true;
    }
    public int GetHashCode(char[] array) {
        if (array.Length == 0) return 17; // guard, indexing into an empty array would throw
        unchecked {
            int hash = 17;
            hash = 31 * hash + array[0].GetHashCode();
            hash = 31 * hash + array[array.Length / 2].GetHashCode();
            hash = 31 * hash + array[array.Length - 1].GetHashCode();
            hash = 31 * hash + array.Length;
            return hash;
        }
    }
}
// A set of strings that can be probed with a span, so a candidate word never has to be turned into
// a string just to find out that it is a stop word. Open addressing with linear probing, and a hash
// over the first character, the last character and the length: the set is small and known up front,
// so the cost of the hash matters more than its quality.
class SpanStringSet {
    readonly string[] _entries;
    readonly int _mask;
    public SpanStringSet(IEnumerable<string> words) {
        var all = words.Distinct().ToArray();
        var size = 4;
        while (size < all.Length * 4) size <<= 1; // load factor well below half, probes stay very short
        _entries = new string[size];
        _mask = size - 1;
        foreach (var w in all) {
            var i = hash(w.AsSpan()) & _mask;
            while (_entries[i] != null) i = (i + 1) & _mask;
            _entries[i] = w;
        }
    }
    static int hash(ReadOnlySpan<char> word) => (word[0] * 8191) ^ (word[^1] * 131) ^ (word.Length * 17);
    public bool Contains(ReadOnlySpan<char> word) {
        if (word.Length == 0) return false; // no empty stop word, and the hash would index out of range
        var i = hash(word) & _mask;
        while (true) {
            var e = _entries[i];
            if (e == null) return false;
            if (word.SequenceEqual(e)) return true;
            i = (i + 1) & _mask;
        }
    }
}
public static class StopWords {
    // Implement more on stop words later (https://github.com/6/stopwords-json/tree/master)
    static string[] _1033 = ["a", "a's", "able", "about", "above", "according", "accordingly", "across", "actually", "after", "afterwards", "again", "against", "ain't", "all", "allow", "allows", "almost", "alone", "along", "already", "also", "although", "always", "am", "among", "amongst", "an", "and", "another", "any", "anybody", "anyhow", "anyone", "anything", "anyway", "anyways", "anywhere", "apart", "appear", "appreciate", "appropriate", "are", "aren't", "around", "as", "aside", "ask", "asking", "associated", "at", "available", "away", "awfully", "b", "be", "became", "because", "become", "becomes", "becoming", "been", "before", "beforehand", "behind", "being", "believe", "below", "beside", "besides", "best", "better", "between", "beyond", "both", "brief", "but", "by", "c", "c'mon", "c's", "came", "can", "can't", "cannot", "cant", "cause", "causes", "certain", "certainly", "changes", "clearly", "co", "com", "come", "comes", "concerning", "consequently", "consider", "considering", "contain", "containing", "contains", "corresponding", "could", "couldn't", "course", "currently", "d", "definitely", "described", "despite", "did", "didn't", "different", "do", "does", "doesn't", "doing", "don't", "done", "down", "downwards", "during", "e", "each", "edu", "eg", "eight", "either", "else", "elsewhere", "enough", "entirely", "especially", "et", "etc", "even", "ever", "every", "everybody", "everyone", "everything", "everywhere", "ex", "exactly", "example", "except", "f", "far", "few", "fifth", "first", "five", "followed", "following", "follows", "for", "former", "formerly", "forth", "four", "from", "further", "furthermore", "g", "get", "gets", "getting", "given", "gives", "go", "goes", "going", "gone", "got", "gotten", "greetings", "h", "had", "hadn't", "happens", "hardly", "has", "hasn't", "have", "haven't", "having", "he", "he's", "hello", "help", "hence", "her", "here", "here's", "hereafter", "hereby", "herein", "hereupon", "hers", "herself", "hi", "him", "himself", "his", "hither", "hopefully", "how", "howbeit", "however", "i", "i'd", "i'll", "i'm", "i've", "ie", "if", "ignored", "immediate", "in", "inasmuch", "inc", "indeed", "indicate", "indicated", "indicates", "inner", "insofar", "instead", "into", "inward", "is", "isn't", "it", "it'd", "it'll", "it's", "its", "itself", "j", "just", "k", "keep", "keeps", "kept", "know", "known", "knows", "l", "last", "lately", "later", "latter", "latterly", "least", "less", "lest", "let", "let's", "like", "liked", "likely", "little", "look", "looking", "looks", "ltd", "m", "mainly", "many", "may", "maybe", "me", "mean", "meanwhile", "merely", "might", "more", "moreover", "most", "mostly", "much", "must", "my", "myself", "n", "name", "namely", "nd", "near", "nearly", "necessary", "need", "needs", "neither", "never", "nevertheless", "new", "next", "nine", "no", "nobody", "non", "none", "noone", "nor", "normally", "not", "nothing", "novel", "now", "nowhere", "o", "obviously", "of", "off", "often", "oh", "ok", "okay", "old", "on", "once", "one", "ones", "only", "onto", "or", "other", "others", "otherwise", "ought", "our", "ours", "ourselves", "out", "outside", "over", "overall", "own", "p", "particular", "particularly", "per", "perhaps", "placed", "please", "plus", "possible", "presumably", "probably", "provides", "q", "que", "quite", "qv", "r", "rather", "rd", "re", "really", "reasonably", "regarding", "regardless", "regards", "relatively", "respectively", "right", "s", "said", "same", "saw", "say", "saying", "says", "second", "secondly", "see", "seeing", "seem", "seemed", "seeming", "seems", "seen", "self", "selves", "sensible", "sent", "serious", "seriously", "seven", "several", "shall", "she", "should", "shouldn't", "since", "six", "so", "some", "somebody", "somehow", "someone", "something", "sometime", "sometimes", "somewhat", "somewhere", "soon", "sorry", "specified", "specify", "specifying", "still", "sub", "such", "sup", "sure", "t", "t's", "take", "taken", "tell", "tends", "th", "than", "thank", "thanks", "thanx", "that", "that's", "thats", "the", "their", "theirs", "them", "themselves", "then", "thence", "there", "there's", "thereafter", "thereby", "therefore", "therein", "theres", "thereupon", "these", "they", "they'd", "they'll", "they're", "they've", "think", "third", "this", "thorough", "thoroughly", "those", "though", "three", "through", "throughout", "thru", "thus", "to", "together", "too", "took", "toward", "towards", "tried", "tries", "truly", "try", "trying", "twice", "two", "u", "un", "under", "unfortunately", "unless", "unlikely", "until", "unto", "up", "upon", "us", "use", "used", "useful", "uses", "using", "usually", "uucp", "v", "value", "various", "very", "via", "viz", "vs", "w", "want", "wants", "was", "wasn't", "way", "we", "we'd", "we'll", "we're", "we've", "welcome", "well", "went", "were", "weren't", "what", "what's", "whatever", "when", "whence", "whenever", "where", "where's", "whereafter", "whereas", "whereby", "wherein", "whereupon", "wherever", "whether", "which", "while", "whither", "who", "who's", "whoever", "whole", "whom", "whose", "why", "will", "willing", "wish", "with", "within", "without", "won't", "wonder", "would", "wouldn't", "x", "y", "yes", "yet", "you", "you'd", "you'll", "you're", "you've", "your", "yours", "yourself", "yourselves", "z", "zero"];
    static string[] _1044 = ["alle", "at", "av", "bare", "begge", "ble", "blei", "bli", "blir", "blitt", "både", "båe", "da", "de", "deg", "dei", "deim", "deira", "deires", "dem", "den", "denne", "der", "dere", "deres", "det", "dette", "di", "din", "disse", "ditt", "du", "dykk", "dykkar", "då", "eg", "ein", "eit", "eitt", "eller", "elles", "en", "enn", "er", "et", "ett", "etter", "for", "fordi", "fra", "før", "ha", "hadde", "han", "hans", "har", "hennar", "henne", "hennes", "her", "hjå", "ho", "hoe", "honom", "hoss", "hossen", "hun", "hva", "hvem", "hver", "hvilke", "hvilken", "hvis", "hvor", "hvordan", "hvorfor", "i", "ikke", "ikkje", "ingen", "ingi", "inkje", "inn", "inni", "ja", "jeg", "kan", "kom", "korleis", "korso", "kun", "kunne", "kva", "kvar", "kvarhelst", "kven", "kvi", "kvifor", "man", "mange", "me", "med", "medan", "meg", "meget", "mellom", "men", "mi", "min", "mine", "mitt", "mot", "mykje", "ned", "no", "noe", "noen", "noka", "noko", "nokon", "nokor", "nokre", "nå", "når", "og", "også", "om", "opp", "oss", "over", "på", "samme", "seg", "selv", "si", "sia", "sidan", "siden", "sin", "sine", "sitt", "sjøl", "skal", "skulle", "slik", "so", "som", "somme", "somt", "så", "sånn", "til", "um", "upp", "ut", "uten", "var", "vart", "varte", "ved", "vere", "verte", "vi", "vil", "ville", "vore", "vors", "vort", "vår", "være", "vært", "å"];
    static HashSet<string> stopWords = new(_1033.Concat(_1044), StringComparer.OrdinalIgnoreCase);
    static HashSet<char[]> stopWordsAsChars = new(stopWords.Select(w => w.ToCharArray()), CharArrayComparer.Instance);
    static SpanStringSet stopWordsAsSpans = new(stopWords);
    public static bool Contains(char[] word) => stopWordsAsChars.Contains(word);
    public static bool Contains(string word) => stopWords.Contains(word);
    // ordinal, unlike the string overload above: the caller has already lowercased the word
    public static bool Contains(ReadOnlySpan<char> word) => stopWordsAsSpans.Contains(word);
}
public class TermSet(SearchTerm[] terms) {
    public static TermSet Empty { get; } = new([]);
    public SearchTerm[] Terms { get; } = terms;
    public override string ToString() {
        if (Terms == null || Terms.Length == 0) return string.Empty;
        var sb = new StringBuilder();
        foreach (var search in Terms) {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(search.ToString());
        }
        return sb.ToString();
    }
    public static TermSet Parse(string text, int minWordLength, int maxWordLength, bool allowInfix) {
        var cleaned = SearchUtil.Clean(text, minWordLength, maxWordLength); // leaves * ?
        if (string.IsNullOrEmpty(cleaned)) return Empty;
        var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var searches = new List<SearchTerm>();
        foreach (var word in words) {
            var fuzzy = word.Contains(SearchConst.FUZZY);
            var strippedWord = fuzzy ? word.Replace(SearchConst.FUZZY.ToString(), string.Empty) : word;
            var prefix = strippedWord.EndsWith(SearchConst.WILDCARD);
            var infix = strippedWord.StartsWith(SearchConst.WILDCARD);
            strippedWord = strippedWord.Replace(SearchConst.WILDCARD.ToString(), string.Empty);
            if (strippedWord.Length > 0) searches.Add(new(strippedWord, prefix, allowInfix ? infix : false, fuzzy));
        }
        return new TermSet([.. searches]);
    }
}
public class SearchTerm(string word, bool prefix, bool infix, bool fuzzy) {
    public string Word { get; } = word;
    public bool Prefix { get; } = prefix;
    public bool Infix { get; } = infix;
    public bool Fuzzy { get; } = fuzzy;
    public override string ToString() {
        var result = Word;
        if (Infix) result = SearchConst.WILDCARD + result;
        if (Fuzzy) result += SearchConst.FUZZY;
        if (Prefix) result += SearchConst.WILDCARD;
        return result;
    }
}


