using Relatude.DB.DataStores.Sets;

namespace Relatude.DB.DataStores.Indexes;

/// <summary>
/// Non-generic handle to a lazily loaded index mirror, so the store can warm every array-index
/// mirror after open without knowing the element type (see DataStoreLocal.warmIndexesInBackground).
/// </summary>
internal interface IIndexMirror {
    void EnsureLoaded();
    string FriendlyName { get; }
}

/// <summary>
/// Base class for array indexes backed by an <see cref="IValueIndexEngine"/>, generic over the
/// element type. The backend only persists the raw id → element[] mapping (through the three
/// primitives at the bottom); queries are answered from an in-memory mirror of that mapping,
/// lazily loaded on first use and kept in sync by writing through on every mutation. Mutations run
/// inside the engine's transaction, so a backend rollback and the reversal actions replayed on the
/// mirror stay consistent with each other.
/// </summary>
public abstract class PersistedValueArrayIndexBase<T> : PersistedIndexBase, IValueArrayIndex<T>, IIndexMirror where T : notnull {
    IdByValue<T> _nodeIdByValue;
    // node arrays are normalized into a reference counted intern table (see ValueArrayInternTable):
    // typically a handful of value combinations are shared by millions of nodes
    ValueArrayInternTable<T> _arrays;
    readonly SetRegister _sets;
    readonly object _loadLock = new();
    bool _loaded;

    protected PersistedValueArrayIndexBase(IIndexEngine engine, bool justCreated, SetRegister sets, string uniqueKey, string friendlyName)
        : base(engine, justCreated) {
        _sets = sets;
        _nodeIdByValue = new(sets);
        _arrays = new();
        UniqueKey = uniqueKey;
        FriendlyName = friendlyName;
    }

    public string UniqueKey { get; }
    public string FriendlyName { get; }

    // also called in the background right after the store opens, so the first query
    // does not pay the full backend read (see DataStoreLocal.warmIndexesInBackground)
    void IIndexMirror.EnsureLoaded() => ensureLoaded();
    void ensureLoaded() {
        if (_loaded) return;
        lock (_loadLock) {
            if (_loaded) return;
            foreach (var kv in ReadAllPersisted()) addToMemory(kv.Key, kv.Value);
            _loaded = true;
        }
    }
    void addToMemory(int nodeId, T[] value) {
        _arrays.Add(nodeId, value);
        // dedup: the same element may occur several times in one node's array,
        // but the node must only be indexed once per unique value (and deindexed symmetrically)
        foreach (var e in value.Distinct()) _nodeIdByValue.Index(e, nodeId);
    }
    void removeFromMemory(int nodeId, T[] value) {
        _arrays.Remove(nodeId);
        foreach (var e in value.Distinct()) _nodeIdByValue.DeIndex(e, nodeId);
    }

    public void Add(int nodeId, object value) {
        ensureLoaded();
        var v = (T[])value;
        addToMemory(nodeId, v);
        PersistAdd(nodeId, v);
    }
    public void Remove(int nodeId, object value) {
        ensureLoaded();
        var v = (T[])value;
        removeFromMemory(nodeId, v);
        PersistRemove(nodeId);
    }
    public void RegisterAddDuringStateLoad(int nodeId, object value) => Add(nodeId, value);
    public void RegisterRemoveDuringStateLoad(int nodeId, object value) => Remove(nodeId, value);

    public IdSet Filter(IdSet set, IndexOperator op, T value) {
        // equality means "the array holds this element"; ordering operators have no meaning per array
        if (op != IndexOperator.Equal) throw new NotSupportedException(GetType().Name + " does not support the " + op.ToString().ToUpper() + " operator. ");
        ensureLoaded();
        return _nodeIdByValue.TryGetValueIdSet(value, out var ids) ? _sets.Intersection(set, ids) : IdSet.Empty;
    }
    public int CountEqual(IdSet set, T value) {
        ensureLoaded();
        if (_nodeIdByValue.TryGetValueIdSet(value, out var ids)) {
            return _sets.CountIntersection(set, ids);
        }
        return 0;
    }
    public bool ContainsValue(T value) {
        ensureLoaded();
        return _nodeIdByValue.ContainsValue(value);
    }
    public IEnumerable<T> GetUniqueValues() {
        ensureLoaded();
        return _nodeIdByValue.Values;
    }
    public int MaxCount(IndexOperator op, T value) {
        ensureLoaded();
        switch (op) {
            case IndexOperator.Equal:
                // real per-value count: the in-memory mirror maintains the id set per unique element, so this is O(1)
                return _nodeIdByValue.TryGetValueIdSet(value, out var ids) ? ids.Count : 0;
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
    public IdSet FilterInValues(IdSet set, List<T> values) {
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
    protected abstract IEnumerable<KeyValuePair<int, T[]>> ReadAllPersisted();
    /// <summary>Persist the mapping id → value. The id is never already present.</summary>
    protected abstract void PersistAdd(int nodeId, T[] value);
    /// <summary>Remove the persisted mapping for id.</summary>
    protected abstract void PersistRemove(int nodeId);
}
