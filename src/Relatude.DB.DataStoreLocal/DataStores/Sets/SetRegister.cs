using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.Query.Expressions;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
namespace Relatude.DB.DataStores.Sets;

public class SetRegister(long maxSize) {
    bool _disabled = maxSize == 0;
    static int _aggregateCacheSize = 10000;
    private readonly SetCache _cache = new(maxSize);
    private readonly AggregateCache _aggregateCache = new(_aggregateCacheSize);
    static long _setStateId = 0;
    public void AddInfo(DataStoreInfo s) {
        s.SetCacheSize = _cache.Size;
        if (_cache.MaxSize > 0) s.SetCacheSizePercentage = 100d * _cache.Size / _cache.MaxSize;

        s.SetCacheHits = _cache.Hits;
        s.SetCacheMisses = _cache.Misses;
        s.SetCacheOverflows = _cache.Overflows;
        s.SetCacheCount = _cache.Count;

        s.AggregateCacheHits = _aggregateCache.Hits;
        s.AggregateCacheCount = _aggregateCache.Count;
        s.AggregateCacheMisses = _aggregateCache.Misses;
        s.AggregateCacheOverflows = _aggregateCache.Overflows;

    }
    static public long NewStateId() => Interlocked.Increment(ref _setStateId);
    public void InvalidateAll() {
        if (_disabled) return;
        _cache.ClearAll_NotSize0();
        _aggregateCache.ClearAll_NotSize0();
    }
    private IdSet createOrLookup(SetCacheKey key, Func<ICollection<int>> create) {
        ICollection<int>? collection;
        if (key.NotCachable || _disabled) {
            collection = create();
            return collection.Count == 0 ? IdSet.EmptyUncachable : IdSet.UncachableSet(collection);
        }
        if (_cache.TryGet(key, out IdSet? set)) {
#if DEBUG
            // COSTLY: checking if the cache is correct
            var testCacheValue = create();
            if (set.Count != testCacheValue.Count) throw new Exception("Cache error: Count mismatch. Different result second time...");

            var setValues = set.Enumerate().ToHashSet();
            var testValues = testCacheValue.ToHashSet();
            if (setValues.Count != testValues.Count) throw new Exception("Cache error: Unique values mismatch");

#endif
            return set;
        }
        collection = create();
        if (collection.Count == 0) {
            set = IdSet.Empty;
        } else if (collection.Count == 1) {
            set = IdSet.SingleIdSet(collection.First());
        } else {
            set = new IdSet(collection, NewStateId());
        }
        _cache.Set(key, set, set.MemSizeEstimate);
#if DEBUG
        //Console.WriteLine($"SetRegister: {key} added to cache");
#endif
        return set;
    }
    // memoization helpers for counts that are cheap to compute but repeated per query
    // (e.g. facet counts during page navigation over the same result set):
    public int CountCached(SetOperation op, long stateIdA, long stateIdB, object[]? values, Func<int> compute)
        => countOrLookup(new SetCacheKey(op, [stateIdA, stateIdB], values), compute);
    public bool TryGetCachedCount(SetOperation op, long stateIdA, long stateIdB, object[]? values, out int count) {
        var key = new SetCacheKey(op, [stateIdA, stateIdB], values);
        if (key.NotCachable || _disabled) { count = 0; return false; }
        return _aggregateCache.TryGet(key, out count);
    }
    public void SetCachedCount(SetOperation op, long stateIdA, long stateIdB, object[]? values, int count) {
        var key = new SetCacheKey(op, [stateIdA, stateIdB], values);
        if (key.NotCachable || _disabled) return;
        _aggregateCache.Set(key, count, 1);
    }
    private int countOrLookup(SetCacheKey key, Func<int> count) {
        if (key.NotCachable || _disabled) return count();
        if (_aggregateCache.TryGet(key, out int cnt)) return cnt;
        cnt = count();
        _aggregateCache.Set(key, cnt, 1);
        return cnt;
    }

