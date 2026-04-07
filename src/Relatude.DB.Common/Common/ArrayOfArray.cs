using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
namespace WAF;

public sealed class ArrayOfArray<TKey, TValue> {

    // maximum number of items in a range before it is split,
    // larger values optimize for better performance on larger ranges, but reduce performance for smaller ranges
    // a value of 512 is a good compromise for most cases and optimal to around 5 million items
    public const int RANGE_MAX_SIZE = 512;

    // min length of range array (does not matter if compacted)
    // there is only a small performance difference between 1 and 512 ( max 10 % )
    // larger values increasse memory usage after random deletes quite a lot
    public const int RANGE_MIN_SIZE = 1;

    // sum of two ranges before they are merged,
    // larger values reduce the number of ranges but increase memory usage after deletion
    public const int RANGE_MERGE_SIZE = 128;

    static Range<TKey, TValue>[] _empty = [];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int compare(TKey x, TKey y) => Comparer<TKey>.Default.Compare(x, y);

    int _rangeCount;
    int _valueCount;
    Range<TKey, TValue>[] _ranges = _empty;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void grow() {
        var newRanges = new Range<TKey, TValue>[_ranges.Length == 0 ? 1 : _ranges.Length * 2];
        _ranges.CopyTo(newRanges, 0);
        _ranges = newRanges;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void append(Range<TKey, TValue> r) {
        if (_ranges.Length == _rangeCount) grow();
        _ranges[_rangeCount] = r;
        _rangeCount++;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void insertAt(int index, Range<TKey, TValue> r) {
        if (_ranges.Length == _rangeCount) grow();
        Array.Copy(_ranges, index, _ranges, index + 1, _rangeCount - index);
        _ranges[index] = r;
        _rangeCount++;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void removeAt(int index) {
        Array.Copy(_ranges, index + 1, _ranges, index, _rangeCount - index - 1);
        _rangeCount--;
        if (_rangeCount < _ranges.Length / 4) {
            var newRanges = new Range<TKey, TValue>[_ranges.Length / 2];
            Array.Copy(_ranges, newRanges, _rangeCount);
            _ranges = newRanges;
        }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    int binarySearch(TKey key) {
        var lower = 0;
        var upper = _rangeCount - 1;
        while (lower <= upper) {
            var index = lower + (upper - lower >> 1);
            var comparison = compare(_ranges[index].LastKey, key);
            if (comparison == 0) return index;
            else if (comparison < 0) lower = index + 1;
            else upper = index - 1;
        }
        return ~lower;
    }
    public TKey? FirstKey { get; private set; }
    public TKey? LastKey { get; private set; }
    public int Count => _valueCount;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void getRangeIndexes(TKey from, TKey to, out int fromIndex, out int toIndex) {
        if (compare(from, FirstKey!) <= 0) fromIndex = 0;
        else if (compare(from, LastKey!) > 0) fromIndex = _rangeCount;
        else {
            fromIndex = binarySearch(from);
            if (fromIndex < 0) fromIndex = ~fromIndex;
        }
        if (compare(to, LastKey!) > 0) toIndex = _rangeCount;
        else if (compare(to, FirstKey!) < 0) toIndex = 0;
        else {
            toIndex = binarySearch(to);
            if (toIndex < 0) toIndex = ~toIndex;
        }
    }
    public IEnumerable<KeyValuePair<TKey, TValue>> GetRange(TKey from, TKey to, bool fromInclusive = true, bool toInclusive = true) {
        getRangeIndexes(from, to, out var fromIndex, out var toIndex);
        for (var i = fromIndex; i < toIndex; i++) foreach (var kv in _ranges[i].GetRange(from, to, fromInclusive, toInclusive)) yield return kv;
    }
    public int CountRange(TKey from, TKey to, bool fromInclusive = true, bool toInclusive = true) {
        getRangeIndexes(from, to, out var fromIndex, out var toIndex);
        int count = 0;
        for (var i = fromIndex; i < toIndex; i++) count += _ranges[i].CountRange(from, to, fromInclusive, toInclusive);
        return count;
    }
    public void Add(TKey key, TValue value) {
        if (_rangeCount == 0) {
            _ranges = [new(key, value)];
            _rangeCount = 1;
            _valueCount = 1;
            FirstKey = key;
            LastKey = key;
            return;
        }
        int index;
        if (compare(key, LastKey!) > 0) {
            index = _rangeCount - 1;
        } else {
            index = binarySearch(key);
            if (index < 0) {
                index = ~index;
                if (index == _rangeCount) index--;
            }
        }
        var range = _ranges[index];
        if (range.Count >= RANGE_MAX_SIZE) {
            if (index == _rangeCount - 1 && compare(key, range.LastKey) > 0) {
                append(new(key, value));
            } else {
                range.Split(out var left, out var right);
                _ranges[index] = left;
                insertAt(index + 1, right);
                if (compare(key, left.LastKey) > 0) right.Add(key, value);
                else left.Add(key, value);
            }
        } else {
            range.Add(key, value);
        }
        if (compare(key, LastKey!) > 0) LastKey = key;
        else if (compare(FirstKey!, key) > 0) FirstKey = key;
        _valueCount++;
    }
    public void Remove(TKey key) {
        int index;
        if (compare(key, LastKey!) == 0) index = _rangeCount - 1;
        else {
            index = binarySearch(key);
            if (index < 0) {
                index = ~index;
                if (index == _rangeCount) throw new Exception("Not found");
            }
        }
        var range = _ranges[index];
        if (range.Count == 1) {
            if (compare(range.FirstKey, key) != 0) throw new Exception("Not found");
            removeAt(index);
        } else {
            range.Remove(key);
            if (_rangeCount > 1) { // merge ranges if they are small
                if (index < _rangeCount - 1) { // has range to the right
                    var right = _ranges[index + 1];
                    if (range.Count + right.Count <= RANGE_MERGE_SIZE) {
                        range.AddRange(right);
                        removeAt(index + 1);
                    }
                } else if (index > 0) { // has range to the left
                    var left = _ranges[index - 1];
                    if (range.Count + left.Count <= RANGE_MERGE_SIZE) {
                        left.AddRange(range);
                        removeAt(index);
                    }
                }
            }
        }
        _valueCount--;
        FirstKey = _valueCount > 0 ? _ranges[0].FirstKey : default;
        LastKey = _valueCount > 0 ? _ranges[_rangeCount - 1].LastKey : default;
    }
    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue? value) {
        var index = binarySearch(key);
        if (index < 0) {
            index = ~index;
            if (index == _rangeCount) {
                value = default;
                return false;
            }
        }
        return _ranges[index].TryGet(key, out value);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(TKey key) {
        var index = binarySearch(key);
        if (index < 0) index = ~index;
        if (index == _rangeCount) return false;
        return _ranges[index].Contains(key);
    }
    public IEnumerable<TKey> Keys { get { for (var i = 0; i < _rangeCount; i++) foreach (var key in _ranges[i].Keys) yield return key; } }
    public IEnumerable<TValue> Values { get { for (var i = 0; i < _rangeCount; i++) foreach (var kv in _ranges[i].Values) yield return kv; } }
    public IEnumerable<KeyValuePair<TKey, TValue>> KeysAndValues { get { for (var i = 0; i < _rangeCount; i++) foreach (var kv in _ranges[i].KeysAndValues) yield return kv; } }
    public void Compact() {
        var newVersion = new ArrayOfArray<TKey, TValue>();
        foreach (var kv in KeysAndValues) newVersion.Add(kv.Key, kv.Value);
        _ranges = new Range<TKey, TValue>[newVersion._rangeCount];
        _rangeCount = newVersion._rangeCount;
        Array.Copy(newVersion._ranges, _ranges, _rangeCount);
        for (var i = 0; i < _rangeCount; i++) {
            _ranges[i].Compact();
        }
    }
    public void Clear() {
        _ranges = [];
        _rangeCount = 0;
        _valueCount = 0;
        FirstKey = default;
        LastKey = default;
    }
}

internal class Range<TKey, TValue> {
    KeyValuePair<TKey, TValue>[] _values;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int compare(TKey x, TKey y) => Comparer<TKey>.Default.Compare(x, y);
    public Range(TKey key, TValue value) {
        _values = new KeyValuePair<TKey, TValue>[ArrayOfArray<TKey, TValue>.RANGE_MIN_SIZE];
        _values[0] = new(key, value);
        FirstKey = key;
        LastKey = key;
        Count = 1;
    }
    private Range(KeyValuePair<TKey, TValue>[] sortedValues, int count) {
        _values = sortedValues;
        Count = count;
        FirstKey = _values[0].Key;
        LastKey = _values[Count - 1].Key;
    }
    public int Count { get; private set; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void grow() {
        var newRanges = new KeyValuePair<TKey, TValue>[_values.Length * 2];
        _values.CopyTo(newRanges, 0);
        _values = newRanges;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void append(KeyValuePair<TKey, TValue> v) {
        if (Count == _values.Length) grow();
        _values[Count] = v;
        Count++;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void insertAt(int index, KeyValuePair<TKey, TValue> v) {
        if (Count == _values.Length) grow();
        Array.Copy(_values, index, _values, index + 1, Count - index);
        _values[index] = v;
        Count++;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void removeAt(int index) {
        if (Count == 0) throw new Exception("Cannot remove last item");
        Array.Copy(_values, index + 1, _values, index, Count - index - 1);
        Count--;
        if (Count > ArrayOfArray<TKey, TValue>.RANGE_MIN_SIZE && Count < _values.Length / 4) {
            var newRanges = new KeyValuePair<TKey, TValue>[_values.Length / 2];
            Array.Copy(_values, newRanges, Count);
            _values = newRanges;
        }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    int binarySearch(TKey key) {
        var lower = 0;
        var upper = Count - 1;
        while (lower <= upper) {
            var index = lower + (upper - lower >> 1);
            var comparison = compare(_values[index].Key, key);
            if (comparison == 0) return index;
            else if (comparison < 0) lower = index + 1;
            else upper = index - 1;
        }
        return ~lower;
    }
    public IEnumerable<TKey> Keys { get { for (var i = 0; i < Count; i++) yield return _values[i].Key; } }
    public IEnumerable<TValue> Values { get { for (var i = 0; i < Count; i++) yield return _values[i].Value; } }
    public IEnumerable<KeyValuePair<TKey, TValue>> KeysAndValues { get { for (var i = 0; i < Count; i++) yield return _values[i]; } }
    public TKey FirstKey { get; private set; }
    public TKey LastKey { get; private set; }
    public void Split(out Range<TKey, TValue> left, out Range<TKey, TValue> right) {
        var leftCount = Count >> 1;
        var rightCount = Count - leftCount;
        var leftValues = new KeyValuePair<TKey, TValue>[ArrayOfArray<TKey, TValue>.RANGE_MIN_SIZE > leftCount ? ArrayOfArray<TKey, TValue>.RANGE_MIN_SIZE : leftCount];
        var rightValues = new KeyValuePair<TKey, TValue>[ArrayOfArray<TKey, TValue>.RANGE_MIN_SIZE > rightCount ? ArrayOfArray<TKey, TValue>.RANGE_MIN_SIZE : rightCount];
        Array.Copy(_values, leftValues, leftCount);
        Array.Copy(_values, leftCount, rightValues, 0, rightCount);
        left = new(leftValues, leftCount);
        right = new(rightValues, rightCount);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet(TKey key, [MaybeNullWhen(false)] out TValue value) {
        var index = binarySearch(key);
        if (index < 0) {
            value = default;
            return false;
        }
        value = _values[index].Value;
        return true;
    }
    public void Add(TKey key, TValue value) {
        if (compare(key, LastKey) > 0) {
            append(new(key, value));
            LastKey = key;
        } else {
            var index = binarySearch(key);
            if (index >= 0) throw new Exception("Already exists");
            index = ~index;
            insertAt(index, new(key, value));
            if (index == 0) FirstKey = key;
        }
    }
    public void Remove(TKey key) {
        var index = binarySearch(key);
        if (index < 0) throw new Exception("Not found");
        removeAt(index);
        LastKey = _values[Count - 1].Key;
        FirstKey = _values[0].Key;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void getFromToIndexes(TKey from, TKey to, bool fromInclusive, bool toInclusive, out int fromIndex, out int toIndex) {
        if (compare(from, FirstKey) < 0) fromIndex = 0;
        else if (compare(from, LastKey) > 0) fromIndex = Count;
        else {
            fromIndex = binarySearch(from);
            if (fromIndex < 0) fromIndex = ~fromIndex;
            else if (!fromInclusive) fromIndex++;
        }
        if (compare(to, LastKey) > 0) toIndex = Count;
        else if (compare(to, FirstKey) < 0) toIndex = 0;
        else {
            toIndex = binarySearch(to);
            if (toIndex < 0) toIndex = ~toIndex;
            else if (toInclusive) toIndex++;
        }
    }
    public IEnumerable<KeyValuePair<TKey, TValue>> GetRange(TKey from, TKey to, bool fromInclusive = true, bool toInclusive = true) {
        getFromToIndexes(from, to, fromInclusive, toInclusive, out var fromIndex, out var toIndex);
        for (var i = fromIndex; i < toIndex; i++) yield return _values[i];
    }
    public int CountRange(TKey from, TKey to, bool fromInclusive = true, bool toInclusive = true) {
        getFromToIndexes(from, to, fromInclusive, toInclusive, out var fromIndex, out var toIndex);
        return toIndex - fromIndex;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(TKey key) {
        return binarySearch(key) >= 0;
    }
    public override string ToString() {
        return $"{FirstKey} - {LastKey} ({Count})";
    }
    internal void Compact() {
        if (Count <= _values.Length) return;
        var newValues = new KeyValuePair<TKey, TValue>[Count];
        Array.Copy(_values, newValues, Count);
        _values = newValues;
    }
    internal void AddRange(Range<TKey, TValue> right) {
        if (Count + right.Count <= _values.Length) {
            Array.Copy(right._values, 0, _values, Count, right.Count);
            Count += right.Count;
            LastKey = right.LastKey;
        } else {
            var newValues = new KeyValuePair<TKey, TValue>[Count + right.Count];
            Array.Copy(_values, newValues, Count);
            Array.Copy(right._values, 0, newValues, Count, right.Count);
            _values = newValues;
            Count += right.Count;
            LastKey = right.LastKey;
        }
    }
}

