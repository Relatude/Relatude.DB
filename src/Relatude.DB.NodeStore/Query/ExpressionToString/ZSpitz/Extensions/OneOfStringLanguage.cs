using OneOf;
using Relatude.DB.Query.ExpressionToString.ZSpitz;
using static Relatude.DB.Query.ExpressionToString.ZSpitz.Language;

namespace Relatude.DB.Query.ExpressionToString.ZSpitz.Extensions {
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
