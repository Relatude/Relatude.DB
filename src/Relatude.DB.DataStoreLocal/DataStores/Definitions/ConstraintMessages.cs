using System.Globalization;
using Relatude.DB.DataStores.Definitions.PropertyTypes;

namespace Relatude.DB.DataStores.Definitions;

internal static class ConstraintMessages {
    const int maxShownValueLength = 100;
    const int maxListedValues = 12;

    public static ValueConstraintException TooLong(Property p, string value, int maxLength)
        => refuse(p, p.CodeName + " is too long: it has " + characters(value.Length) + ", and at most " + maxLength.ToString(CultureInfo.InvariantCulture) + " are allowed. ");

    public static ValueConstraintException TooShort(Property p, string value, int minLength) {
        if (value.Length == 0) return refuse(p, p.CodeName + " is required. ");
        return refuse(p, p.CodeName + " is too short: it has " + characters(value.Length) + ", and at least " + minLength.ToString(CultureInfo.InvariantCulture) + " are needed. ");
    }

    public static ValueConstraintException NoPatternMatch(Property p, string value, string pattern, string? customMessage) {
        var hasCustomMessage = !string.IsNullOrWhiteSpace(customMessage);
        if (value.Length == 0) return refuse(p, p.CodeName + " is required" + (hasCustomMessage ? ". " : ": an empty value does not match the pattern " + pattern + ". "));
        if (hasCustomMessage) return refuse(p, p.CodeName + " " + quoted(value) + " is not valid: " + withFullStop(customMessage!));
        return refuse(p, p.CodeName + " " + quoted(value) + " is not in the expected format: it does not match the pattern " + pattern + ". ");
    }

    public static ValueConstraintException NotLegalValue(Property p, object value, IEnumerable<object> legalValues)
        => refuse(p, p.CodeName + " " + format(value) + " is not one of its legal values: " + list(legalValues) + ". ");

    public static ValueConstraintException IllegalCharacters(Property p, string value, IEnumerable<char> illegal) {
        var shown = string.Join(" ", illegal.Distinct().Take(maxListedValues).Select(describe));
        return refuse(p, p.CodeName + " " + quoted(value) + " contains characters that are not allowed: " + shown + ". ");
    }

    public static ValueConstraintException AboveMaximum(Property p, object value, object maximum)
        => refuse(p, p.CodeName + " is " + format(value) + ", which is above the maximum of " + format(maximum) + ". ");

    public static ValueConstraintException BelowMinimum(Property p, object value, object minimum)
        => refuse(p, p.CodeName + " is " + format(value) + ", which is below the minimum of " + format(minimum) + ". ");

    public static string NotUnique(Property p, object value) {
        var ignoresCase = p is StringProperty { CaseSensitive: false };
        return "Another node already has " + p.CodeName + " " + format(value) + ", and " + p.CodeName + " must be unique"
            + (ignoresCase ? " (upper and lower case do not count as a difference). " : ". ");
    }

    static ValueConstraintException refuse(Property p, string message) => new(message, p.Id);

    static string characters(int count) => count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " character" : " characters");

    static string withFullStop(string message) {
        var trimmed = message.Trim();
        return (trimmed.EndsWith('.') || trimmed.EndsWith('!') || trimmed.EndsWith('?') ? trimmed : trimmed + ".") + " ";
    }

    static string list(IEnumerable<object> values) {
        var all = values.Select(format).ToList();
        var shown = string.Join(", ", all.Take(maxListedValues));
        return all.Count > maxListedValues ? shown + " and " + (all.Count - maxListedValues).ToString(CultureInfo.InvariantCulture) + " more" : shown;
    }

    static string describe(char c) => c switch {
        ' ' => "space",
        '\t' => "tab",
        '\n' => "line break",
        '\r' => "carriage return",
        _ when char.IsControl(c) => "U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture),
        _ => "\"" + c + "\"",
    };

    static string format(object value) => value switch {
        string s => quoted(s),
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    static string quoted(string value) => "\"" + (value.Length > maxShownValueLength ? value[..maxShownValueLength] + "..." : value) + "\"";
}
