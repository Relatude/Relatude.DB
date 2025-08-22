using System.Collections.Generic;

namespace WAF.Query.ExpressionToString.ZSpitz.Extensions {
    public static class DictionaryExtensions {
        public static void AddRange<TKey, TValue>(this IDictionary<TKey, TValue> dict, IEnumerable<(TKey, TValue)> src) where TKey : notnull => src.ForEach(t => dict.Add(t.Item1, t.Item2));
    }
}
