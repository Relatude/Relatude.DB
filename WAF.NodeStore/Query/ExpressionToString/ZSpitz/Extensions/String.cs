using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using static WAF.Query.ExpressionToString.ZSpitz.Language;
using System.Diagnostics.CodeAnalysis;
using OneOf;
using System.Text.RegularExpressions;
using WAF.Query.ExpressionToString.ZSpitz.Extensions;

namespace WAF.Query.ExpressionToString.ZSpitz.Extensions {
    public static class StringExtensions {
        public static bool IsNullOrWhitespace([NotNullWhen(false)] this string? s) => string.IsNullOrWhiteSpace(s);
        public static bool IsNullOrEmpty([NotNullWhen(false)] this string? s) => string.IsNullOrEmpty(s);
        public static bool ContainsWhitespace(this string s) => s.Any(c => char.IsWhiteSpace(c));

        private static readonly Regex whitespace = new(@"\s+");
        public static string ReplaceWhitespace(this string s, string replacement = "") => whitespace.Replace(s, replacement);
        public static bool ContainsAny(this string s, params string[] testStrings) => testStrings.Any(x => s.Contains(x));
        public static bool StartsWithAny(this string s, params string[] testStrings) => testStrings.Any(x => s.StartsWith(x));
        public static bool StartsWithAny(this string s, StringComparison comparison, params string[] testStrings) => testStrings.Any(x => s.StartsWith(x, comparison));
        public static void AppendTo(this string? s, StringBuilder sb) => sb.Append(s);

        // https://stackoverflow.com/a/14502246/111794
        private static string toCSharpLiteral(this string input) {
            var literal = new StringBuilder("\"", input.Length + 2);
            foreach (var c in input) {
                switch (c) {
                    case '\'': literal.Append(@"\'"); break;
                    case '\"': literal.Append("\\\""); break;
                    case '\\': literal.Append(@"\\"); break;
                    case '\0': literal.Append(@"\0"); break;
                    case '\a': literal.Append(@"\a"); break;
                    case '\b': literal.Append(@"\b"); break;
                    case '\f': literal.Append(@"\f"); break;
                    case '\n': literal.Append(@"\n"); break;
                    case '\r': literal.Append(@"\r"); break;
                    case '\t': literal.Append(@"\t"); break;
                    case '\v': literal.Append(@"\v"); break;
                    default:
                        if (char.GetUnicodeCategory(c) != UnicodeCategory.Control) {
                            literal.Append(c);
                        } else {
                            literal.Append(@"\u");
                            literal.Append(((ushort)c).ToString("x4"));
                        }
                        break;
                }
            }
            literal.Append('\"');
            return literal.ToString();
        }

        private static readonly char[] specialChars = new[] {
            '\'','\"', '\\','\0','\a','\b','\f','\n','\r', '\t','\v'
        };
        public static bool HasSpecialCharacters(this string s) =>
            s.IndexOfAny(specialChars) > -1;

        public static string ToVerbatimString(this string s, OneOf<string, Language?> langArg) =>
            langArg.ResolveLanguage() switch {
                CSharp => s.toCSharpLiteral(),
                VisualBasic => $"\"{s.Replace("\"", "\"\"")}\"",
                _ => throw new ArgumentException("Invalid language")
            };

        public static void AppendLineTo(this string? s, StringBuilder sb, int indentationLevel = 0) {
            s = (s ?? "").TrimEnd();
            var toAppend = new string(' ', indentationLevel * 4) + s.TrimEnd();
            sb.AppendLine(toAppend);
        }

        [return: NotNullIfNotNull("s")]
        public static string? ToCamelCase(this string? s) =>
            s == null || s.Length == 0 ?
                s :
                char.ToLowerInvariant(s[0]) + s[1..];

        public static bool EndsWithAny(this string s, params string[] testStrings) => testStrings.Any(x => s.EndsWith(x));
        public static bool EndsWithAny(this string s, IEnumerable<string> testStrings) => testStrings.Any(x => s.EndsWith(x));
        public static bool EndsWithAny(this string s, StringComparison comparisonType, params string[] testStrings) => testStrings.Any(x => s.EndsWith(x, comparisonType));
        public static bool EndsWithAny(this string s, StringComparison comparisonType, IEnumerable<string> testStrings) => testStrings.Any(x => s.EndsWith(x, comparisonType));
        public static bool Contains(this string s, string toCheck, StringComparison comp) => s?.IndexOf(toCheck, comp) >= 0;
    }
}
