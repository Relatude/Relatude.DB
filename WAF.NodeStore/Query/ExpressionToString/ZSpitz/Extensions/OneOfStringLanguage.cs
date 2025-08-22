using OneOf;
using WAF.Query.ExpressionToString.ZSpitz;
using static WAF.Query.ExpressionToString.ZSpitz.Language;

namespace WAF.Query.ExpressionToString.ZSpitz.Extensions {
    public static class OneOfStringLanguageExtensions {
        public static Language? ResolveLanguage(this OneOf<string, Language?> languageArg) {
            if (languageArg.IsT1) { return languageArg.AsT1; }
            return languageArg.AsT0 switch {
                LanguageNames.CSharp => CSharp,
                LanguageNames.VisualBasic => VisualBasic,
                _ => null,
            };
        }
    }
}