    public IdSet Filter<T>(IValueIndex<T> index, IdSet nodeIds, IndexOperator op, T v) where T : notnull {
        // optimize later, intersect in index directly
        var matches = op switch {
            IndexOperator.Equal => this.WhereEqual(index, v),
            IndexOperator.NotEqual => this.WhereNotEqual(index, v),
            IndexOperator.Greater => this.WhereGreater(index, v),
            IndexOperator.Smaller => this.WhereLess(index, v),
            IndexOperator.GreaterOrEqual => this.WhereGreaterOrEqual(index, v),
            IndexOperator.SmallerOrEqual => this.WhereLessOrEqual(index, v),
            _ => throw new NotImplementedException(),
        };
        return this.Intersection(nodeIds, matches);
    }
    public IdSet FilterInValues<T>(IValueIndex<T> index, IdSet nodeIds, IEnumerable<T> selectedValues) where T : notnull {
        // optimize later, intersect in index directly
        var matches = this.WhereIn(index, selectedValues);
        return this.Intersection(nodeIds, matches);
    }
    public IdSet FilterRanges<T>(IValueIndex<T> index, IdSet nodeIds, List<Tuple<T, T>> selectedRanges) where T : notnull {
        // optimize later, intersect in index directly
        IdSet? matches = null; // ranges are inclusive at both ends, matching NativeKvValueIndex.FilterRanges
        foreach (var range in selectedRanges) {
            var rangeMatch = this.Intersection(this.WhereGreaterOrEqual(index, range.Item1), this.WhereLessOrEqual(index, range.Item2));
            matches = matches == null ? rangeMatch : this.Union(matches, rangeMatch);
        }
        if (matches == null) return IdSet.Empty;
        return this.Intersection(nodeIds, matches);
    }
    public IdSet FilterRangesObject<T>(IValueIndex<T> index, IdSet set, object from, object to) where T : notnull {
        return FilterRanges(index, set, [new Tuple<T, T>((T)from, (T)to)]);
    }

    // Generates a set with a single value, and caches it for future use.
    public IdSet SingleValueIdSet(int id) {
        var key = new SetCacheKey(SetOperation.SingleValueSet, [], [id]);
        return createOrLookup(key, () => {
            return [id];
        });
    }

    // a ∩ b
    public IdSet Intersection(IdSet a, IdSet b) {
        if (a.Count == 0 || b.Count == 0) return IdSet.Empty;
        if (b.Count == 1 && a.Has(b.First())) return b;
        if (a.Count == 1 && b.Has(a.First())) return a;
        var key = new SetCacheKey(SetOperation.Intersection, [a.StateId, b.StateId], null);
        return createOrLookup(key, () => {
            if (a.Bits is { } ba && b.Bits is { } bb) return DenseBitSet.And(ba, bb); // word-parallel
            var (small, big) = a.Count < b.Count ? (a, b) : (b, a);
            var result = new List<int>();
            foreach (var id in small.Enumerate()) if (big.Has(id)) result.Add(id);
            return result;
        });
    }

    // a ∩ b
    public int CountIntersection(IdSet a, IdSet b) {
        if (a.Count == 0 || b.Count == 0) return 0;
        var key = new SetCacheKey(SetOperation.CountIntersection, [a.StateId, b.StateId], null);
        return countOrLookup(key, () => IdSet.IntersectionCount(a, b));
    }

    // ⋃ sets
    public IdSet Union(List<IdSet> sets) {
        if (sets.Any(s => s.Count == 0)) sets = sets.Where(s => s.Count > 0).ToList();
        if (sets.Count == 0) return IdSet.Empty;
        if (sets.Count == 1) return sets[0];
        var stateIds = new long[sets.Count];
        for (var i = 0; i < sets.Count; i++) stateIds[i] = sets[i].StateId;
        var key = new SetCacheKey(SetOperation.Union, stateIds, null);
        return createOrLookup(key, () => {
            if (sets.All(s => s.Bits != null)) { // word-parallel
                var bits = DenseBitSet.Or(sets[0].Bits!, sets[1].Bits!);
                for (var i = 2; i < sets.Count; i++) bits = DenseBitSet.Or(bits, sets[i].Bits!);
                return bits;
            }
            var ids = new HashSet<int>();
            foreach (var set in sets) {
                foreach (var id in set.Enumerate()) ids.Add(id);
            }
            return ids;
        });
    }

