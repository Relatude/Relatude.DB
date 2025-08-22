using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace WAF.Query.ExpressionToString.ZSpitz.Extensions {
    public static class IListTExtensions {
        public static bool TryGetValue<T>(this IList<T> lst, int index, [MaybeNullWhen(returnValue: false)] out T result) {
            result = default!;
            if (lst == null) { return false; }
            if (index < 0 || index >= lst.Count) { return false; }
            result = lst[index];
            return true;
        }
    }
}
