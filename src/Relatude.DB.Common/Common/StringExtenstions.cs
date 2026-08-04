using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Relatude.DB.Hash.xxHash;

namespace Relatude.DB.Common {
    public static class StringExtenstions {
        public static string ToStringLiteral(this string text) {
            return Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(text, true);
        }
        public static string ToStringLiteral(this Guid guid) {
            return guid.ToString().ToStringLiteral();
        }
        public static string ToStringLiteral(this bool value) {
            return value ? "true" : "false";
        }
        public static ulong XXH64Hash(this string s) {
            return XXH64.DigestOf(Encoding.UTF8.GetBytes(s));
        }
        /// <summary>
        /// An exclusive upper bound such that every string starting with this prefix lies in the half
        /// open range [prefix, bound). Found by incrementing the last character that is not
        /// <see cref="char.MaxValue"/> and dropping the tail after it: the strings starting with "ab"
        /// all lie in ["ab", "ac"). Null when there is no upper bound, which is the case for the
        /// empty prefix and for a prefix of only char.MaxValue.
        /// The range never misses a prefixed string, in any lexicographic ordering of the characters,
        /// but it is only free of extra members when <see cref="HasExactOrdinalPrefixRange"/> holds.
        /// </summary>
        public static string? OrdinalPrefixUpperBound(this string prefix) {
            for (var i = prefix.Length - 1; i >= 0; i--) {
                if (prefix[i] == char.MaxValue) continue;
                var bound = prefix.ToCharArray(0, i + 1);
                bound[i]++;
                return new string(bound);
            }
            return null;
        }
        /// <summary>
        /// True when [prefix, <see cref="OrdinalPrefixUpperBound"/>) holds the strings starting with
        /// prefix and nothing else, whatever the ordering, so a range scan of an index needs no
        /// confirmation per value.
        /// It is false when the last character is char.MaxValue or a surrogate, because incrementing
        /// it then means one thing to an index ordered by UTF-16 code units and another to one
        /// ordered by UTF-8 bytes (that is, by code point), and the range widens in one of them. For
        /// the prefix "a￿" for instance the bound becomes "b", which by code point also covers
        /// "a" followed by anything above the BMP.
        /// </summary>
        public static bool HasExactOrdinalPrefixRange(this string prefix)
            => prefix.Length == 0 || !(prefix[prefix.Length - 1] == char.MaxValue || char.IsSurrogate(prefix[prefix.Length - 1]));
        public static string Decamelize(this string s, bool capitalizeFirstLetter = true) {
            if (string.IsNullOrEmpty(s)) return s;
            //return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s);
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++) {
                if (i > 0 && char.IsUpper(s[i])) sb.Append(' ');
                sb.Append(i == 0 && capitalizeFirstLetter ? char.ToUpper(s[i]) : char.ToLower(s[i]));
            }
            return sb.ToString();
        }
        public static string FixedLeft(this string s, int length, char padChar = ' ') {
            if (string.IsNullOrEmpty(s)) return new string(padChar, length);
            else if (s.Length > length && s.Length > 2)
                return s.Remove(length - 3) + "...";
            else if (s.Length > length)
                return new string('.', length);
            else if (s.Length < length) return s.PadRight(length);
            else return s;
        }
        public static string FixedRight(this string s, int length, char padChar = ' ') {
            if (string.IsNullOrEmpty(s)) return new string(padChar, length);
            else if (s.Length > length && s.Length > 2)
                return "..." + s.Substring(s.Length - length + 3);
            else if (s.Length > length)
                return new string('.', length);
            else if (s.Length < length) return s.PadLeft(length);
            else return s;
        }
        public static string InKB(this long bytes) {
            return ((bytes / 1024).ToString("### ### ### ##0") + " KB").Trim();
        }
        public static string InKB(this int bytes) {
            return ((long)bytes).InKB();
        }
    }
}
