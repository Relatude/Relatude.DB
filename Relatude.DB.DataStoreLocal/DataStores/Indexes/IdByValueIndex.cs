using System.Diagnostics.CodeAnalysis;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Indexes;
/// <summary>
/// This is a special purpose index used by the ValueIndex class.
/// It indexes ids by value.
/// The "MutableSet" is a set designed to be used for fast creations of IdSets
/// </summary>
/// <typeparam name="T"></typeparam>
internal class IdByValue<T>(SetRegister sets) where T : notnull {

    int _idCount = 0;
    // the values are split into to dictionaries to save memory. 
    // for values with only one id, is stored in _idByValue
    // for values with multiple ids, is stored in _idsByValue
    readonly Dictionary<T, int> _idByValue = [];
    readonly Dictionary<T, MutableSet> _idsByValue = [];
    List<T>? _sortedValues;
    List<int>? _sortedIds;
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
        _idCount++;
        _sortedIds = null;
        _sortedValues = null;
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
    object _lock = new();
    void ensureIdsSortedByValues() {
        lock (_lock) {
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
        lock (_lock) {
            if (_sortedValues == null) {
                _sortedValues = Values.ToList();
                _sortedValues.Sort();
            }
        }
    }
    public int RangeCount(T from, T to, bool fromInclusive, bool toInclusive) {
        if (ValueCount == 0) return 0;
        ensureSortedValues();
        var count = 0;
        foreach (var value in rangeSearch(_sortedValues!, from, to, fromInclusive, toInclusive)) {
            if (TryGetValue(value, out var set)) {
                count += set.Count;
            } else {
                throw new Exception("Integrity problems with index. ");
            }
        }
        return count;
    }
    public int InSetRangeCount(IdSet ids, T from, T to, bool fromInclusive, bool toInclusive) {
        if (ValueCount == 0) return 0;
        ensureSortedValues();
        var count = 0;
        foreach (var value in rangeSearch(_sortedValues!, from, to, fromInclusive, toInclusive)) {
            if (TryGetValueIdSet(value, out var set)) {
                //count += _sets.CountIntersection(set, ids); // no need to cache in set reg. as call is cached further up in call chain
                count += IdSet.IntersectionCount(ids, set); 
            } else {
                throw new Exception("integrity problems with index. ");
            }
            //if (TryGetValue(value, out var set)) {
            //    count += set.Where(ids.Has).Count();
            //} else {
            //    throw new Exception("Integrity problems with index. ");
            //}
        }
        return count;
    }

    public int CountGreaterThan(T from, bool inclusive) {
        if (ValueCount == 0) return 0;
        var count = 0;
        ensureSortedValues();
        foreach (var value in greaterThan(_sortedValues!, from, inclusive)) {
            if (TryGetValue(value, out var set)) {
                count += set.Count;
            } else {
                throw new Exception("Integrity problems with index. ");
            }
        }
        return count;
    }
    public int CountEqual(T value) {
        if (TryGetValue(value, out var set)) return set.Count;
        return 0;
    }
    public int CountLessThan(T to, bool inclusive) {
        if (ValueCount == 0) return 0;
        var count = 0;
        ensureSortedValues();
        foreach (var value in lessThan(_sortedValues!, to, inclusive)) {
            if (TryGetValue(value, out var set)) {
                count += set.Count;
            } else {
                throw new Exception("Integrity problems with index. ");
            }
        }
        return count;
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
    static IEnumerable<T> greaterThan(List<T> sortedList, T from, bool inclusive) {
        int index = sortedList.BinarySearch(from);
        if (index < 0) index = ~index;
        else if (!inclusive) index++;
        if (index < 0 || index >= sortedList.Count) yield break;
        for (int i = index; i < sortedList.Count; i++) yield return sortedList[i];
    }
    static IEnumerable<T> lessThan(List<T> sortedList, T to, bool inclusive) {
        int index = sortedList.BinarySearch(to);
        if (index < 0) index = ~index - 1;
        else if (!inclusive) index--;
        if (index < 0 || index >= sortedList.Count) yield break;
        for (int i = index; i >= 0; i--) yield return sortedList[i];
    }
    static IEnumerable<T> rangeSearch(List<T> sortedList, T from, T to, bool fromInclusive, bool toInclusive) {
        int lower = sortedList.BinarySearch(from!);
        lower = lower < 0 ? ~lower : fromInclusive ? lower : lower + 1;
        int upper = sortedList.BinarySearch(to!);
        upper = upper < 0 ? ~upper - 1 : toInclusive ? upper : upper - 1;
        if (lower < 0 || lower >= sortedList.Count || upper < 0 || upper >= sortedList.Count) yield break;
        for (int i = lower; i <= upper; i++) yield return sortedList[i];
    }
}