    // a ∪ b
    /// <summary>
    /// a ∪ b 
    /// </summary>
    /// <param name="a"></param>
    /// <param name="b"></param>
    /// <returns></returns>
    public IdSet Union(IdSet a, IdSet b) {
        if (a.Count == 0) return b;
        if (b.Count == 0) return a;
        var key = new SetCacheKey(SetOperation.Union, [a.StateId, b.StateId], null);
        return createOrLookup(key, () => {
            if (a.Bits is { } ba && b.Bits is { } bb) return DenseBitSet.Or(ba, bb); // word-parallel
            var ids = new HashSet<int>(a.Enumerate());
            foreach (var id in b.Enumerate()) ids.Add(id);
            return ids;
        });
    }
    // a - b = { x ∈ a | x ∉ b }
    /// <summary>
    /// a - b = { x ∈ a | x ∉ b } all elements in a that are not in b
    /// </summary>
    /// <param name="a">first set</param>
    /// <param name="b">set subtracted from first set</param>
    /// <returns></returns>
    public IdSet Difference(IdSet a, IdSet b) { // subtract b from a
        if (a.Count == 0) return IdSet.Empty;
        if (b.Count == 0) return a;
        var key = new SetCacheKey(SetOperation.Difference, [a.StateId, b.StateId], null);
        return createOrLookup(key, () => {
            // safe only when a is itself a bit set: a's enumeration order is then already ascending
            if (a.Bits is { } ba && b.Bits is { } bb) return DenseBitSet.AndNot(ba, bb); // word-parallel
            var result = new List<int>();
            foreach (var v in a.Enumerate()) if (!b.Has(v)) result.Add(v);
            return result;
        });
    }
    // a Δ b = (a - b) ∪ (b - a), also known as symmetric difference
    public IdSet DisjunctiveUnion(IdSet a, IdSet b) { // not used yet
        if (a.Count == 0) return b;
        if (b.Count == 0) return a;
        var key = new SetCacheKey(SetOperation.DisjunctiveUnion, [a.StateId, b.StateId], null);
        return createOrLookup(key, () => {
            var result = new List<int>();
            foreach (var v in a.Enumerate()) if (!b.Has(v)) result.Add(v);
            foreach (var v in b.Enumerate()) if (!a.Has(v)) result.Add(v);
            return result;
        });
    }
    internal IdSet FilterByQueryContext(IdSet set, QueryContext queryOptions, Definition definition) {
        throw new NotImplementedException();
        //if (set.Count == 0) return set;
        //var contextKeys = QueryContext.Memberships.Cast<object>().ToArray(); // more context can be added here if needed
        //var key = new SetCacheKey(SetOperation.FilterByUserReadAccess, [set.StateId], contextKeys);
        //return createOrLookup(key, () => {
        //    var result = new List<int>();
        //    foreach (var id in set.Enumerate()) {

        //    }
        //    return result;
        //});
    }


