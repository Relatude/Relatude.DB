using Relatude.DB.Query.ExpressionToString.ZSpitz.Extensions;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Relatude.DB.Query.ExpressionToString.ZSpitz.Extensions {
    public static class IEnumerableTExtensions {
        public static bool None<T>(this IEnumerable<T> src, Func<T, bool>? predicate = null) =>
            predicate is null ?
                !src.Any() :
                !src.Any(predicate);

        public static IEnumerable<T> ForEach<T>(this IEnumerable<T> src, Action<T> action) {
            foreach (var item in src) {
                action(item);
            }
            return src;
        }
        public static IEnumerable<T> ForEach<T>(this IEnumerable<T> src, Action<T, int> action) {
            var current = 0;
            foreach (var item in src) {
                action(item, current);
                current += 1;
            }
            return src;
        }

        public static string Joined<T>(this IEnumerable<T> source, string delimiter = ",", Func<T, string>? selector = null) =>
            source is null ? "" :
            selector is null ? string.Join(delimiter, source) :
            string.Join(delimiter, source.Select(selector));

        public static string Joined<T>(this IEnumerable<T> source, string delimiter, Func<T, int, string> selector) =>
            source is null ? "" :
            selector is null ? string.Join(delimiter, source) :
            string.Join(delimiter, source.Select(selector));

        public static IEnumerable<(TFirst, TSecond)> ZipT<TFirst, TSecond>(this IEnumerable<TFirst> first, IEnumerable<TSecond> second) => first.Zip(second, (x, y) => (x, y));
        public static IEnumerable<(T, int)> WithIndex<T>(this IEnumerable<T> src) => src.Select((x, index) => (x, index));

        public static IEnumerable<T> Ordered<T>(this IEnumerable<T> src) => src.OrderBy(x => x);

        public static void AddRangeTo<T>(this IEnumerable<T> src, ICollection<T> dest) => dest.AddRange(src);

        /// <summary>
        /// Returns an element If the sequence has exactly one element; otherwise returns the default of T
        /// (unlike the standard SingleOrDefault, which will throw an exception on multiple elements).
        /// </summary>
        [return: MaybeNull]
        public static T SingleOrDefaultExt<T>(this IEnumerable<T> src, Func<T, bool>? predicate = null) {
            if (src == null) { return default!; }
            if (predicate != null) { src = src.Where(predicate); }
            T ret = default!;
            var counter = 0;
            foreach (var item in src.Take(2)) {
                if (counter == 1) { return default!; }
                ret = item;
                counter += 1;
            }
            return ret;
        }

        public static HashSet<T> ToHashSet<T>(this IEnumerable<T> src, IEqualityComparer<T>? comparer = null) => new(src, comparer);

        public static IEnumerable<T> SelectMany<T>(this IEnumerable<IEnumerable<T>> src) => src.SelectMany(x => x);

        public static ReadOnlyCollection<T> ToReadOnlyCollection<T>(this IEnumerable<T> src) => new(src.ToList());

        // https://stackoverflow.com/a/18304070
        public static bool IsUnique<T>(this IEnumerable<T> src) {
            var hs = new HashSet<T>();
            return src.All(hs.Add);
        }

        public static IEnumerable<T> Select<T>(this IEnumerable<T> src) => src.Select(x => x);

        // https://stackoverflow.com/a/27097569
        public static T? Unanimous<T>(this IEnumerable<T> src, T? other = default) {
            var initialized = false;
            T? first = default;
            foreach (var item in src) {
                if (!initialized) {
                    first = item;
                    initialized = true;
                } else if (!EqualityComparer<T>.Default.Equals(first!, item)) {
                    return other;
                }
            }
            return initialized ? first : other;
        }

        public static IEnumerable<T> ConcatOne<T>(this IEnumerable<T> src, T element) => src.Concat(Enumerable.Repeat(element, 1));
    }
}
