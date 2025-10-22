using System.Text;
namespace Relatude.DB.Query.Parsing.Tokens;
public static class StringLiteralParser {
    /// <summary>
    /// Extracts and decodes a C#-style string literal starting at startPos.
    /// Supports escapes: \0 \a \b \f \n \r \t \v \\ \' \" \uXXXX \UXXXXXXXX \xH[H][H][H]
    /// 
    /// Success: returns decoded string, sets endPos to first index AFTER the closing quote.
    /// Error: returns null, sets endPos = -1.
    /// </summary>
    public static string extractStringLiteral(string sourceText, int startPos, char stringQuotationChar, out int endPos) {
        endPos = -1;

        if (sourceText == null) throw new Exception("Invalid string literal");

        if (startPos < 0 || startPos >= sourceText.Length) throw new Exception("Invalid string literal");

        if (sourceText[startPos] != stringQuotationChar) throw new Exception("Invalid \\U escape");

        var sb = new StringBuilder();
        int i = startPos + 1; // position after opening quote

        while (i < sourceText.Length) {
            char c = sourceText[i++];

            // Closing quote?
            if (c == stringQuotationChar) {
                endPos = i;
                return sb.ToString();
            }

            //// Newlines are not allowed in regular C# string literals
            //if (c == '\r' || c == '\n') throw new Exception("Newline in string literal");

            if (c != '\\') {
                sb.Append(c);
                continue;
            }

            // Escape sequence
            if (i >= sourceText.Length)
                throw new Exception("Invalid escape sequence at end of string");

            char esc = sourceText[i++];

            switch (esc) {
                case '\'': sb.Append('\''); break;
                case '\"': sb.Append('\"'); break;
                case '\\': sb.Append('\\'); break;
                case '0': sb.Append('\0'); break;
                case 'a': sb.Append('\a'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'v': sb.Append('\v'); break;

                case 'u': {
                        // \uXXXX (exactly 4 hex)
                        if (!TryReadHex(sourceText, ref i, 4, 4, out int codePoint))
                            throw new Exception("Invalid \\u escape");
                        AppendCodePoint(sb, codePoint);
                        break;
                    }

                case 'U': {
                        // \UXXXXXXXX (exactly 8 hex)
                        if (!TryReadHex(sourceText, ref i, 8, 8, out int codePoint))
                            throw new Exception("Invalid \\U escape");
                        if (codePoint < 0 || codePoint > 0x10FFFF)
                            throw new Exception("Invalid \\U escape");
                        AppendCodePoint(sb, codePoint);
                        break;
                    }

                case 'x': {
                        // \xH[H][H][H] (1 to 4 hex digits)
                        if (!TryReadHex(sourceText, ref i, 1, 4, out int codePoint))
                            throw new Exception("Invalid \\x escape");
                        AppendCodePoint(sb, codePoint);
                        break;
                    }

                default:
                    throw new Exception("Unknown escape");
            }
        }

        // If we got here, we ran out of input without finding the closing quote
        throw new Exception("Unterminated string literal");

        static bool IsHex(char ch) {
            return ch >= '0' && ch <= '9'
                || ch >= 'a' && ch <= 'f'
                || ch >= 'A' && ch <= 'F';
        }

        static int HexVal(char ch) {
            if (ch >= '0' && ch <= '9') return ch - '0';
            if (ch >= 'a' && ch <= 'f') return 10 + (ch - 'a');
            return 10 + (ch - 'A');
        }

        static bool TryReadHex(string s, ref int index, int minDigits, int maxDigits, out int value) {
            int start = index;
            int digits = 0;
            int acc = 0;

            while (index < s.Length && digits < maxDigits && IsHex(s[index])) {
                acc = acc << 4 | HexVal(s[index]);
                index++;
                digits++;
            }

            if (digits < minDigits) {
                value = 0;
                return false;
            }

            value = acc;
            return true;
        }

        static void AppendCodePoint(StringBuilder sb2, int codePoint) {
            // Normalize to valid scalar; C# char can hold UTF-16 units, so use ConvertFromUtf32
            if (codePoint < 0 || codePoint > 0x10FFFF)
                throw new ArgumentOutOfRangeException(nameof(codePoint), "Invalid Unicode code point.");

            string s = char.ConvertFromUtf32(codePoint);
            sb2.Append(s);
        }
    }
}
