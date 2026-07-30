using System.Diagnostics.CodeAnalysis;
using System.Collections;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Indexes;
/// <summary>
/// This is a special purpose index used by the ValueIndex class.
/// It indexes ids by value.
/// The "MutableSet" is a set designed to be used for fast creations of IdSets
/// Optimized for fast lookups and insertions/removals. 
/// However, it is not so efficient with range queries, as it needs a resort after each insertion/removal.
/// Optimization is possible using a btree or similar structure ( even though that reduces insertion/removal speed. ).
/// </summary>
/// <typeparam name="T"></typeparam>
internal class IdByValue<T>(SetRegister sets) where T : notnull {

    // the same comparer as the owning ValueIndex (ordinal for strings), used by every sort and
    // binary search over the sorted values so they always agree
    static readonly IComparer<T> _comparer = ValueIndex<T>.comparer;
    int _idCount = 0;
    int _maxId = 0; // upper window bound for bit set results, never shrinks
    // the values are split into to dictionaries to save memory.
    // for values with only one id, is stored in _idByValue
    // for values with multiple ids, is stored in _idsByValue
    readonly Dictionary<T, int> _idByValue = [];
    readonly Dictionary<T, MutableSet> _idsByValue = [];
    List<T>? _sortedValues;
    int[]? _cumCounts; // _cumCounts[i] = total ids for _sortedValues[0..i], built and invalidated together with _sortedValues
    List<int>? _sortedIds; // sorted list of all ids, sorted by their values, and reset on every change, and lazily re-created on request ( somewhat expensive operation )
    readonly SetRegister _sets = sets;
    public void Index(T value, int id) {
        if (_idByValue.TryGetValue(value, out var existingId)) {
            _idByValue.Remove(value);
            _idsByValue.Add(value, new(existingId, id));
        } else if (_idsByValue.TryGetValue(value, out var idList)) {
            idList.Add(id);
        } else {
            _idByValue.Add(value, id);
        }
        if (id > _maxId) _maxId = id;
        _idCount++;
        _sortedIds = null;
        _sortedValues = null;
        _cumCounts = null;
    }
    public void DeIndex(T value, int id) {
        if (_idsByValue.TryGetValue(value, out var idList)) {
            idList.Remove(id);
            if (idList.Count == 1) {
                _idsByValue.Remove(value);
                _idByValue.Add(value, idList.Single());
            }
        } else {
            _idByValue.Remove(value);
        }
        _idCount--;
        _sortedIds = null;
        _sortedValues = null;
        _cumCounts = null;
    }
    public bool ContainsValue(T value) => _idByValue.ContainsKey(value) || _idsByValue.ContainsKey(value);
    public bool TryGetValue(T value, [MaybeNullWhen(false)] out ICollection<int> ids) {
        if (_idByValue.TryGetValue(value, out var id)) {
            ids = new SingleValueSet(id);
            return true;
        }
        if (_idsByValue.TryGetValue(value, out var idList)) {
            ids = idList;
            return true;
        }
        ids = null;
        return false;
    }
    public bool TryGetValueIdSet(T value, [MaybeNullWhen(false)] out IdSet set) {
        if (_idByValue.TryGetValue(value, out var id)) {
            set = IdSet.SingleIdSet(id);
            return true;
        }
        if (_idsByValue.TryGetValue(value, out var idList)) {
            set = idList.AsUnmutableIdSet();
            return true;
        }
        set = null;
        return false;
    }
    public IEnumerable<T> Values {
        get {
            foreach (var value in _idByValue.Keys) yield return value;
            foreach (var value in _idsByValue.Keys) yield return value;
        }
    }
    public int ValueCount => _idByValue.Count + _idsByValue.Count;
    public List<T> GetSortedValues() {
        ensureSortedValues();
        return _sortedValues!;
    }
    public List<int> GetIdsSortedByValue() {
        ensureIdsSortedByValues();
        return _sortedIds!;
    }
    public IEnumerable<int> AscendingIds() {
        ensureIdsSortedByValues();
        return _sortedIds!;
    }
    public IEnumerable<int> DescendingIds() {
        ensureIdsSortedByValues();
        for (int i = _sortedIds!.Count - 1; i >= 0; i--) yield return _sortedIds[i];
    }
    public IEnumerable<T> AscendingValues() {
        ensureSortedValues();
        return _sortedValues!;
    }
    public IEnumerable<T> DescendingValues() {
        ensureSortedValues();
        for (int i = _sortedValues!.Count - 1; i >= 0; i--) yield return _sortedValues[i];
    }
    object _sortLock = new();
    void ensureIdsSortedByValues() {
        lock (_sortLock) {
            ensureSortedValues();
            if (_sortedIds == null) {
                _sortedIds = new(_idCount);
                foreach (var v in _sortedValues!) {
                    if (TryGetValue(v, out var set)) {
                        _sortedIds.AddRange(set);
                    } else {
                        throw new Exception("Integrity problems with index. ");
                    }
                }
            }
        }
    }
    void ensureSortedValues() {
        lock (_sortLock) {
            if (_sortedValues == null) {
                var sorted = Values.ToList();
                sorted.Sort(_comparer);
                var cum = new int[sorted.Count];
                var running = 0;
                for (int i = 0; i < sorted.Count; i++) {
                    running += countOf(sorted[i]);
                    cum[i] = running;
                }
                _cumCounts = cum;
                _sortedValues = sorted;
            }
        }
    }
    // id count for a value without allocating (TryGetValue wraps single ids in a SingleValueSet)
    int countOf(T value) {
        if (_idByValue.ContainsKey(value)) return 1;
        if (_idsByValue.TryGetValue(value, out var set)) return set.Count;
        return 0;
    }
    public int RangeCount(T from, T to, bool fromInclusive, bool toInclusive) {
        if (ValueCount == 0) return 0;
        ensureSortedValues();
        var sorted = _sortedValues!;
        int lower = sorted.BinarySearch(from, _comparer);
        lower = lower < 0 ? ~lower : fromInclusive ? lower : lower + 1;
        int upper = sorted.BinarySearch(to, _comparer);
        upper = upper < 0 ? ~upper - 1 : toInclusive ? upper : upper - 1;
        if (lower > upper || upper < 0 || lower >= sorted.Count) return 0;
        var cum = _cumCounts!;
        return cum[upper] - (lower > 0 ? cum[lower - 1] : 0);
    }
    public int InSetRangeCount(IdSet ids, T from, T to, bool fromInclusive, bool toInclusive) {
        if (ValueCount == 0) return 0;
        ensureSortedValues();
        var count = 0;
        foreach (var value in rangeSearch(_sortedValues!, from, to, fromInclusive, toInclusive)) {
            count += CountIntersection(value, ids);
        }
        return count;
    }

