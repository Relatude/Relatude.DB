
using System.Collections;
using System.Collections.Frozen;
using System.ComponentModel;

namespace Relatude.DB.DataStores.Sets;
/// <summary>
/// Immutable set of ids.
/// StateId is used to identify the state of the set.
/// If StateId is long.MaxValue, the set or resulting sets are not cached.
/// </summary>
public class IdSet {
    const int arrayHashLimit = 200;
    ICollection<int> _ids;
    int[]? _asArray;
    public IdSet(ICollection<int> uniqueListOfIds, long stateId) {
        StateId = stateId;
        var arr = new int[uniqueListOfIds.Count]; // always starts as array, as it may never use the has method that convert it to a fastset
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
    public bool Has(int id) { // Using "Has" name instead of "Contains" to avoid confusion with the "Contains" method of Enumerable or LINQ
        ensureFastSetIfBetter();
        return _ids.Contains(id);
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
        if (_ids is int[] && _ids.Count > arrayHashLimit) {
            _ids = FastSet.Create(_ids);
        }
    }
    static internal int IntersectionCount(IdSet set1, IdSet set2) {
        var s1 = set1._ids;
        var s2 = set2._ids;
        if (s1.Count == 0 || s2.Count == 0) return 0;
        set1.ensureFastSetIfBetter();
        set2.ensureFastSetIfBetter();
        var (small, big) = s1.Count < s2.Count ? (s1, s2) : (s2, s1);
        var count = 0;
        foreach (var id in small) if (big.Contains(id)) count++;
        return count;
    }
    public int MemSizeEstimate => 3 * sizeof(int) * _ids.Count + 30;
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