using System;
using System.Collections.Generic;
using System.Linq;
using Relatude.DB.Query.ExpressionToString.ZSpitz.Extensions;
#if !NET452
using System.Collections.Immutable;
#endif

namespace Relatude.DB.Query.ExpressionToString.ZSpitz.Extensions {
    public static class IEnumerableTupleExtensions {
        public static void AddRangeTo<T1, T2>(this IEnumerable<(T1, T2)> src, IDictionary<T1, T2> dict) where T1 : notnull => dict.AddRange(src);
        [Obsolete("Use foreach and tuple deconstruction")] public static IEnumerable<(T1, T2)> ForEachT<T1, T2>(this IEnumerable<(T1, T2)> src, Action<T1, T2> action) => src.ForEach(x => action(x.Item1, x.Item2));
        [Obsolete("Use WithIndex, foreach and tuple deconstruction")] public static IEnumerable<(T1, T2)> ForEachT<T1, T2>(this IEnumerable<(T1, T2)> src, Action<T1, T2, int> action) => src.ForEach((x, index) => action(x.Item1, x.Item2, index));
        [Obsolete("Use foreach and tuple deconstruction")] public static IEnumerable<(T1, T2, T3)> ForEachT<T1, T2, T3>(this IEnumerable<(T1, T2, T3)> src, Action<T1, T2, T3> action) => src.ForEach(x => action(x.Item1, x.Item2, x.Item3));
        [Obsolete("Use foreach and tuple deconstruction")] public static IEnumerable<(T1, T2, T3, T4)> ForEachT<T1, T2, T3, T4>(this IEnumerable<(T1, T2, T3, T4)> src, Action<T1, T2, T3, T4> action) => src.ForEach(x => action(x.Item1, x.Item2, x.Item3, x.Item4));

        public static string JoinedT<T1, T2>(this IEnumerable<(T1, T2)> src, string delimiter, Func<T1, T2, string> selector) =>
            src.Joined(delimiter, x => selector(x.Item1, x.Item2));
        public static string JoinedT<T1, T2>(this IEnumerable<(T1, T2)> src, string delimiter, Func<T1, T2, int, string> selector) =>
            src.Joined(delimiter, (x, index) => selector(x.Item1, x.Item2, index));
        public static string JoinedT<T1, T2, T3, T4>(this IEnumerable<(T1, T2, T3, T4)> src, string delimiter, Func<T1, T2, T3, T4, string> selector) =>
            src.Joined(delimiter, x => selector(x.Item1, x.Item2, x.Item3, x.Item4));


        public static IEnumerable<T2> Item2s<T1, T2>(this IEnumerable<(T1, T2)> src) => src.Select(x => x.Item2);
        public static IEnumerable<TResult> SelectT<T1, T2, TResult>(this IEnumerable<(T1, T2)> src, Func<T1, T2, TResult> selector) =>
            src.Select(x => selector(x.Item1, x.Item2));
        public static IEnumerable<TResult> SelectT<T1, T2, T3, TResult>(this IEnumerable<(T1, T2, T3)> src, Func<T1, T2, T3, TResult> selector) =>
            src.Select(x => selector(x.Item1, x.Item2, x.Item3));
        public static IEnumerable<TResult> SelectT<T1, T2, T3, T4, TResult>(this IEnumerable<(T1, T2, T3, T4)> src, Func<T1, T2, T3, T4, TResult> selector) =>
            src.Select(x => selector(x.Item1, x.Item2, x.Item3, x.Item4));
        public static IEnumerable<TResult> SelectManyT<T1, T2, T3, T4, TResult>(this IEnumerable<(T1, T2, T3, T4)> src, Func<T1, T2, T3, T4, IEnumerable<TResult>> selector) =>
            src.SelectMany(x => selector(x.Item1, x.Item2, x.Item3, x.Item4));
        public static IEnumerable<(T1, T2)> WhereT<T1, T2>(this IEnumerable<(T1, T2)> src, Func<T1, T2, bool> predicate) => src.Where(x => predicate(x.Item1, x.Item2));
        public static bool AllT<T1, T2>(this IEnumerable<(T1, T2)> src, Func<T1, T2, bool> predicate) => src.All(x => predicate(x.Item1, x.Item2));

        public static IEnumerable<(T1, T2, int)> WithIndex<T1, T2>(this IEnumerable<(T1, T2)> src) => src.Select((x, index) => (x.Item1, x.Item2, index));
        public static Dictionary<TKey, TValue> ToDictionary<TKey, TValue>(this IEnumerable<(TKey, TValue)> src) where TKey : notnull => src.ToDictionary(t => t.Item1, t => t.Item2);

#if !NET452
        public static ImmutableDictionary<TKey, TValue> ToImmutableDictionary<TKey, TValue>(this IEnumerable<(TKey, TValue)> src) where TKey : notnull => src.ToImmutableDictionary(t => t.Item1, t => t.Item2);
#endif
    }
}
