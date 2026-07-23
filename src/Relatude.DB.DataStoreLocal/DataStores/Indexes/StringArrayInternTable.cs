namespace Relatude.DB.DataStores.Indexes;

// content equality for the intern table: order sensitive and ordinal, matching how arrays are
// stored on the nodes themselves (["a","b"] and ["b","a"] intern separately on purpose - the
// canonical array must round-trip through persistence exactly as the node supplied it)
sealed class StringArrayEqualityComparer : IEqualityComparer<string[]> {
    public static readonly StringArrayEqualityComparer Instance = new();
    public bool Equals(string[]? x, string[]? y) {
        if (ReferenceEquals(x, y)) return true;
        if (x == null || y == null || x.Length != y.Length) return false;
        for (var i = 0; i < x.Length; i++) if (!string.Equals(x[i], y[i], StringComparison.Ordinal)) return false;
        return true;
    }
    public int GetHashCode(string[] a) {
        var h = new HashCode();
        h.Add(a.Length);
        foreach (var s in a) h.Add(s, StringComparer.Ordinal);
        return h.ToHashCode();
    }
}

/// <summary>
/// Normalizes the node → string[] mapping of a string array index: each distinct array (by
/// content, order sensitive) is stored once in a reference counted intern table and nodes map to
/// its id (dense array backed at scale). Typically a handful of value combinations are shared by
/// millions of nodes, so this replaces a dictionary entry + array object + strings PER NODE with
/// one int per node (measured 965 MB → 39 MB at 10M nodes with the shop tag profile).
/// Not thread safe: mutations run under the store's write lock like the indexes that own it.
/// </summary>
internal sealed class StringArrayInternTable {
    static readonly string[] _emptyArray = [];
    readonly Dictionary<string[], int> _arrayIdByArray = new(StringArrayEqualityComparer.Instance);
    readonly List<string[]?> _arrayById = []; // arrayId -> canonical array (null = freed slot awaiting reuse)
    readonly List<int> _refCountByArrayId = [];
    readonly Stack<int> _freeArrayIds = new();
    readonly ValueByIdMap<int> _arrayIdByNodeId = new();

    public int Count => _arrayIdByNodeId.Count;
    public void Add(int nodeId, string[] value) => _arrayIdByNodeId.Add(nodeId, intern(value));
    /// <summary>Removes the node's entry and releases its interned array, resolved by the STORED
    /// id so refcounts stay exact even if the caller's array instance differs. False when the
    /// node has no entry.</summary>
    public bool Remove(int nodeId) {
        if (!_arrayIdByNodeId.TryGetValue(nodeId, out var arrayId)) return false;
        _arrayIdByNodeId.Remove(nodeId);
        release(arrayId);
        return true;
    }
    public IEnumerable<KeyValuePair<int, string[]>> All {
        get {
            foreach (var (nodeId, arrayId) in _arrayIdByNodeId) yield return new(nodeId, _arrayById[arrayId]!);
        }
    }
    int intern(string[] v) {
        if (_arrayIdByArray.TryGetValue(v, out var arrayId)) {
            _refCountByArrayId[arrayId]++;
            return arrayId;
        }
        var canonical = v.Length == 0 ? _emptyArray : (string[])v.Clone(); // the canonical instance must never alias a caller owned array
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
        _arrayById[arrayId] = null; // slot is reused via the free list; clearing lets the strings be collected
        _freeArrayIds.Push(arrayId);
    }
}
