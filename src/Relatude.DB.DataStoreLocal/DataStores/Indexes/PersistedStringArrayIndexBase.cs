using Relatude.DB.DataStores.Sets;

namespace Relatude.DB.DataStores.Indexes;

/// <summary>
/// Base class for string-array indexes backed by an <see cref="IPersistedIndexStore"/>. The
/// backend only persists the raw id → string[] mapping (through the three primitives at the
/// bottom); queries are answered from an in-memory mirror of that mapping, lazily loaded on first
/// use and kept in sync by writing through on every mutation. Mutations run inside the store's
/// transaction, so a backend rollback and the reversal actions replayed on the mirror stay
/// consistent with each other.
/// </summary>
public abstract class PersistedStringArrayIndexBase : PersistedIndexBase, IStringArrayIndex {
    IdByValue<string> _nodeIdByValue;
    // node arrays are normalized into a reference counted intern table (see StringArrayInternTable):
    // typically a handful of value combinations are shared by millions of nodes
    StringArrayInternTable _arrays;
    readonly SetRegister _sets;
    readonly object _loadLock = new();
    bool _loaded;

    protected PersistedStringArrayIndexBase(IPersistedIndexStore store, bool justCreated, SetRegister sets, string uniqueKey, string friendlyName)
        : base(store, justCreated) {
        _sets = sets;
        _nodeIdByValue = new(sets);
        _arrays = new();
        UniqueKey = uniqueKey;
        FriendlyName = friendlyName;
    }

    public string UniqueKey { get; }
    public string FriendlyName { get; }

    void ensureLoaded() {
        if (_loaded) return;
        lock (_loadLock) {
            if (_loaded) return;
            foreach (var kv in ReadAllPersisted()) addToMemory(kv.Key, kv.Value);
            _loaded = true;
        }
    }
    void addToMemory(int nodeId, string[] value) {
        _arrays.Add(nodeId, value);
        // dedup: the same string may occur several times in one node's array,
        // but the node must only be indexed once per unique value (and deindexed symmetrically)
        foreach (var str in value.Distinct()) _nodeIdByValue.Index(str, nodeId);
    }
    void removeFromMemory(int nodeId, string[] value) {
        _arrays.Remove(nodeId);
        foreach (var str in value.Distinct()) _nodeIdByValue.DeIndex(str, nodeId);
    }

    public void Add(int nodeId, object value) {
        ensureLoaded();
        var v = (string[])value;
        addToMemory(nodeId, v);
        PersistAdd(nodeId, v);
    }
    public void Remove(int nodeId, object value) {
        ensureLoaded();
        var v = (string[])value;
        removeFromMemory(nodeId, v);
        PersistRemove(nodeId);
    }
    public void RegisterAddDuringStateLoad(int nodeId, object value) => Add(nodeId, value);
    public void RegisterRemoveDuringStateLoad(int nodeId, object value) => Remove(nodeId, value);

    public IdSet Filter(IdSet set, IndexOperator op, string value) {
        throw new NotSupportedException("The string array index does not support the " + op.ToString().ToUpper() + " operator. ");
    }
    public int CountEqual(IdSet set, string value) {
        ensureLoaded();
        if (_nodeIdByValue.TryGetValueIdSet(value, out var ids)) {
            return _sets.CountIntersection(set, ids);
        }
        return 0;
    }
    public bool ContainsValue(string value) {
        ensureLoaded();
        return _nodeIdByValue.ContainsValue(value);
    }
    public IEnumerable<string> GetUniqueValues() {
        ensureLoaded();
        return _nodeIdByValue.Values;
    }
    public int MaxCount(IndexOperator op, string value) {
        ensureLoaded();
        switch (op) {
            case IndexOperator.Equal:
                return 1;
            case IndexOperator.NotEqual:
            case IndexOperator.Greater:
            case IndexOperator.Smaller:
            case IndexOperator.GreaterOrEqual:
            case IndexOperator.SmallerOrEqual:
                return _arrays.Count;
            default: break;
        }
        throw new NotSupportedException(GetType().Name + " types does not support the " + op.ToString().ToUpper() + " operator. ");
    }
    public IdSet FilterInValues(IdSet set, List<string> values) {
        ensureLoaded();
        List<IdSet> matches = [];
        foreach (var value in values) {
            if (_nodeIdByValue.TryGetValueIdSet(value, out var ids)) {
                var matchForOneValue = _sets.Intersection(set, ids);
                if (matchForOneValue.Count > 0) matches.Add(matchForOneValue);
            }
        }
        return _sets.Union(matches);
    }

    // The mirror is only a cache of the backend data: drop it and let the next access reload. This
    // is also how the index follows ResetAll, where the store wipes the backend and then calls
    // ClearCache on every open index.
    public void ClearCache() {
        lock (_loadLock) {
            _nodeIdByValue = new(_sets);
            _arrays = new();
            _loaded = false;
        }
    }
    public void CompressMemory() { }
    public void Dispose() { }

    // ---- backend persistence primitives ----

    /// <summary>Every persisted (id, value) entry; called once to populate the in-memory mirror.</summary>
    protected abstract IEnumerable<KeyValuePair<int, string[]>> ReadAllPersisted();
    /// <summary>Persist the mapping id → value. The id is never already present.</summary>
    protected abstract void PersistAdd(int nodeId, string[] value);
    /// <summary>Remove the persisted mapping for id.</summary>
    protected abstract void PersistRemove(int nodeId);
}
