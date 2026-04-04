namespace Relatude.DB.Common;
public static class IEnumerableExtenstion {
    /// <summary>
    /// Performs the specified action on each element of the <see cref="IEnumerable{T}"/>.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="enumeration"></param>
    /// <param name="action"></param>
    public static void ForEach<T>(this IEnumerable<T> enumeration, Action<T> action) {
        foreach (T item in enumeration) {
            action(item);
        }
    }
}
