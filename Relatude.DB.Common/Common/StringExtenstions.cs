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
        public static ulong XXH64Hash(this string s) {
            return XXH64.DigestOf(Encoding.UTF8.GetBytes(s));
        }
        public static string Decamelize(this string s) {
            if (string.IsNullOrEmpty(s)) return s;
            //return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s);
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++) {
                if (i > 0 && char.IsUpper(s[i])) sb.Append(' ');
                sb.Append(i == 0 ? char.ToUpper(s[i]) : char.ToLower(s[i]));
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
