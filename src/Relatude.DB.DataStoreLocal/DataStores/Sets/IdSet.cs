
using System.Collections;
using System.Collections.Frozen;

namespace Relatude.DB.DataStores.Sets;
/// <summary>
/// Immutable set of ids.
/// StateId is used to identify the state of the set.
/// If StateId is long.MaxValue, the set or resulting sets are not cached.
/// Large dense sets are held as a <see cref="DenseBitSet"/> (ascending enumeration, O(1) lookup,
/// word-parallel intersection counts); everything else keeps the given order in an int array.
/// NB: passing a DenseBitSet to the constructor transfers ownership of it - callers holding on
/// to a live bit set (e.g. MutableSet) must pass a clone.
/// </summary>
public class IdSet {
    const int arrayHashLimit = 200;
    readonly ICollection<int> _ids;
    ISet<int>? _fastSet; // lookup accelerator for the int[] representation, created on demand
    int[]? _asArray;
    public IdSet(ICollection<int> uniqueListOfIds, long stateId) {
        StateId = stateId;
        if (uniqueListOfIds is DenseBitSet bits) { // ownership transferred, see class notes
            _ids = bits;
            return;
        }
        if (uniqueListOfIds is MutableSet ms && ms.TryGetBits(out var liveBits)) {
            _ids = liveBits.Clone(); // snapshot: one word-array copy instead of materializing ids
            return;
        }
        // only collections without meaningful order may switch to the (ascending) bit set
        // representation; arrays, lists and ordered sets keep their given order:
        if (uniqueListOfIds is HashSet<int> or MutableSet && uniqueListOfIds.Count > arrayHashLimit) {
            int min = int.MaxValue, max = int.MinValue;
            foreach (var id in uniqueListOfIds) {
                if (id < min) min = id;
                if (id > max) max = id;
            }
            if (DenseBitSet.WorthIt(uniqueListOfIds.Count, min, max)) {
                _ids = DenseBitSet.From(uniqueListOfIds, min, max);
                return;
            }
        }
        var arr = new int[uniqueListOfIds.Count];
        uniqueListOfIds.CopyTo(arr, 0);
        _ids = arr;
    }
    private IdSet(int id, long stateId) {
        _ids = new int[1] { id };
        StateId = stateId;
    }
    public static readonly IdSet Empty = new(EmptySet.Instance, 0);
    public static readonly IdSet EmptyUncachable = UncachableSet(EmptySet.Instance);
    public static IdSet SingleIdSet(int id) => new IdSet(id, long.MaxValue - id); // using id as stateId, avoiding collisions by using long.MaxValue - id
    public static IdSet UncachableSet(ICollection<int> ids) => new(ids, long.MaxValue); // MaxValue means not cached
    public long StateId { get; }
    public int Count => _ids.Count;
    public IEnumerable<int> Enumerate() => _ids;
    internal DenseBitSet? Bits => _ids as DenseBitSet; // enables word-parallel set operations in SetRegister
    public bool Has(int id) { // Using "Has" name instead of "Contains" to avoid confusion with the "Contains" method of Enumerable or LINQ
        if (_ids is DenseBitSet bits) return bits.Contains(id);
        ensureFastSetIfBetter();
        return _fastSet != null ? _fastSet.Contains(id) : _ids.Contains(id);
    }
    public int First() => _ids.First();
    string? _s = null;
    public string AsStringList() {
        if (_s == null) {
            var sb = new System.Text.StringBuilder();
            foreach (var id in _ids) {
                sb.Append(id);
                sb.Append(',');
            }
            if (sb.Length > 0) sb.Length--;
            _s = sb.ToString();
        }
        return _s;

    }
    void ensureFastSetIfBetter() {
        // never replaces _ids, only adds a lookup accelerator, so enumeration order is preserved
        if (_fastSet == null && _ids.Count > arrayHashLimit) {
            _fastSet = _ids.ToFrozenSet();
        }
    }
    /// <summary>
    /// Collects an enumeration of DISTINCT ids into the best set representation in one pass:
    /// a <see cref="DenseBitSet"/> when large and dense enough, otherwise a plain list.
    /// This replaces the old "new HashSet&lt;int&gt;(ids)" pattern in the set builders, which paid
    /// a hash insert and ~8x the memory per id only to be converted to a bit set right after -
    /// that materialization dominated the cold cost of large range and text queries.
    /// NB: the input must not contain duplicates (a list would keep them and corrupt Count);
    /// all callers enumerate index structures that hold each id at most once.
    /// </summary>
    static internal ICollection<int> CollectUnique(IEnumerable<int> uniqueIds) {
        var list = new List<int>();
        int min = int.MaxValue, max = int.MinValue;
        foreach (var id in uniqueIds) {
            if (id < min) min = id;
            if (id > max) max = id;
            list.Add(id);
        }
        if (DenseBitSet.WorthIt(list.Count, min, max)) return DenseBitSet.From(list, min, max);
        return list;
    }
    static internal int IntersectionCount(IdSet set1, IdSet set2) {
        if (set1.Count == 0 || set2.Count == 0) return 0;
        if (set1.Bits is { } b1 && set2.Bits is { } b2) return DenseBitSet.AndCount(b1, b2);
        var (small, big) = set1.Count < set2.Count ? (set1, set2) : (set2, set1);
        var count = 0;
        foreach (var id in small._ids) if (big.Has(id)) count++;
        return count;
    }
    public int MemSizeEstimate => _ids is DenseBitSet bits ? bits.MemSizeEstimate : 3 * sizeof(int) * _ids.Count + 30;
    public int[] ToArray() {
        if (_asArray == null) {
            if (_ids is int[] arr) return arr;
            _asArray = new int[_ids.Count];
            var i = 0;
            foreach (var id in _ids) _asArray[i++] = id;
        }
        return _asArray;
    }
}