    public IdSet Page(IdSet ids, int page, int pageSize) {
        var key = new SetCacheKey(SetOperation.Page, [ids.StateId], [page, pageSize]);
        return createOrLookup(key, () => {
            var result = new List<int>();
            // iterate to start:
            var enumerator = ids.Enumerate().GetEnumerator();
            for (var i = 0; i < page * pageSize; i++) {
                if (!enumerator.MoveNext()) break;
            }
            // iterate to end:
            for (var i = 0; i < pageSize; i++) {
                if (!enumerator.MoveNext()) break;
                result.Add(enumerator.Current);
            }
            return result;
        });
    }
    public IdSet Take(IdSet ids, int take) {
        var key = new SetCacheKey(SetOperation.Take, [ids.StateId], [take]);
        return createOrLookup(key, () => {
            var result = new List<int>();
            // iterate to start:
            var enumerator = ids.Enumerate().GetEnumerator();
            // iterate to end:
            for (var i = 0; i < take; i++) {
                if (!enumerator.MoveNext()) break;
                result.Add(enumerator.Current);
            }
            return result;
        });
    }
    public IdSet Skip(IdSet ids, int skip) {
        var key = new SetCacheKey(SetOperation.Skip, [ids.StateId], [skip]);
        return createOrLookup(key, () => {
            var result = new List<int>();
            // iterate to start:
            var enumerator = ids.Enumerate().GetEnumerator();
            for (var i = 0; i < skip; i++) {
                if (!enumerator.MoveNext()) break;
            }
            // iterate to end:
            while (enumerator.MoveNext()) {
                result.Add(enumerator.Current);
            }
            return result;
        });
    }
    public IdSet SearchForIdSetUnranked(long sourceStateKey, TermSet value, bool orSearch, Func<ICollection<int>> search) {
        var key = new SetCacheKey(SetOperation.Search, [sourceStateKey], [value.ToString(), orSearch]);
        return createOrLookup(key, () => search());
    }
    public IdSet SearchSemantic(long sourceStateKey, string value, float defaultMinimumVectorSimilarity, Func<ICollection<int>> search) {
        var key = new SetCacheKey(SetOperation.SearchSemantic, [sourceStateKey], [value, defaultMinimumVectorSimilarity]);
        return createOrLookup(key, () => search());
    }

