namespace Relatude.DB.DataStores.Indexes;

// content equality for the intern table: order sensitive and exact per element, matching how arrays
// are stored on the nodes themselves (["a","b"] and ["b","a"] intern separately on purpose - the
// canonical array must round-trip through persistence exactly as the node supplied it)
sealed class ArrayEqualityComparer<T> : IEqualityComparer<T[]> where T : notnull {
    // strings compare ordinally, matching the ordinal hashing/comparison used by the rest of the
    // index stack; every other element type uses its default equality
    static readonly IEqualityComparer<T> _elementComparer =
        typeof(T) == typeof(string) ? (IEqualityComparer<T>)(object)StringComparer.Ordinal : EqualityComparer<T>.Default;
    public static readonly ArrayEqualityComparer<T> Instance = new();
    public bool Equals(T[]? x, T[]? y) {
        if (ReferenceEquals(x, y)) return true;
        if (x == null || y == null || x.Length != y.Length) return false;
        for (var i = 0; i < x.Length; i++) if (!_elementComparer.Equals(x[i], y[i])) return false;
        return true;
    }
    public int GetHashCode(T[] a) {
        var h = new HashCode();
        h.Add(a.Length);
        foreach (var s in a) h.Add(s, _elementComparer);
        return h.ToHashCode();
    }
}

/// <summary>
/// Normalizes the node → element[] mapping of a value array index: each distinct array (by
/// content, order sensitive) is stored once in a reference counted intern table and nodes map to
/// its id (dense array backed at scale). Typically a handful of value combinations are shared by
/// millions of nodes, so this replaces a dictionary entry + array object + elements PER NODE with
/// one int per node (measured 965 MB → 39 MB at 10M nodes with the shop tag profile).
/// Not thread safe: mutations run under the store's write lock like the indexes that own it.
/// </summary>
internal sealed class ValueArrayInternTable<T> where T : notnull {
    static readonly T[] _emptyArray = [];
    readonly Dictionary<T[], int> _arrayIdByArray = new(ArrayEqualityComparer<T>.Instance);
    readonly List<T[]?> _arrayById = []; // arrayId -> canonical array (null = freed slot awaiting reuse)
    readonly List<int> _refCountByArrayId = [];
    readonly Stack<int> _freeArrayIds = new();
    readonly ValueByIdMap<int> _arrayIdByNodeId = new();

    public int Count => _arrayIdByNodeId.Count;
    public void Add(int nodeId, T[] value) => _arrayIdByNodeId.Add(nodeId, intern(value));
    /// <summary>Removes the node's entry and releases its interned array, resolved by the STORED
    /// id so refcounts stay exact even if the caller's array instance differs. False when the
    /// node has no entry.</summary>
    public bool Remove(int nodeId) {
        if (!_arrayIdByNodeId.TryGetValue(nodeId, out var arrayId)) return false;
        _arrayIdByNodeId.Remove(nodeId);
        release(arrayId);
        return true;
    }
    public IEnumerable<KeyValuePair<int, T[]>> All {
        get {
            foreach (var (nodeId, arrayId) in _arrayIdByNodeId) yield return new(nodeId, _arrayById[arrayId]!);
        }
    }
    int intern(T[] v) {
        if (_arrayIdByArray.TryGetValue(v, out var arrayId)) {
            _refCountByArrayId[arrayId]++;
            return arrayId;
        }
        var canonical = v.Length == 0 ? _emptyArray : (T[])v.Clone(); // the canonical instance must never alias a caller owned array
        if (_freeArrayIds.TryPop(out arrayId)) {
            _arrayById[arrayId] = canonical;
            _refCountByArrayId[arrayId] = 1;
        } else {
            arrayId = _arrayById.Count;
            _arrayById.Add(canonical);
            _refCountByArrayId.Add(1);
        }
        _arrayIdByArray.Add(canonical, arrayId);
        return arrayId;
    }
    void release(int arrayId) {
        if (--_refCountByArrayId[arrayId] > 0) return;
        _arrayIdByArray.Remove(_arrayById[arrayId]!);
        _arrayById[arrayId] = null; // slot is reused via the free list; clearing lets the elements be collected
        _freeArrayIds.Push(arrayId);
    }
}
