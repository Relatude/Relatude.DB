using System;

namespace Relatude.DB.Query.ExpressionToString.OneOf {
    internal static class Functions {
        internal static string FormatValue<T>(T value) => $"{typeof(T).FullName}: {value?.ToString()}";
        internal static string FormatValue<T>(object @this, object @base, T value) {
            if (ReferenceEquals(@this, value)) {
                if (@base is null) return "null";
                return @base.ToString()!;
            } else {
                return $"{typeof(T).FullName}: {value?.ToString()}";
            }
        }
    }
}
