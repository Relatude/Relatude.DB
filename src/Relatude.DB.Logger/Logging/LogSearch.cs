using System.Globalization;
using System.Text;

namespace Relatude.DB.Logging;

/// <summary>
/// A text search over recorded log entries.
///
/// There is no index behind this: a search reads the records of the range it is given and tests
/// each one, which is what makes it able to answer anything without anything having been prepared
/// for it. A log is a few files per day, so the cost is the cost of reading them.
///
/// What a search looks like:
/// <code>
///   timeout                  an entry with "timeout" anywhere in it
///   time*out                 * stands for any run of characters, ? for exactly one
///   "could not open"         a phrase, because a space would otherwise be two terms
///   error -shutdown          both hold: "error" somewhere, "shutdown" nowhere
///   duration:*ms message:get a term can name the column it searches
/// </code>
/// Several terms all have to hold. A term is looked for anywhere in a value, wildcard or not:
/// <c>get*nodes</c> finds "GetNodes" in the middle of a longer value, and a leading or trailing
/// star is simply redundant - adding one to a term never takes away entries it was finding. There
/// is no escape: <c>*</c> and <c>?</c> are always wildcards.
///
/// A column is named by its key or by the name it shows under, and <c>time</c> names the timestamp
/// every entry has. A name that is no column of this log is not an error - the term is searched as
/// the plain text it looks like, which is what makes <c>10:30</c> find a time rather than nothing.
/// </summary>
public sealed class LogSearch {
    /// <summary>A search that has nothing to look for, and so leaves every entry in.</summary>
    public static readonly LogSearch Empty = new(string.Empty, [], false);

    // the names of the timestamp, which is the one column no log declares
    static readonly string[] _timeFields = ["time", "timestamp"];
    // how a timestamp reads when it is searched, the same form the text log writes
    internal const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

    readonly Term[] _terms;
    LogSearch(string query, Term[] terms, bool caseSensitive) {
        Query = query;
        _terms = terms;
        CaseSensitive = caseSensitive;
    }

    /// <summary>The text this search was made from, as it was written.</summary>
    public string Query { get; }
    /// <summary>Whether upper and lower case are told apart. They are not, unless asked for.</summary>
    public bool CaseSensitive { get; }
    /// <summary>True when there is nothing to look for, and every entry matches.</summary>
    public bool IsEmpty => _terms.Length == 0;

    /// <summary>
    /// Reads a search. Anything parses: a query is words, and a word that looks like nothing in
    /// particular is searched as itself. Whitespace only gives <see cref="Empty"/>.
    /// </summary>
    public static LogSearch Parse(string? query, bool caseSensitive = false) {
        if (string.IsNullOrWhiteSpace(query)) return Empty;
        var terms = new List<Term>();
        foreach (var token in tokenize(query)) {
            var text = token.Text;
            if (text.Length == 0) continue;
            // A column name is only taken as one if the log turns out to have that column, so a
            // term that merely contains a colon is still searched as the text it is. The colon
            // that splits the two is the first one outside the quotes: everything within them is
            // text searched for, whichever side of the colon it is on.
            string? field = null;
            var value = text;
            var colon = token.ColonAt;
            if (colon > 0 && colon < text.Length - 1) {
                field = text[..colon];
                value = text[(colon + 1)..];
            }
            terms.Add(new Term(text, field, value, token.Negated));
        }
        if (terms.Count == 0) return Empty;
        return new LogSearch(query, [.. terms], caseSensitive);
    }

    /// <summary>
    /// Whether one entry is one of the search's. The log settings name the columns, which is what
    /// a term naming one is resolved against; without them every term searches every value.
    /// </summary>
    public bool Matches(LogEntry entry, LogSettings? settings = null) {
        foreach (var term in _terms) {
            if (matches(term, entry, settings) == term.Negated) return false;
        }
        return true;
    }

    bool matches(Term term, LogEntry entry, LogSettings? settings) {
        // A term naming a column searches that column alone - but only once the column is known to
        // exist, since "10:30" names no column and is a search for half past ten. A log whose
        // settings changed can hold entries carrying values it no longer declares, so the entry is
        // asked as well as the settings.
        if (term.Field != null) {
            var column = settings == null ? null : namedColumn(settings, term.Field);
            foreach (var value in entry.Values) {
                if (string.Equals(value.Key, column ?? term.Field, StringComparison.OrdinalIgnoreCase)) {
                    return matchesText(textOf(value.Value), term.Value);
                }
            }
            // a column of this log that this entry happens not to carry: nothing there to match
            if (column != null) return false;
            if (isTimeField(term.Field)) return matchesText(timeText(entry.Timestamp), term.Value);
            // ...and otherwise the term names nothing here, so it is searched as the text it is
        }
        // everything the entry holds, the time it was recorded at included
        if (matchesText(timeText(entry.Timestamp), term.Text)) return true;
        foreach (var value in entry.Values.Values) {
            if (matchesText(textOf(value), term.Text)) return true;
        }
        return false;
    }

