using Relatude.DB.Query.ExpressionToString.ZSpitz.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Relatude.DB.Query.ExpressionToString.ZSpitz.Extensions {
    public static class IEnumerableKVPExtensions {
        public static void AddRangeTo<TKey, TValue>(this IEnumerable<KeyValuePair<TKey, TValue>> toAdd, Dictionary<TKey, TValue> dict) where TKey : notnull =>
            dict.AddRange(toAdd);
        public static IEnumerable<TValue> Values<TKey, TValue>(this IEnumerable<KeyValuePair<TKey, TValue>> src) =>
            src.Select(x => x.Value);
        public static IEnumerable<TResult> SelectKVP<TKey, TValue, TResult>(this IEnumerable<KeyValuePair<TKey, TValue>> src, Func<TKey, TValue, TResult> selector) =>
            src.Select(kvp => selector(kvp.Key, kvp.Value));
        public static IEnumerable<TKey> Keys<TKey, TValue>(this IEnumerable<KeyValuePair<TKey, TValue>> src) =>
            src.Select(x => x.Key);
        public static ILookup<TKey, TValue> ToLookup<TKey, TValue>(this IEnumerable<KeyValuePair<TKey, TValue>> src) =>
            src.ToLookup(kvp => kvp.Key, kvp => kvp.Value);
        public static IEnumerable<KeyValuePair<TKey, TValue>> WhereKVP<TKey, TValue>(this IEnumerable<KeyValuePair<TKey, TValue>> src, Func<TKey, TValue, bool> predicate) =>
            src.Where(kvp => predicate(kvp.Key, kvp.Value));
        public static Dictionary<TKey, TValue> ToDictionary<TKey, TValue>(this IEnumerable<KeyValuePair<TKey, TValue>> src) where TKey : notnull =>
            src.ToDictionary(x => x.Key, x => x.Value);
        public static IEnumerable<KeyValuePair<TKey, TValue>> ForEachKVP<TKey, TValue>(this IEnumerable<KeyValuePair<TKey, TValue>> src, Action<TKey, TValue> action) =>
            src.ForEach(kvp => action(kvp.Key, kvp.Value));
        public static string JoinedKVP<TKey, TValue>(this IEnumerable<KeyValuePair<TKey, TValue>> src, Func<TKey, TValue, string> selector) =>
            src.SelectKVP(selector).Joined();
        public static string JoinedKVP<TKey, TValue>(this IEnumerable<KeyValuePair<TKey, TValue>> src, string delimiter, Func<TKey, TValue, string> selector) =>
            src.SelectKVP(selector).Joined(delimiter);
    }
}
