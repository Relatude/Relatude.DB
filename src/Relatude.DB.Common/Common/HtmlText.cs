using System.Net;
using System.Text;
namespace Relatude.DB.Common;

public static class HtmlText {
    static readonly HashSet<string> _tagsRenderedWithoutGap = new(StringComparer.OrdinalIgnoreCase) {
        "a", "abbr", "b", "bdi", "bdo", "cite", "code", "data", "del", "dfn", "em", "font", "i", "ins", "kbd",
        "mark", "q", "s", "samp", "small", "span", "strike", "strong", "sub", "sup", "time", "tt", "u", "var", "wbr",
    };
    static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _tagsRenderedWithoutGapLookup = _tagsRenderedWithoutGap.GetAlternateLookup<ReadOnlySpan<char>>();
    const int _maxEntityLength = 32;
    const int _notATag = -1;

    public static string ToText(string html) {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var sb = new StringBuilder(html.Length);
        var i = 0;
        while (i < html.Length) {
            var c = html[i];
            if (c == '<' && i + 1 < html.Length && startsTag(html[i + 1])) {
                var end = skipTag(html, i, out var rendersGap);
                if (end != _notATag) {
                    if (rendersGap) appendSpace(sb);
                    i = end;
                    continue;
                }
            } else if (c == '&') {
                i = appendEntity(html, i, sb);
                continue;
            }
            append(sb, c);
            i++;
        }
        if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        return sb.ToString();
    }
    static bool startsTag(char c) => char.IsAsciiLetter(c) || c == '/' || c == '!' || c == '?';
    static void append(StringBuilder sb, char c) {
        if (char.IsWhiteSpace(c)) appendSpace(sb);
        else sb.Append(c);
    }
    static void appendSpace(StringBuilder sb) {
        if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
    }
    static int skipTag(string html, int start, out bool rendersGap) {
        rendersGap = true;
        if (string.CompareOrdinal(html, start, "<!--", 0, 4) == 0) {
            rendersGap = false;
            var commentEnd = html.IndexOf("-->", start + 4, StringComparison.Ordinal);
            return commentEnd < 0 ? html.Length : commentEnd + 3;
        }
        var p = start + 1;
        var isClosingTag = html[p] == '/';
        if (isClosingTag) p++;
        var nameStart = p;
        while (p < html.Length && (char.IsAsciiLetterOrDigit(html[p]) || html[p] == '-' || html[p] == ':')) p++;
        var name = html.AsSpan(nameStart, p - nameStart);
        var end = findTagEnd(html, p);
        if (end == _notATag) return _notATag;
        if (_tagsRenderedWithoutGapLookup.Contains(name)) rendersGap = false;
        else if (!isClosingTag && isScriptOrStyle(name)) return skipPastClosingTag(html, end, name);
        return end;
    }
    static bool isScriptOrStyle(ReadOnlySpan<char> name) => name.Equals("script", StringComparison.OrdinalIgnoreCase) || name.Equals("style", StringComparison.OrdinalIgnoreCase);
    static int skipPastClosingTag(string html, int from, ReadOnlySpan<char> name) {
        var closingTag = html.IndexOf("</" + name.ToString(), from, StringComparison.OrdinalIgnoreCase);
        if (closingTag < 0) return html.Length;
        var closingTagEnd = html.IndexOf('>', closingTag);
        return closingTagEnd < 0 ? html.Length : closingTagEnd + 1;
    }
    static int findTagEnd(string html, int p) {
        var quote = '\0';
        var quoteMayOpen = false;
        for (; p < html.Length; p++) {
            var c = html[p];
            if (quote != '\0') {
                if (c == quote) quote = '\0';
            } else if (c == '>') {
                return p + 1;
            } else if (quoteMayOpen && (c == '"' || c == '\'')) {
                quote = c;
            }
            if (!char.IsWhiteSpace(c)) quoteMayOpen = c == '=';
        }
        return _notATag;
    }
    static int appendEntity(string html, int start, StringBuilder sb) {
        var p = start + 1;
        var limit = Math.Min(html.Length, start + _maxEntityLength);
        while (p < limit && (char.IsAsciiLetterOrDigit(html[p]) || html[p] == '#')) p++;
        if (p == start + 1 || p >= limit || html[p] != ';') {
            sb.Append('&');
            return start + 1;
        }
        foreach (var c in WebUtility.HtmlDecode(html.Substring(start, p - start + 1))) append(sb, c);
        return p + 1;
    }
}
