using Relatude.DB.Common;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Indexes;

/// <summary>
/// In-memory index over array properties, generic over the element type. Subclasses only supply
/// the element-specific state-file serialization (<see cref="WriteArray"/>/<see cref="ReadArray"/>).
/// </summary>
public abstract class ValueArrayIndexBase<T> : IValueArrayIndex<T> where T : notnull {
    readonly IdByValue<T> _nodeIdByValue;
    // node arrays are normalized into a reference counted intern table (see ValueArrayInternTable):
    // typically a handful of value combinations are shared by millions of nodes
    readonly ValueArrayInternTable<T> _arrays = new();
    readonly SetRegister _sets;
    readonly IIOProvider _io;
    readonly FileKeyUtility _fileKeys;
    // true whenever the in-memory state may differ from the persisted body; starts true so an
    // index that was never persisted is never re-stamped as if it were (see WriteNewTimestampDueToRewriteHotswap)
    bool _changedSinceLastSave = true;
    internal ValueArrayIndexBase(Definition def, string uniqueKey, string freindlyName, IIOProvider io, FileKeyUtility fileKey) {
        _nodeIdByValue = new(def.Sets);
        UniqueKey = uniqueKey;
        FriendlyName = freindlyName;
        _io = io;
        _fileKeys = fileKey;
        _sets = def.Sets;
    }
    public string UniqueKey { get; private set; }
    public IdSet Filter(IdSet set, IndexOperator op, T value) {
        // equality means "the array holds this element"; ordering operators have no meaning per array
        if (op != IndexOperator.Equal) throw new NotSupportedException(GetType().Name + " does not support the " + op.ToString().ToUpper() + " operator. ");
        return _nodeIdByValue.TryGetValueIdSet(value, out var ids) ? _sets.Intersection(set, ids) : IdSet.Empty;
    }
    public int CountEqual(IdSet set, T value) {
        if (_nodeIdByValue.TryGetValueIdSet(value, out var ids)) {
            return _sets.CountIntersection(set, ids);
        }
        return 0;
    }
    public void Add(int nodeId, object value) {
        _changedSinceLastSave = true;
        var v = (T[])value;
        _arrays.Add(nodeId, v);
        // dedup: the same element may occur several times in one node's array,
        // but the node must only be indexed once per unique value (and deindexed symmetrically)
        foreach (var e in v.Distinct()) _nodeIdByValue.Index(e, nodeId);
    }
    public void Remove(int nodeId, object value) {
        _changedSinceLastSave = true;
        var v = (T[])value;
        _arrays.Remove(nodeId);
        foreach (var e in v.Distinct()) _nodeIdByValue.DeIndex(e, nodeId);
    }
    public void RegisterAddDuringStateLoad(int nodeId, object value) => Add(nodeId, value);
    public void RegisterRemoveDuringStateLoad(int nodeId, object value) => Remove(nodeId, value);
    public bool ContainsValue(T value) => _nodeIdByValue.ContainsValue(value);
    public IEnumerable<T> GetUniqueValues() {
        return _nodeIdByValue.Values;
    }
    public int MaxCount(IndexOperator op, T value) {
        switch (op) {
            case IndexOperator.Equal:
                // real per-value count: the id set per unique element is maintained, so this is O(1)
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
        List<IdSet> matches = [];
        foreach (var value in values) {
            if (_nodeIdByValue.TryGetValueIdSet(value, out var ids)) {
                var matchForOneValue = _sets.Intersection(set, ids);
                if (matchForOneValue.Count > 0) matches.Add(matchForOneValue);
            }
        }
        return _sets.Union(matches);
    }
    public void WriteNewTimestampDueToRewriteHotswap(long newTimestamp, Guid walFileId) {
        // appending a stamp is only sound when the persisted body equals the in-memory state: the
        // stamp is trusted on the next open, so changes missing from a stale body would be skipped
        // by the log replay and silently lost — and a never-persisted index would get a body-less,
        // unreadable file. Persist the full state whenever the body may be behind:
        if (_changedSinceLastSave) {
            SaveStateForMemoryIndexes(newTimestamp, walFileId);
            return;
        }
        var fileName = _fileKeys.Index_GetFileKey(UniqueKey);
        using var stream = _io.OpenAppend(fileName);
        stream.WriteVerifiedLong(newTimestamp);
        stream.WriteGuid(walFileId);
        PersistedTimestamp = newTimestamp;
    }
    public void SaveStateForMemoryIndexes(long logTimestamp, Guid walFileId) {
        var fileName = _fileKeys.Index_GetFileKey(UniqueKey);
        _io.DeleteFileIfItExists(fileName); // could be optimized to keep old file
        using var stream = _io.OpenAppend(fileName);
        // same on-disk format as before normalization (one nodeId + array per node), so existing
        // index files stay readable and no migration is needed; ReadState re-interns on load
        stream.WriteVerifiedInt(_arrays.Count);
        foreach (var (nodeId, array) in _arrays.All) {
            stream.WriteUInt((uint)nodeId);
            WriteArray(stream, array);
        }
        stream.WriteVerifiedLong(logTimestamp);
        stream.WriteGuid(walFileId);
        PersistedTimestamp = logTimestamp;
        _changedSinceLastSave = false;
    }
    public void ReadStateForMemoryIndexes(Guid walFileId) {
        PersistedTimestamp = 0;
        var fileName = _fileKeys.Index_GetFileKey(UniqueKey);
        if (_io.DoesNotExistsOrIsEmpty(fileName)) return;
        using var stream = _io.OpenRead(fileName, 0);
        var count_valueByNodeId = stream.ReadVerifiedInt();
        for (var i = 0; i < count_valueByNodeId; i++) {
            var k = (int)stream.ReadUInt();
            var v = ReadArray(stream);
            Add(k, v);
        }
        Guid walId = Guid.Empty;
        while (stream.More()) {
            PersistedTimestamp = stream.ReadVerifiedLong();
            walId = stream.ReadGuid();
        }
        if (walId != walFileId) throw new Exception("WAL file ID mismatch when reading index state. ");
        _changedSinceLastSave = false; // memory now equals the body just read
    }
    public void CompressMemory() { }
    public void Dispose() { }
    public void ClearCache() { }
    public long PersistedTimestamp { get; private set; }
    public void FlagFirstCommit() { }
    public string FriendlyName { get; }

    // ---- element-specific state-file serialization ----
    protected abstract void WriteArray(IAppendStream stream, T[] array);
    protected abstract T[] ReadArray(IReadStream stream);
}
