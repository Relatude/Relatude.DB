namespace WAF.Query.ExpressionToString.ZSpitz.Extensions;
public static class IGroupingTKeyTElementExtensions {
    public static Dictionary<TKey, List<TElement>> ToDictionaryList<TKey, TElement>(IEnumerable<IGrouping<TKey, TElement>> groups) where TKey : notnull =>
        groups.ToDictionary(group => group.Key, group => group.ToList());
}
