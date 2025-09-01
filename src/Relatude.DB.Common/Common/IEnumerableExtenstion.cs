namespace Relatude.DB.Common;
public static class IEnumerableExtenstion {
    public static void ForEach<T>(this IEnumerable<T> enumeration, Action<T> action) {
        foreach (T item in enumeration) {
            action(item);
        }
    }
}