    public IdSet WhereTypes(IdSet set, IdSet[] allTypeSets) {
        var stateIds = new long[allTypeSets.Length + 1];
        stateIds[0] = set.StateId;
        var i = 1;
        foreach (var typeSet in allTypeSets) stateIds[i++] = typeSet.StateId;
        var key = new SetCacheKey(SetOperation.WhereTypes, stateIds, null);
        return createOrLookup(key, () => IdSet.CollectUnique(whereTypes(set, allTypeSets)));
        static IEnumerable<int> whereTypes(IdSet set, IdSet[] allTypeSets) {
            foreach (var id in set.Enumerate()) {
                foreach (var typeSet in allTypeSets) {
                    if (typeSet.Has(id)) {
                        yield return id;
                        break;
                    }
                }
            }
        }
    }
    internal IdSet WhereHasRelation(IdSet set, bool[] directions, Relation[] relations, int to, RelQuestion method) {
        var stateIds = new long[relations.Length + 1];
        for (var i = 0; i < relations.Length; i++) stateIds[i] = relations[i].GeneralStateId;
        stateIds[relations.Length] = set.StateId;
        var valueKeys = new object[2 + directions.Length];
        valueKeys[0] = to;
        valueKeys[1] = (int)method;
        for (var i = 0; i < directions.Length; i++) valueKeys[i + 2] = directions[i];
        var key = new SetCacheKey(SetOperation.WhereHasRelation, stateIds, valueKeys);
        return createOrLookup(key, () => {
            if (method != RelQuestion.Relates) throw new NotSupportedException();
            var result = new List<int>(); // each set id is tested once, so the list is distinct
            foreach (var id in set.Enumerate()) {
                if (containsRelation(0, id, directions, relations, to)) result.Add(id);
            }
            return result;
        });
    }
    bool containsRelation(int level, int from, bool[] directions, Relation[] relations, int to) {
        if (directions.Length - level == 1) // last level
            return relations[level].Contains(from, to, directions[level]);
        foreach (var target in relations[level].GetRelated(from, directions[level]).Enumerate()) { // recursive
            if (containsRelation(level + 1, target, directions, relations, to)) return true;
        }
        return false;
    }
    public IdSet WhereEqualId(IdSet a, int id) {
        var key = new SetCacheKey(SetOperation.WhereEqualId, [a.StateId], [id]);
        return createOrLookup(key, () => {
            if (a.Has(id)) return new SingleValueSet(id);
            return EmptySet.Instance;
        });
    }
    public IdSet WhereNotEqualId(IdSet a, int id) {
        var key = new SetCacheKey(SetOperation.WhereNotEqualId, [a.StateId], [id]);
        return createOrLookup(key, () => {
            if (a.Count == 0) return EmptySet.Instance;
            var newSet = new List<int>(a.Count); // a is a set, so removing one id keeps it distinct
            foreach (var v in a.Enumerate()) if (v != id) newSet.Add(v);
            return newSet;
        });
    }
    internal IdSet WhereEqual<T>(IValueIndex<T> index, T value) where T : notnull {
        var key = new SetCacheKey(SetOperation.WhereEqual, [index.StateId], index.GetCacheKey(value, QueryType.Equal));
        return createOrLookup(key, () => index.GetIds(value));
    }
    internal IdSet WhereIn<T>(IValueIndex<T> index, IEnumerable<T> values) where T : notnull {
        var valueKeys = values.SelectMany(value => index.GetCacheKey(value, QueryType.Equal)).ToArray();
        var key = new SetCacheKey(SetOperation.WhereIn, [index.StateId], valueKeys);
        // Distinct() because the caller may pass the same value twice; per-value id sets are
        // disjoint (one value per id in a value index), so the combined ids are then unique
        return createOrLookup(key, () => IdSet.CollectUnique(values.Distinct().SelectMany(index.GetIds)));
    }
    internal IdSet WhereNotEqual<T>(IValueIndex<T> index, T value) where T : notnull {
        var key = new SetCacheKey(SetOperation.WhereNotEqual, [index.StateId], index.GetCacheKey(value, QueryType.NotEqual));
        return createOrLookup(key, () => {
            var exclude = index.GetIds(value);
            // membership test must be O(1): small backing collections may be list based, so copy
            // those to a hash set once (cheap, they are small) instead of hashing ALL index ids
            ICollection<int> excludeFast;
            if (exclude is MutableSet ms && ms.TryGetBits(out var bits)) excludeFast = bits;
            else if (exclude.Count > 16) excludeFast = new HashSet<int>(exclude);
            else excludeFast = exclude;
            return IdSet.CollectUnique(whereNot(index.Ids, excludeFast));
            static IEnumerable<int> whereNot(IEnumerable<int> ids, ICollection<int> exclude) {
                foreach (var id in ids) if (!exclude.Contains(id)) yield return id;
            }
        });
    }
    internal IdSet WhereGreaterOrEqual<T>(IValueIndex<T> index, T value) where T : notnull {
        var key = new SetCacheKey(SetOperation.WhereGreaterOrEqual, [index.StateId], index.GetCacheKey(value, QueryType.GreaterOrEqual));
        return createOrLookup(key, () => IdSet.CollectUnique(index.GreaterThan(value, true)));
    }
    internal IdSet WhereGreater<T>(IValueIndex<T> index, T value) where T : notnull {
        var key = new SetCacheKey(SetOperation.WhereGreater, [index.StateId], index.GetCacheKey(value, QueryType.Greater));
        return createOrLookup(key, () => IdSet.CollectUnique(index.GreaterThan(value, false)));
    }
    internal IdSet WhereLessOrEqual<T>(IValueIndex<T> index, T value) where T : notnull {
        var key = new SetCacheKey(SetOperation.WhereLessOrEqual, [index.StateId], index.GetCacheKey(value, QueryType.LessOrEqual));
        return createOrLookup(key, () => IdSet.CollectUnique(index.LessThan(value, true)));
    }
    internal IdSet WhereLess<T>(IValueIndex<T> index, T value) where T : notnull {
        var key = new SetCacheKey(SetOperation.WhereLess, [index.StateId], index.GetCacheKey(value, QueryType.Less));
        return createOrLookup(key, () => IdSet.CollectUnique(index.LessThan(value, false)));
    }

