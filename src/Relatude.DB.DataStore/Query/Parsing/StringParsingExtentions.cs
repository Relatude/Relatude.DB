namespace Relatude.DB.Query.Parsing;
public static class StringExtensions {
    public static int IndexOfAnyOutsideStringLiterals(this string s, int startIndex, params char[] anyOf) {
        return IndexOfAnyOutsideStringLiterals(s, startIndex, s.Length - 1, anyOf);
    }
    public static int IndexOfAnyOutsideStringLiterals(this string s, int startIndex, int endIndex, params char[] anyOf) {
        if (s == null) throw new ArgumentNullException(nameof(s));
        if (anyOf == null || anyOf.Length == 0) return -1;
        if (startIndex < 0 || startIndex >= s.Length) throw new ArgumentOutOfRangeException(nameof(startIndex));
        if (endIndex < startIndex || endIndex >= s.Length) throw new ArgumentOutOfRangeException(nameof(endIndex));

        bool insideStringLiteral = false;
        char prevChar = '\0';

        for (int i = startIndex; i <= endIndex; i++) {
            char c = s[i];

            // Handle escape sequences within string literals, like \" or \\
            if (insideStringLiteral && prevChar == '\\') {
                prevChar = c;
                continue;
            }

            // Toggle string literal state when encountering an unescaped "
            if (c == '"') {
                insideStringLiteral = !insideStringLiteral;
            }

            // If outside string literal and character matches any of the target characters, return the index
            if (!insideStringLiteral && anyOf.Contains(c)) {
                return i;
            }

            prevChar = c;
        }

        return -1;
    }
}
