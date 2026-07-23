using System.Collections;
using System.Numerics;

namespace Relatude.DB.DataStores.Sets;

/// <summary>
/// Set of node ids stored as one bit per id in a window of 64-bit words. Node ids are dense
/// small integers, so this is both the smallest and the fastest representation for large sets:
/// membership is one bit test and set operations (intersection counts in particular, which
/// facet counting is made of) run word-parallel with popcount instead of per-element probing.
/// Enumeration order is always ascending id.
/// </summary>
public sealed class DenseBitSet : ICollection<int> {
    ulong[] _words;
    int _base; // id of bit 0, always a multiple of 64
    public int Count { get; private set; }
    public bool IsReadOnly => false;

    public DenseBitSet(int minId, int maxId) {
        _base = (minId < 0 ? 0 : minId) & ~63;
        _words = new ulong[wordsNeeded(_base, maxId)];
    }
    DenseBitSet(ulong[] words, int baseId, int count) {
        _words = words;
        _base = baseId;
        Count = count;
    }
    static int wordsNeeded(int baseId, int maxId) => ((maxId - baseId) >> 6) + 1;

    /// <summary>True when a bit set is worth it for a set of this shape: large enough to matter and
    /// dense enough that the window costs at most about twice an int array (it is then still much
    /// faster, and usually far smaller).</summary>
    //public static bool WorthIt(int count, int minId, int maxId) => false;
    public static bool WorthIt(int count, int minId, int maxId) => count > 256 && minId >= 0 && (long)maxId - minId < (long)count * 64;

    public static DenseBitSet From(IEnumerable<int> ids, int minId, int maxId) {
        var set = new DenseBitSet(minId, maxId);
        foreach (var id in ids) set.Add(id);
        return set;
    }
    public DenseBitSet Clone() => new((ulong[])_words.Clone(), _base, Count);

    public bool Contains(int id) {
        var i = id - _base;
        if ((uint)i >= (uint)_words.Length << 6) return false;
        return (_words[i >> 6] & 1UL << (i & 63)) != 0;
    }
    public void Add(int id) {
        if (id < _base || id >= _base + (_words.Length << 6)) grow(id);
        var i = id - _base;
        ref var w = ref _words[i >> 6];
        var bit = 1UL << (i & 63);
        if ((w & bit) != 0) return;
        w |= bit;
        Count++;
    }
    public bool Remove(int id) {
        var i = id - _base;
        if ((uint)i >= (uint)_words.Length << 6) return false;
        ref var w = ref _words[i >> 6];
        var bit = 1UL << (i & 63);
        if ((w & bit) == 0) return false;
        w &= ~bit;
        Count--;
        return true;
    }
    void grow(int id) {
        var newBase = Math.Min(_base, (id < 0 ? 0 : id) & ~63);
        var newMax = Math.Max(id, _base + (_words.Length << 6) - 1);
        var newWords = new ulong[Math.Max(wordsNeeded(newBase, newMax), _words.Length * 2)];
        Array.Copy(_words, 0, newWords, (_base - newBase) >> 6, _words.Length);
        _words = newWords;
        _base = newBase;
    }
    public void Clear() {
        Array.Clear(_words);
        Count = 0;
    }
    public IEnumerator<int> GetEnumerator() {
        for (var w = 0; w < _words.Length; w++) {
            var word = _words[w];
            while (word != 0) {
                var bit = BitOperations.TrailingZeroCount(word);
                yield return _base + (w << 6) + bit;
                word &= word - 1; // clear lowest set bit
            }
        }
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void CopyTo(int[] array, int arrayIndex) {
        foreach (var id in this) array[arrayIndex++] = id;
    }
    public int MemSizeEstimate => _words.Length * 8 + 40;

    /// <summary>|a ∩ b| by word-parallel AND + popcount over the overlapping window.</summary>
    public static int AndCount(DenseBitSet a, DenseBitSet b) {
        if (a.Count == 0 || b.Count == 0) return 0;
        overlap(a, b, out var wa, out var wb, out var n);
        var count = 0;
        for (var i = 0; i < n; i++) count += BitOperations.PopCount(a._words[wa + i] & b._words[wb + i]);
        return count;
    }
    public static DenseBitSet And(DenseBitSet a, DenseBitSet b) {
        overlap(a, b, out var wa, out var wb, out var n);
        var words = new ulong[n < 1 ? 1 : n];
        var count = 0;
        for (var i = 0; i < n; i++) {
            var w = a._words[wa + i] & b._words[wb + i];
            words[i] = w;
            count += BitOperations.PopCount(w);
        }
        return new DenseBitSet(words, Math.Max(a._base, b._base), count);
    }
    public static DenseBitSet Or(DenseBitSet a, DenseBitSet b) {
        var newBase = Math.Min(a._base, b._base);
        var newMax = Math.Max(a._base + (a._words.Length << 6), b._base + (b._words.Length << 6)) - 1;
        var words = new ulong[wordsNeeded(newBase, newMax)];
        var count = 0;
        var oa = (a._base - newBase) >> 6;
        var ob = (b._base - newBase) >> 6;
        for (var i = 0; i < words.Length; i++) {
            var ia = i - oa;
            var ib = i - ob;
            var w = ((uint)ia < (uint)a._words.Length ? a._words[ia] : 0) | ((uint)ib < (uint)b._words.Length ? b._words[ib] : 0);
            words[i] = w;
            count += BitOperations.PopCount(w);
        }
        return new DenseBitSet(words, newBase, count);
    }
    /// <summary>a \ b (all of a except the ids also in b).</summary>
    public static DenseBitSet AndNot(DenseBitSet a, DenseBitSet b) {
        var words = (ulong[])a._words.Clone();
        overlap(a, b, out var wa, out var wb, out var n);
        var removed = 0;
        for (var i = 0; i < n; i++) {
            var before = words[wa + i];
            var after = before & ~b._words[wb + i];
            words[wa + i] = after;
            removed += BitOperations.PopCount(before) - BitOperations.PopCount(after);
        }
        return new DenseBitSet(words, a._base, a.Count - removed);
    }
    static void overlap(DenseBitSet a, DenseBitSet b, out int wa, out int wb, out int n) {
        var from = Math.Max(a._base, b._base);
        var to = Math.Min(a._base + (a._words.Length << 6), b._base + (b._words.Length << 6));
        wa = (from - a._base) >> 6;
        wb = (from - b._base) >> 6;
        n = to > from ? (to - from) >> 6 : 0;
    }
}