    // all ids whose value is within the range (used by CountInRange, i.e. range facets)
    internal IdSet WhereValueInRange<T>(IValueIndex<T> index, T from, T to, bool fromInclusive, bool toInclusive) where T : notnull {
        var key = new SetCacheKey(SetOperation.WhereInRange, [index.StateId], [
            .. index.GetCacheKey(from, fromInclusive ? QueryType.GreaterOrEqual : QueryType.Greater),
            .. index.GetCacheKey(to, toInclusive ? QueryType.LessOrEqual : QueryType.Less),
            fromInclusive,
            toInclusive
                ]);
        return createOrLookup(key, () => IdSet.CollectUnique(index.RangeSearch(from, to, fromInclusive, toInclusive)));
    }
    internal IdSet WhereRangeOverlapsRange<T>(IValueIndex<T> indexFrom, IValueIndex<T> indexTo, T from, T to, bool fromInclusive, bool toInclusive) where T : notnull {
        var key = new SetCacheKey(SetOperation.WherePartOfRange, [indexFrom.StateId, indexTo.StateId], [
            .. indexFrom.GetCacheKey(from, fromInclusive ? QueryType.GreaterOrEqual : QueryType.Greater),
            .. indexTo.GetCacheKey(from, fromInclusive ? QueryType.GreaterOrEqual : QueryType.Greater),
            .. indexFrom.GetCacheKey(to, toInclusive ? QueryType.LessOrEqual : QueryType.Less),
            .. indexTo.GetCacheKey(to, toInclusive ? QueryType.LessOrEqual : QueryType.Less),
            fromInclusive,
            toInclusive]);
        // each id holds exactly one (from, to) range, so the overlap enumeration is distinct
        return createOrLookup(key, () => IdSet.CollectUnique(indexFrom.WhereRangeOverlapsRange(indexTo, from, to, fromInclusive, toInclusive)));
    }

    public IdSet OrderBy<T>(IValueIndex<T> index, IdSet unsorted, bool descending) where T : notnull {
        var key = new SetCacheKey(descending ? SetOperation.OrderByDescending : SetOperation.OrderByAscending, [index.StateId, unsorted.StateId], null);
        return createOrLookup(key, () => {
            IEnumerable<int> sorted;
            if (descending) sorted = unsorted.Enumerate().OrderByDescending(id => index.GetValue(id));
            else sorted = unsorted.Enumerate().OrderBy(id => index.GetValue(id));
            return new FixedOrderedSet(sorted, unsorted.Count);
        });
    }
    public int CountEqual<T>(IValueIndex<T> index, IdSet nodeIds, T v) where T : notnull {
        var key = new SetCacheKey(SetOperation.CountEqual, [index.StateId, nodeIds.StateId], index.GetCacheKey(v, QueryType.Equal));
        // counting against the cached value set intersects the smaller side over in-memory ids;
        // enumerating index.GetIds(v) directly would re-read every id of the value from the
        // index for every set the count cache has not seen (e.g. every new search string)
        return countOrLookup(key, () => IdSet.IntersectionCount(this.WhereEqual(index, v), nodeIds));
    }
    public int CountInRange<T>(IValueIndex<T> index, IdSet nodeIds, T from, T to, bool fromInclusive, bool toInclusive) where T : notnull {
        if (nodeIds.Count == 0) return 0;
        var key = new SetCacheKey(SetOperation.CountInRange, [index.StateId, nodeIds.StateId], [
            .. index.GetCacheKey(from, fromInclusive ? QueryType.GreaterOrEqual : QueryType.Greater),
            .. index.GetCacheKey(to, toInclusive ? QueryType.LessOrEqual : QueryType.Less),
            fromInclusive,
            toInclusive]);
        // same reasoning as CountEqual: the range's id set is cached per index state, so only the
        // first count of a given range pays for reading it from the index
        return countOrLookup(key, () => IdSet.IntersectionCount(this.WhereValueInRange(index, from, to, fromInclusive, toInclusive), nodeIds));
    }

    internal void ClearCache() {
        _cache.ClearAll_NotSize0();
        _aggregateCache.ClearAll_NotSize0();
    }
    internal void HalfCacheSize() {
        _cache.HalfSize();
        _aggregateCache.HalfSize();
    }
    internal long CacheSize => _cache.Size + _aggregateCache.Size;
    internal int CacheCount => _cache.Count + _aggregateCache.Count;
}
