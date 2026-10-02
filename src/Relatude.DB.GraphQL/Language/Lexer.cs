using System.Globalization;
using System.Text;

namespace Relatude.DB.GraphQL.Language;

/// <summary>Thrown for a document that does not follow the GraphQL grammar. Carries the 1-based line and column.</summary>
public sealed class GraphQLSyntaxException(string message, int line, int column) : Exception(message) {
    public int Line { get; } = line;
    public int Column { get; } = column;
}

internal enum TokenKind {
    EndOfFile, Bang, Dollar, Amp, ParenL, ParenR, Spread, Colon, Equals, At, BracketL, BracketR, BraceL, BraceR, Pipe,
    Name, Int, Float, String, BlockString,
}

internal readonly struct Token(TokenKind kind, int start, int end, string value) {
    public TokenKind Kind { get; } = kind;
    public int Start { get; } = start;
    public int End { get; } = end;
    /// <summary>Name text, number text or the decoded string value.</summary>
    public string Value { get; } = value;
}

/// <summary>Tokenizer for GraphQL executable documents (the query language, not the type system language).</summary>
internal sealed class Lexer {
    readonly string _src;
    int _pos;

    public Lexer(string source) { _src = source; }

    public static (int Line, int Column) LineColumn(string source, int position) {
        var line = 1;
        var lineStart = 0;
        var end = Math.Min(position, source.Length);
        for (var i = 0; i < end; i++) {
            var c = source[i];
            if (c == '\n' || (c == '\r' && (i + 1 >= source.Length || source[i + 1] != '\n'))) {
                line++;
                lineStart = i + 1;
            }
        }
        return (line, end - lineStart + 1);
    }

    GraphQLSyntaxException error(string message, int position) {
        var (line, column) = LineColumn(_src, position);
        return new GraphQLSyntaxException($"Syntax error: {message} (line {line}, column {column}).", line, column);
    }

    public Token Next() {
        skipIgnored();
        if (_pos >= _src.Length) return new Token(TokenKind.EndOfFile, _pos, _pos, "");
        var start = _pos;
        var c = _src[_pos];
        switch (c) {
            case '!': _pos++; return new Token(TokenKind.Bang, start, _pos, "!");
            case '$': _pos++; return new Token(TokenKind.Dollar, start, _pos, "$");
            case '&': _pos++; return new Token(TokenKind.Amp, start, _pos, "&");
            case '(': _pos++; return new Token(TokenKind.ParenL, start, _pos, "(");
            case ')': _pos++; return new Token(TokenKind.ParenR, start, _pos, ")");
            case ':': _pos++; return new Token(TokenKind.Colon, start, _pos, ":");
            case '=': _pos++; return new Token(TokenKind.Equals, start, _pos, "=");
            case '@': _pos++; return new Token(TokenKind.At, start, _pos, "@");
            case '[': _pos++; return new Token(TokenKind.BracketL, start, _pos, "[");
            case ']': _pos++; return new Token(TokenKind.BracketR, start, _pos, "]");
            case '{': _pos++; return new Token(TokenKind.BraceL, start, _pos, "{");
            case '}': _pos++; return new Token(TokenKind.BraceR, start, _pos, "}");
            case '|': _pos++; return new Token(TokenKind.Pipe, start, _pos, "|");
            case '.':
                if (_pos + 2 < _src.Length && _src[_pos + 1] == '.' && _src[_pos + 2] == '.') {
                    _pos += 3;
                    return new Token(TokenKind.Spread, start, _pos, "...");
                }
                throw error("unexpected \".\"", start);
            case '"':
                if (_pos + 2 < _src.Length && _src[_pos + 1] == '"' && _src[_pos + 2] == '"') return readBlockString();
                return readString();
        }
        if (c == '_' || char.IsAsciiLetter(c)) return readName();
        if (c == '-' || char.IsAsciiDigit(c)) return readNumber();
        throw error($"unexpected character \"{printable(c)}\"", start);
    }

    static string printable(char c) => c < ' ' ? "U+" + ((int)c).ToString("X4") : c.ToString();

