using System.Collections.Generic;
using static System.Environment;

namespace Relatude.DB.Query.ExpressionToString.ZSpitz {
    public static class LanguageNames {
        public const string CSharp = "C#";
        public const string VisualBasic = "Visual Basic";
    }

    public enum Language {
        CSharp,
        VisualBasic
    }

    public enum ConversionStrategy {
        None,
        Assignable,
        BuiltIn,
        Method
    }

    public record ProcessResult(int ExitCode, string StdOut, string StdErr) {
        public override string? ToString() =>
            StdOut +
            (StdOut is not (null or "") && ExitCode > 0 ? "\n" : "") +
            (ExitCode != 0 ?
                $"{ExitCode}\n{StdErr}" :
                "");
    }
}
