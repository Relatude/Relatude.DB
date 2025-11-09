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
    public static string Clean(string search, int minWordLength, int maxWordLength) {
        if (string.IsNullOrWhiteSpace(search)) return string.Empty;
        var sb = new StringBuilder();
        search = search.ToLower();
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
    static byte[] _legalIndex = new byte[char.MaxValue]; // 0=not determined, 1=include, 2=replace with space, 3=ignore
    static char[] _toLower = new char[char.MaxValue];
    static byte evalLegalIndex(char c) => (byte)(SearchConst.Keep(c) ? 1 : SearchConst.DEVIDERS.Contains(c) ? 2 : 3);
    private static List<char[]> cleanIntoCharWords(string text, int minWordLength, int maxWordLength) {
        var pos = 0;
        var buffer = new char[maxWordLength];
        List<char[]> words = [];
        foreach (char c in text) {
            if (_legalIndex[c] == 0) _legalIndex[c] = evalLegalIndex(c);
            if (_legalIndex[c] == 1) { // include char and build word
                if (pos < maxWordLength) {
                    if (_toLower[c] == 0) _toLower[c] = char.ToLower(c);
                    buffer[pos++] = _toLower[c];
                }
            } else if (_legalIndex[c] == 2) { // add word
                if (pos >= minWordLength) {
                    var word = new char[pos];
                    Array.Copy(buffer, 0, word, 0, pos);
                    if (!StopWords.Contains(word)) words.Add(word);
                }
                pos = 0;
            }
        }
        if (pos >= minWordLength) {
            var word = new char[pos];
            Array.Copy(buffer, 0, word, 0, pos);
            if (!StopWords.Contains(word)) words.Add(word);
        }
        return words;
    }
    public static Dictionary<char[], byte> Clean(string text, int minWordLength, int maxWordLength, out int wordCount) {
        var wc = new Dictionary<char[], byte>(CharArrayComparer.Instance);
        var words = cleanIntoCharWords(text, minWordLength, maxWordLength);
        wordCount = words.Count;
        foreach (var w in words) {
            if (wc.TryGetValue(w, out var existingCount)) {
                if (existingCount < byte.MaxValue) wc[w] = (byte)(existingCount + 1);
            } else {
                wc.Add(w, 1);
            }
        }
        return wc;
    }
    static void cleanIntoStringBuilder(string text, StringBuilder sb, int minWordLength, int maxWordLength) {
        var pos = 0;
        var buffer = new char[maxWordLength];
        foreach (char c in text) {
            if (_legalIndex[c] == 0) _legalIndex[c] = evalLegalIndex(c);
            if (_legalIndex[c] == 1) {
                if (pos < maxWordLength) {
                    if (_toLower[c] == 0) _toLower[c] = char.ToLower(c);
                    buffer[pos++] = _toLower[c];
                }
            } else if (_legalIndex[c] == 2) {
                if (pos >= minWordLength) {
                    var word = new char[pos];
                    Array.Copy(buffer, 0, word, 0, pos);
                    if (!StopWords.Contains(word)) {
                        if (sb.Length > 0) sb.Append(SearchConst.DEVIDER);
                        sb.Append(word);
                    }
                }
                pos = 0;
            }
        }
        if (pos >= minWordLength) {
            var word = new char[pos];
            Array.Copy(buffer, 0, word, 0, pos);
            if (!StopWords.Contains(word)) {
                if (sb.Length > 0) sb.Append(SearchConst.DEVIDER);
                sb.Append(word);
            }
        }

    }
    public static string Clean(string text, int minWordLength, int maxWordLength) {
        var sb = new StringBuilder();
        cleanIntoStringBuilder(text, sb, minWordLength, maxWordLength);
        return sb.ToString();
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
public static class StopWords {
    // Implement more on stop words later (https://github.com/6/stopwords-json/tree/master)
    static string[] _1033 = ["a", "a's", "able", "about", "above", "according", "accordingly", "across", "actually", "after", "afterwards", "again", "against", "ain't", "all", "allow", "allows", "almost", "alone", "along", "already", "also", "although", "always", "am", "among", "amongst", "an", "and", "another", "any", "anybody", "anyhow", "anyone", "anything", "anyway", "anyways", "anywhere", "apart", "appear", "appreciate", "appropriate", "are", "aren't", "around", "as", "aside", "ask", "asking", "associated", "at", "available", "away", "awfully", "b", "be", "became", "because", "become", "becomes", "becoming", "been", "before", "beforehand", "behind", "being", "believe", "below", "beside", "besides", "best", "better", "between", "beyond", "both", "brief", "but", "by", "c", "c'mon", "c's", "came", "can", "can't", "cannot", "cant", "cause", "causes", "certain", "certainly", "changes", "clearly", "co", "com", "come", "comes", "concerning", "consequently", "consider", "considering", "contain", "containing", "contains", "corresponding", "could", "couldn't", "course", "currently", "d", "definitely", "described", "despite", "did", "didn't", "different", "do", "does", "doesn't", "doing", "don't", "done", "down", "downwards", "during", "e", "each", "edu", "eg", "eight", "either", "else", "elsewhere", "enough", "entirely", "especially", "et", "etc", "even", "ever", "every", "everybody", "everyone", "everything", "everywhere", "ex", "exactly", "example", "except", "f", "far", "few", "fifth", "first", "five", "followed", "following", "follows", "for", "former", "formerly", "forth", "four", "from", "further", "furthermore", "g", "get", "gets", "getting", "given", "gives", "go", "goes", "going", "gone", "got", "gotten", "greetings", "h", "had", "hadn't", "happens", "hardly", "has", "hasn't", "have", "haven't", "having", "he", "he's", "hello", "help", "hence", "her", "here", "here's", "hereafter", "hereby", "herein", "hereupon", "hers", "herself", "hi", "him", "himself", "his", "hither", "hopefully", "how", "howbeit", "however", "i", "i'd", "i'll", "i'm", "i've", "ie", "if", "ignored", "immediate", "in", "inasmuch", "inc", "indeed", "indicate", "indicated", "indicates", "inner", "insofar", "instead", "into", "inward", "is", "isn't", "it", "it'd", "it'll", "it's", "its", "itself", "j", "just", "k", "keep", "keeps", "kept", "know", "known", "knows", "l", "last", "lately", "later", "latter", "latterly", "least", "less", "lest", "let", "let's", "like", "liked", "likely", "little", "look", "looking", "looks", "ltd", "m", "mainly", "many", "may", "maybe", "me", "mean", "meanwhile", "merely", "might", "more", "moreover", "most", "mostly", "much", "must", "my", "myself", "n", "name", "namely", "nd", "near", "nearly", "necessary", "need", "needs", "neither", "never", "nevertheless", "new", "next", "nine", "no", "nobody", "non", "none", "noone", "nor", "normally", "not", "nothing", "novel", "now", "nowhere", "o", "obviously", "of", "off", "often", "oh", "ok", "okay", "old", "on", "once", "one", "ones", "only", "onto", "or", "other", "others", "otherwise", "ought", "our", "ours", "ourselves", "out", "outside", "over", "overall", "own", "p", "particular", "particularly", "per", "perhaps", "placed", "please", "plus", "possible", "presumably", "probably", "provides", "q", "que", "quite", "qv", "r", "rather", "rd", "re", "really", "reasonably", "regarding", "regardless", "regards", "relatively", "respectively", "right", "s", "said", "same", "saw", "say", "saying", "says", "second", "secondly", "see", "seeing", "seem", "seemed", "seeming", "seems", "seen", "self", "selves", "sensible", "sent", "serious", "seriously", "seven", "several", "shall", "she", "should", "shouldn't", "since", "six", "so", "some", "somebody", "somehow", "someone", "something", "sometime", "sometimes", "somewhat", "somewhere", "soon", "sorry", "specified", "specify", "specifying", "still", "sub", "such", "sup", "sure", "t", "t's", "take", "taken", "tell", "tends", "th", "than", "thank", "thanks", "thanx", "that", "that's", "thats", "the", "their", "theirs", "them", "themselves", "then", "thence", "there", "there's", "thereafter", "thereby", "therefore", "therein", "theres", "thereupon", "these", "they", "they'd", "they'll", "they're", "they've", "think", "third", "this", "thorough", "thoroughly", "those", "though", "three", "through", "throughout", "thru", "thus", "to", "together", "too", "took", "toward", "towards", "tried", "tries", "truly", "try", "trying", "twice", "two", "u", "un", "under", "unfortunately", "unless", "unlikely", "until", "unto", "up", "upon", "us", "use", "used", "useful", "uses", "using", "usually", "uucp", "v", "value", "various", "very", "via", "viz", "vs", "w", "want", "wants", "was", "wasn't", "way", "we", "we'd", "we'll", "we're", "we've", "welcome", "well", "went", "were", "weren't", "what", "what's", "whatever", "when", "whence", "whenever", "where", "where's", "whereafter", "whereas", "whereby", "wherein", "whereupon", "wherever", "whether", "which", "while", "whither", "who", "who's", "whoever", "whole", "whom", "whose", "why", "will", "willing", "wish", "with", "within", "without", "won't", "wonder", "would", "wouldn't", "x", "y", "yes", "yet", "you", "you'd", "you'll", "you're", "you've", "your", "yours", "yourself", "yourselves", "z", "zero"];
    static string[] _1044 = ["alle", "at", "av", "bare", "begge", "ble", "blei", "bli", "blir", "blitt", "både", "båe", "da", "de", "deg", "dei", "deim", "deira", "deires", "dem", "den", "denne", "der", "dere", "deres", "det", "dette", "di", "din", "disse", "ditt", "du", "dykk", "dykkar", "då", "eg", "ein", "eit", "eitt", "eller", "elles", "en", "enn", "er", "et", "ett", "etter", "for", "fordi", "fra", "før", "ha", "hadde", "han", "hans", "har", "hennar", "henne", "hennes", "her", "hjå", "ho", "hoe", "honom", "hoss", "hossen", "hun", "hva", "hvem", "hver", "hvilke", "hvilken", "hvis", "hvor", "hvordan", "hvorfor", "i", "ikke", "ikkje", "ingen", "ingi", "inkje", "inn", "inni", "ja", "jeg", "kan", "kom", "korleis", "korso", "kun", "kunne", "kva", "kvar", "kvarhelst", "kven", "kvi", "kvifor", "man", "mange", "me", "med", "medan", "meg", "meget", "mellom", "men", "mi", "min", "mine", "mitt", "mot", "mykje", "ned", "no", "noe", "noen", "noka", "noko", "nokon", "nokor", "nokre", "nå", "når", "og", "også", "om", "opp", "oss", "over", "på", "samme", "seg", "selv", "si", "sia", "sidan", "siden", "sin", "sine", "sitt", "sjøl", "skal", "skulle", "slik", "so", "som", "somme", "somt", "så", "sånn", "til", "um", "upp", "ut", "uten", "var", "vart", "varte", "ved", "vere", "verte", "vi", "vil", "ville", "vore", "vors", "vort", "vår", "være", "vært", "å"];
    static HashSet<string> stopWords = new(_1033.Concat(_1044), StringComparer.OrdinalIgnoreCase);
    static HashSet<char[]> stopWordsAsChars = new(stopWords.Select(w => w.ToCharArray()), CharArrayComparer.Instance);
    public static bool Contains(char[] word) => stopWordsAsChars.Contains(word);
    public static bool Contains(string word) => stopWords.Contains(word);
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