    public int CountGreaterThan(T from, bool inclusive) {
        if (ValueCount == 0) return 0;
        ensureSortedValues();
        var sorted = _sortedValues!;
        int index = sorted.BinarySearch(from, _comparer);
        index = index < 0 ? ~index : inclusive ? index : index + 1;
        if (index >= sorted.Count) return 0;
        var cum = _cumCounts!;
        return cum[^1] - (index > 0 ? cum[index - 1] : 0);
    }
    public int CountEqual(T value) => countOf(value);
    // |{ids with value} ∩ other| without materializing anything
    public int CountIntersection(T value, IdSet other) {
        if (_idByValue.TryGetValue(value, out var id)) return other.Has(id) ? 1 : 0;
        if (_idsByValue.TryGetValue(value, out var idList)) return idList.CountIntersection(other);
        return 0;
    }
    public int CountLessThan(T to, bool inclusive) {
        if (ValueCount == 0) return 0;
        ensureSortedValues();
        var sorted = _sortedValues!;
        int index = sorted.BinarySearch(to, _comparer);
        index = index < 0 ? ~index - 1 : inclusive ? index : index - 1;
        if (index < 0) return 0;
        return _cumCounts![index];
    }
    public IEnumerable<int> GreaterThan(T from, bool inclusive) {
        if (ValueCount == 0) yield break;
        ensureSortedValues();
        foreach (var value in greaterThan(_sortedValues!, from, inclusive)) {
            if (TryGetValue(value, out var set)) {
                foreach (var id in set) yield return id;
            } else {
                throw new Exception("Integrity problems with index. ");
            }
        }
    }
    public IEnumerable<int> LessThan(T to, bool inclusive) {
        if (ValueCount == 0) yield break;
        ensureSortedValues();
        foreach (var value in lessThan(_sortedValues!, to, inclusive)) {
            if (TryGetValue(value, out var set)) {
                foreach (var id in set) yield return id;
            } else {
                throw new Exception("Integrity problems with index. ");
            }
        }
    }
    public IEnumerable<int> RangeSearch(T from, T to, bool fromInclusive, bool toInclusive) {
        if (ValueCount == 0) yield break;
        ensureSortedValues();
        foreach (var value in rangeSearch(_sortedValues!, from, to, fromInclusive, toInclusive)) {
            if (TryGetValue(value, out var set)) {
                foreach (var id in set) yield return id;
            } else {
                throw new Exception("Integrity problems with index. ");
            }
        }
    }
    // one-allocation set construction: counts first (set counts, no id enumeration), then fills
    // either a bit set (word-parallel for bit backed value sets) or an exactly sized list.
    // Replaces the id-at-a-time yield chains + CollectUnique's list-then-bitset double pass.
    public ICollection<int> CollectGreaterThan(T from, bool inclusive) {
        if (ValueCount == 0) return Array.Empty<int>();
        ensureSortedValues();
        return collect(greaterThan(_sortedValues!, from, inclusive));
    }
    public ICollection<int> CollectLessThan(T to, bool inclusive) {
        if (ValueCount == 0) return Array.Empty<int>();
        ensureSortedValues();
        return collect(lessThan(_sortedValues!, to, inclusive));
    }
    public ICollection<int> CollectRangeSearch(T from, T to, bool fromInclusive, bool toInclusive) {
        if (ValueCount == 0) return Array.Empty<int>();
        ensureSortedValues();
        return collect(rangeSearch(_sortedValues!, from, to, fromInclusive, toInclusive));
    }
    public ICollection<int> CollectValues(IEnumerable<T> distinctValues) {
        if (ValueCount == 0) return Array.Empty<int>();
        return collect(distinctValues);
    }
    public ICollection<int> CollectNotEqualValue(T value) {
        if (ValueCount == 0) return Array.Empty<int>();
        return collect(notEqual(value));
        IEnumerable<T> notEqual(T v) {
            var eq = EqualityComparer<T>.Default;
            foreach (var k in _idByValue.Keys) if (!eq.Equals(k, v)) yield return k;
            foreach (var k in _idsByValue.Keys) if (!eq.Equals(k, v)) yield return k;
        }
    }
    ICollection<int> collect(IEnumerable<T> values) {
        // one probe sweep resolving each value to its ids, then a fill from the resolved refs
        // (a second dictionary sweep costs more than these two lists at high cardinality)
        var singles = new List<int>();
        var sets = new List<MutableSet>();
        long total = 0;
        foreach (var v in values) {
            if (_idByValue.TryGetValue(v, out var id)) { singles.Add(id); total++; }
            else if (_idsByValue.TryGetValue(v, out var s)) { sets.Add(s); total += s.Count; }
        }
        if (total == 0) return Array.Empty<int>();
        if (DenseBitSet.WorthIt((int)total, 0, _maxId)) {
            var bits = new DenseBitSet(0, _maxId);
            foreach (var id in singles) bits.Add(id);
            foreach (var set in sets) set.OrInto(bits);
            return bits;
        }
        var result = new List<int>((int)total);
        result.AddRange(singles);
        foreach (var set in sets) result.AddRange(set);
        return result;
    }
    static IEnumerable<T> greaterThan(List<T> sortedList, T from, bool inclusive) {
        int index = sortedList.BinarySearch(from, _comparer);
        if (index < 0) index = ~index;
        else if (!inclusive) index++;
        if (index < 0 || index >= sortedList.Count) yield break;
        for (int i = index; i < sortedList.Count; i++) yield return sortedList[i];
    }
    static IEnumerable<T> lessThan(List<T> sortedList, T to, bool inclusive) {
        int index = sortedList.BinarySearch(to, _comparer);
        if (index < 0) index = ~index - 1;
        else if (!inclusive) index--;
        if (index < 0 || index >= sortedList.Count) yield break;
        for (int i = index; i >= 0; i--) yield return sortedList[i];
    }
    static IEnumerable<T> rangeSearch(List<T> sortedList, T from, T to, bool fromInclusive, bool toInclusive) {
        int lower = sortedList.BinarySearch(from!, _comparer);
        lower = lower < 0 ? ~lower : fromInclusive ? lower : lower + 1;
        int upper = sortedList.BinarySearch(to!, _comparer);
        upper = upper < 0 ? ~upper - 1 : toInclusive ? upper : upper - 1;
        if (lower < 0 || lower >= sortedList.Count || upper < 0 || upper >= sortedList.Count) yield break;
        for (int i = lower; i <= upper; i++) yield return sortedList[i];
    }
}
