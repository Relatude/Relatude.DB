using System.Text;

namespace Relatude.DB.GraphQL.Schema;

/// <summary>
/// Hands out unique, valid GraphQL names deterministically.
/// One registry instance per name space (type names, Query field names).
/// </summary>
internal sealed class NameRegistry {
    readonly HashSet<string> _used = new(StringComparer.Ordinal);
    public NameRegistry(params string[] reserved) { foreach (var r in reserved) _used.Add(r); }

    /// <summary>Claims the first available candidate (sanitized); falls back to a deterministic numeric suffix.</summary>
    public string Claim(params string?[] candidates) {
        foreach (var c in candidates) {
            if (string.IsNullOrEmpty(c)) continue;
            var s = Sanitize(c);
            if (_used.Add(s)) return s;
        }
        var first = candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c)) ?? "Type";
        var baseName = Sanitize(first);
        for (var i = 2; ; i++) {
            var s = baseName + "_" + i;
            if (_used.Add(s)) return s;
        }
    }

    /// <summary>Reduces a name to the GraphQL name grammar /[_A-Za-z][_0-9A-Za-z]*/ (no leading "__").</summary>
    public static string Sanitize(string name) {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name) {
            if (ch == '_' || char.IsAsciiLetterOrDigit(ch)) sb.Append(ch);
        }
        if (sb.Length == 0) sb.Append('X');
        if (char.IsAsciiDigit(sb[0])) sb.Insert(0, '_');
        while (sb.Length > 1 && sb[0] == '_' && sb[1] == '_') sb.Remove(0, 1); // "__" prefix is reserved for introspection
        return sb.ToString();
    }

    /// <summary>Sanitizes and lowercases the leading uppercase run: "Name" → "name", "URLValue" → "urlValue", "ID" → "id".</summary>
    public static string CamelCase(string name) {
        var s = Sanitize(name);
        var chars = s.ToCharArray();
        var upperRun = 0;
        while (upperRun < chars.Length && char.IsAsciiLetterUpper(chars[upperRun])) upperRun++;
        if (upperRun == 0) return s;
        // if the run is followed by a lowercase letter, the run's last upper starts the next word ("URLValue" → "urlValue")
        var lowerCount = upperRun < chars.Length && char.IsAsciiLetterLower(chars[upperRun]) ? Math.Max(1, upperRun - 1) : upperRun;
        for (var i = 0; i < lowerCount; i++) chars[i] = char.ToLowerInvariant(chars[i]);
        return new string(chars);
    }

    /// <summary>Naive english pluralization; deterministic, collision-safe via Claim.</summary>
    public static string Pluralize(string name) {
        if (name.Length == 0) return name;
        if (name.EndsWith("s", StringComparison.Ordinal) || name.EndsWith("x", StringComparison.Ordinal) ||
            name.EndsWith("z", StringComparison.Ordinal) || name.EndsWith("ch", StringComparison.Ordinal) ||
            name.EndsWith("sh", StringComparison.Ordinal)) return name + "es";
        if (name.Length > 1 && name.EndsWith("y", StringComparison.Ordinal) && !isVowel(name[^2])) return name[..^1] + "ies";
        return name + "s";
    }
    static bool isVowel(char c) => "aeiouAEIOU".IndexOf(c) >= 0;
}
