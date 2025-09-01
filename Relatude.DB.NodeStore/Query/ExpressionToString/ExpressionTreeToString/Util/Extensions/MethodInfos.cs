using Relatude.DB.Query.ExpressionToString.ZSpitz.Extensions;
using System.Reflection;

namespace Relatude.DB.Query.ExpressionToString.ExpressionTreeToString.Util.Extensions {
    internal static class MethodInfoExtensions {
        internal static readonly HashSet<MethodInfo> StringConcats;
        internal static readonly HashSet<MethodInfo> StringFormats;

        static MethodInfoExtensions() {
            var methods = typeof(string)
                .GetMethods()
                .Where(x => x.Name switch {
                    "Concat" => x.GetParameters().All(
                        y => y.ParameterType.In(typeof(string), typeof(string[]))
                    ),
                    "Format" => x.GetParameters().First().ParameterType == typeof(string),
                    _ => false,
                })
                .ToLookup(x => x.Name);

            StringConcats = methods["Concat"].ToHashSet();
            StringFormats = methods["Format"].ToHashSet();
        }

        internal static bool IsStringConcat(this MethodInfo mthd) => mthd.In(StringConcats);
        internal static bool IsStringFormat(this MethodInfo mthd) => mthd.In(StringFormats);

        // Microsoft.VisualBasic.CompilerServices is not available to .NET Standard, so we have to check by name
        internal static bool IsVBLike(this MethodInfo mthd) =>
            mthd.DeclaringType?.FullName == "Microsoft.VisualBasic.CompilerServices.LikeOperator" &&
            mthd.Name.StartsWith("Like");
    }
}