    void skipIgnored() {
        while (_pos < _src.Length) {
            var c = _src[_pos];
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == ',' || c == (char)0xFEFF) { _pos++; continue; }
            if (c == '#') {
                while (_pos < _src.Length && _src[_pos] != '\n' && _src[_pos] != '\r') _pos++;
                continue;
            }
            break;
        }
    }

    Token readName() {
        var start = _pos;
        while (_pos < _src.Length && (_src[_pos] == '_' || char.IsAsciiLetterOrDigit(_src[_pos]))) _pos++;
        return new Token(TokenKind.Name, start, _pos, _src[start.._pos]);
    }

    Token readNumber() {
        var start = _pos;
        var isFloat = false;
        if (_src[_pos] == '-') _pos++;
        if (_pos >= _src.Length || !char.IsAsciiDigit(_src[_pos])) throw error("invalid number, expected a digit", start);
        if (_src[_pos] == '0') {
            _pos++;
            if (_pos < _src.Length && char.IsAsciiDigit(_src[_pos])) throw error("invalid number, unexpected digit after 0", start);
        } else {
            while (_pos < _src.Length && char.IsAsciiDigit(_src[_pos])) _pos++;
        }
        if (_pos < _src.Length && _src[_pos] == '.') {
            isFloat = true;
            _pos++;
            if (_pos >= _src.Length || !char.IsAsciiDigit(_src[_pos])) throw error("invalid number, expected a digit after \".\"", start);
            while (_pos < _src.Length && char.IsAsciiDigit(_src[_pos])) _pos++;
        }
        if (_pos < _src.Length && (_src[_pos] == 'e' || _src[_pos] == 'E')) {
            isFloat = true;
            _pos++;
            if (_pos < _src.Length && (_src[_pos] == '+' || _src[_pos] == '-')) _pos++;
            if (_pos >= _src.Length || !char.IsAsciiDigit(_src[_pos])) throw error("invalid number, expected an exponent digit", start);
            while (_pos < _src.Length && char.IsAsciiDigit(_src[_pos])) _pos++;
        }
        if (_pos < _src.Length && (_src[_pos] == '.' || _src[_pos] == '_' || char.IsAsciiLetter(_src[_pos]))) {
            throw error($"invalid number, unexpected \"{_src[_pos]}\"", start);
        }
        return new Token(isFloat ? TokenKind.Float : TokenKind.Int, start, _pos, _src[start.._pos]);
    }

    Token readString() {
        var start = _pos;
        _pos++; // opening quote
        var sb = new StringBuilder();
        while (true) {
            if (_pos >= _src.Length) throw error("unterminated string", start);
            var c = _src[_pos];
            if (c == '"') { _pos++; break; }
            if (c == '\n' || c == '\r') throw error("unterminated string", start);
            if (c < ' ' && c != '\t') throw error($"invalid character \"{printable(c)}\" in string", _pos);
            if (c == '\\') {
                _pos++;
                if (_pos >= _src.Length) throw error("unterminated string", start);
                var e = _src[_pos];
                switch (e) {
                    case '"': sb.Append('"'); _pos++; break;
                    case '\\': sb.Append('\\'); _pos++; break;
                    case '/': sb.Append('/'); _pos++; break;
                    case 'b': sb.Append('\b'); _pos++; break;
                    case 'f': sb.Append('\f'); _pos++; break;
                    case 'n': sb.Append('\n'); _pos++; break;
                    case 'r': sb.Append('\r'); _pos++; break;
                    case 't': sb.Append('\t'); _pos++; break;
                    case 'u': readUnicodeEscape(sb); break;
                    default: throw error($"invalid escape sequence \"\\{e}\"", _pos - 1);
                }
                continue;
            }
            sb.Append(c);
            _pos++;
        }
        return new Token(TokenKind.String, start, _pos, sb.ToString());
    }

    void readUnicodeEscape(StringBuilder sb) {
        var escapeStart = _pos - 1;
        _pos++; // 'u'
        int codePoint;
        if (_pos < _src.Length && _src[_pos] == '{') {
            _pos++;
            var hexStart = _pos;
            while (_pos < _src.Length && char.IsAsciiHexDigit(_src[_pos])) _pos++;
            if (_pos >= _src.Length || _src[_pos] != '}' || _pos == hexStart || _pos - hexStart > 8) throw error("invalid unicode escape sequence", escapeStart);
            codePoint = int.Parse(_src[hexStart.._pos], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            _pos++;
        } else {
            if (_pos + 4 > _src.Length) throw error("invalid unicode escape sequence", escapeStart);
            var hex = _src.Substring(_pos, 4);
            if (!hex.All(char.IsAsciiHexDigit)) throw error("invalid unicode escape sequence", escapeStart);
            codePoint = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            _pos += 4;
            // a high surrogate followed by an escaped low surrogate forms one code point
            if (codePoint is >= 0xD800 and <= 0xDBFF && _pos + 6 <= _src.Length && _src[_pos] == '\\' && _src[_pos + 1] == 'u') {
                var low = _src.Substring(_pos + 2, 4);
                if (low.All(char.IsAsciiHexDigit)) {
                    var lowValue = int.Parse(low, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    if (lowValue is >= 0xDC00 and <= 0xDFFF) {
                        codePoint = char.ConvertToUtf32((char)codePoint, (char)lowValue);
                        _pos += 6;
                    }
                }
            }
        }
        if (codePoint > 0x10FFFF || codePoint is >= 0xD800 and <= 0xDFFF) throw error("invalid unicode escape sequence", escapeStart);
        sb.Append(char.ConvertFromUtf32(codePoint));
    }

    Token readBlockString() {
        var start = _pos;
        _pos += 3;
        var raw = new StringBuilder();
        while (true) {
            if (_pos >= _src.Length) throw error("unterminated block string", start);
            if (_src[_pos] == '"' && _pos + 2 < _src.Length && _src[_pos + 1] == '"' && _src[_pos + 2] == '"') {
                _pos += 3;
                break;
            }
            if (_src[_pos] == '\\' && _pos + 3 < _src.Length && _src[_pos + 1] == '"' && _src[_pos + 2] == '"' && _src[_pos + 3] == '"') {
                raw.Append("\"\"\"");
                _pos += 4;
                continue;
            }
            raw.Append(_src[_pos]);
            _pos++;
        }
        return new Token(TokenKind.BlockString, start, _pos, BlockStringValue(raw.ToString()));
    }

    /// <summary>The spec's BlockStringValue(): common indentation removed, leading and trailing blank lines dropped.</summary>
    internal static string BlockStringValue(string raw) {
        var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int? commonIndent = null;
        for (var i = 1; i < lines.Length; i++) {
            var line = lines[i];
            var indent = 0;
            while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t')) indent++;
            if (indent == line.Length) continue;
            if (commonIndent == null || indent < commonIndent) commonIndent = indent;
        }
        var list = new List<string>(lines);
        if (commonIndent is > 0) {
            for (var i = 1; i < list.Count; i++) list[i] = list[i].Length >= commonIndent ? list[i][commonIndent.Value..] : list[i].TrimStart(' ', '\t');
        }
        while (list.Count > 0 && isBlank(list[0])) list.RemoveAt(0);
        while (list.Count > 0 && isBlank(list[^1])) list.RemoveAt(list.Count - 1);
        return string.Join("\n", list);
        static bool isBlank(string s) => s.All(c => c == ' ' || c == '\t');
    }
}
