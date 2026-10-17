using System.Globalization;
using System.Text.RegularExpressions;

namespace Relatude.DB.Datamodels.Properties;

public static class RegularExpressionErrors {
    public static string? Validate(string? pattern) {
        if (string.IsNullOrEmpty(pattern)) return null;
        try {
            _ = new Regex(pattern);
            return null;
        } catch (ArgumentException e) {
            return Describe(e);
        }
    }

    public static string Describe(ArgumentException e) {
        if (e is not RegexParseException parse) return "The pattern is not valid: " + e.Message;
        var problem = parse.Error switch {
            RegexParseError.UnterminatedBracket => "a [ is not closed with ]",
            RegexParseError.InsufficientClosingParentheses => "a ( is not closed with )",
            RegexParseError.InsufficientOpeningParentheses => "a ) has no ( before it",
            RegexParseError.QuantifierAfterNothing => "*, + or ? has nothing before it to repeat - write \\* \\+ or \\? to mean the character itself",
            RegexParseError.NestedQuantifiersNotParenthesized => "two repeat signs follow each other (like ** or +*)",
            RegexParseError.UnescapedEndingBackslash => "the pattern ends with a single \\",
            RegexParseError.ReversedCharacterRange => "a range inside [ ] goes backwards, like z-a",
            RegexParseError.ReversedQuantifierRange => "in {n,m} the first number is larger than the second",
            RegexParseError.UnrecognizedEscape => "a \\ is followed by a character that has no special meaning - remove the \\",
            RegexParseError.ShorthandClassInCharacterRange => "a range inside [ ] uses \\d, \\w or \\s as one of its ends",
            RegexParseError.InvalidUnicodePropertyEscape or RegexParseError.UnrecognizedUnicodeProperty => "\\p{...} names a Unicode category that does not exist",
            _ => parse.Message,
        };
        return "The pattern is not valid at character " + parse.Offset.ToString(CultureInfo.InvariantCulture) + ": " + problem + ".";
    }
}