    static bool isTimeField(string field) {
        foreach (var name in _timeFields) {
            if (string.Equals(field, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // the key of the column written this way, whether that is the key itself or the name it shows
    // under ("Duration ms" is a column named in the settings, "duration" the key it is recorded by)
    static string? namedColumn(LogSettings settings, string field) {
        foreach (var property in settings.Properties) {
            if (string.Equals(property.Key, field, StringComparison.OrdinalIgnoreCase)) return property.Key;
            if (string.Equals(property.Value.Name, field, StringComparison.OrdinalIgnoreCase)) return property.Key;
        }
        return null;
    }

    /// <summary>
    /// Whether one value holds what a term is looking for. A wildcard changes nothing about where
    /// the match may be: a term is found anywhere in the value, with or without one, so adding a
    /// <c>*</c> to a term that was finding entries never takes them away again.
    /// </summary>
    bool matchesText(string text, Needle needle) {
        if (needle.Text.Length == 0) return true;
        return needle.Pattern is string pattern
            ? MatchesWildcard(text, pattern, CaseSensitive)
            : text.Contains(needle.Text, CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One thing a term looks for. Plain text is found with a substring search; text with a
    /// wildcard in it is walked by <see cref="MatchesWildcard"/>, which matches the whole of a
    /// value - so the pattern is padded with stars here, once, to mean the same "anywhere in the
    /// value" a plain term does.
    /// </summary>
    sealed class Needle {
        public Needle(string text) {
            Text = text;
            Pattern = hasWildcard(text) ? "*" + text + "*" : null;
        }
        public readonly string Text;
        public readonly string? Pattern;
        static bool hasWildcard(string text) {
            foreach (var c in text) if (c is '*' or '?') return true;
            return false;
        }
    }

    internal static string timeText(DateTime timestamp) => timestamp.ToString(TimeFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// One value as a search reads it: the plainest text of it, in the invariant culture so the
    /// same query finds the same entries whatever the machine is set to. Binary values have no
    /// text and are never matched.
    /// </summary>
    internal static string textOf(object? value) => value switch {
        null => string.Empty,
        string s => s,
        DateTime dt => timeText(dt),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        byte[] => string.Empty,
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>
    /// Whether a pattern of <c>*</c> (any run of characters, none included) and <c>?</c> (exactly
    /// one) covers the whole of the text. Walked rather than compiled: a log search runs this once
    /// per value of every record it reads, and building a regular expression for each of them
    /// would cost more than the matching does.
    /// </summary>
    public static bool MatchesWildcard(string text, string pattern, bool caseSensitive = false) {
        var t = 0;
        var p = 0;
        var starP = -1; // the last * met, to come back to when the run after it does not fit
        var starT = 0;
        while (t < text.Length) {
            if (p < pattern.Length && (pattern[p] == '?' || same(pattern[p], text[t], caseSensitive))) {
                t++;
                p++;
            } else if (p < pattern.Length && pattern[p] == '*') {
                starP = p++;
                starT = t;
            } else if (starP >= 0) { // let the last * swallow one more character and try again
                p = starP + 1;
                t = ++starT;
            } else {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*') p++; // trailing stars match nothing at all
        return p == pattern.Length;
    }

    static bool same(char a, char b, bool caseSensitive)
        => caseSensitive ? a == b : char.ToUpperInvariant(a) == char.ToUpperInvariant(b);

    /// <summary>
    /// Words, with quotes holding one together. A quote can open a term or come after a column
    /// name (<c>duration:"1 000"</c>), and an unclosed one runs to the end of the query rather
    /// than being an error - a search is typed a character at a time, and the half-written form of
    /// it should still find something.
    ///
    /// A leading <c>-</c> is taken off here, since it is the term that is excluded and not the
    /// dash that is searched for. Quoting it (<c>"-5"</c>) is how a term beginning with one is
    /// looked for as itself.
    /// </summary>
    static List<Token> tokenize(string query) {
        var tokens = new List<Token>();
        var sb = new StringBuilder();
        var negated = false;
        var started = false; // something has been written into this token, quotes included
        var colonAt = -1; // where the first colon outside the quotes fell, if there was one
        var inQuotes = false;
        void flush() {
            if (started) tokens.Add(new Token(sb.ToString(), negated, colonAt));
            sb.Clear();
            negated = false;
            started = false;
            colonAt = -1;
        }
        foreach (var c in query) {
            if (c == '"') {
                started = true;
                inQuotes = !inQuotes;
            } else if (!inQuotes && char.IsWhiteSpace(c)) {
                flush();
            } else if (!inQuotes && !started && c == '-') {
                negated = true; // and nothing written: a term of a dash alone falls away below
                started = true;
            } else {
                if (c == ':' && !inQuotes && colonAt < 0) colonAt = sb.Length;
                sb.Append(c);
                started = true;
            }
        }
        flush();
        return tokens;
    }

    readonly record struct Token(string Text, bool Negated, int ColonAt);
    // Text is the whole term as written, Value what is left of it once a column name is taken off:
    // which of the two is searched is decided per entry, by whether the column turns out to exist.
    sealed class Term {
        public Term(string text, string? field, string value, bool negated) {
            Text = new(text);
            Field = field;
            Value = new(value);
            Negated = negated;
        }
        public readonly Needle Text;
        public readonly string? Field;
        public readonly Needle Value;
        public readonly bool Negated;
    }
}
