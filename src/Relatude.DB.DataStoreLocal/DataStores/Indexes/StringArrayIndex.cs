using Relatude.DB.Common;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Indexes;

public class StringArrayIndex : IIndex {
    readonly IdByValue<string> _nodeIdByValue;
    readonly Dictionary<int, string[]> _valueByNodeId;
    readonly SetRegister _sets;
    readonly IIOProvider _io;
    readonly FileKeyUtility _fileKeys;
    internal StringArrayIndex(Definition def, string uniqueKey, string freindlyName, IIOProvider io, FileKeyUtility fileKey, Guid propertyId) {
        _nodeIdByValue = new(def.Sets);
        _valueByNodeId = new();
        UniqueKey = uniqueKey;
        FriendlyName = freindlyName;
        _io = io;
        _fileKeys = fileKey;
        _sets = def.Sets;
    }
    public string UniqueKey { get; private set; }
    public IdSet Filter(IdSet set, IndexOperator op, string value) {
        //var matches = _nodeIdByValue.Get(value);
        //if (op == IndexOperator.Equal) return set.Intersection(matches);
        //if (op == IndexOperator.NotEqual) return set.DisjunctiveUnion(matches);
        throw new NotSupportedException("The string index does not support the " + op.ToString().ToUpper() + " operator. ");
    }
    public int CountEqual(IdSet set, string value) {
        if (_nodeIdByValue.TryGetValueIdSet(value, out var ids)) {
            return _sets.CountIntersection(set, ids);
        }
        return 0;
    }
    public void Add(int nodeId, object value) {
        var v = (string[])value;
        _valueByNodeId.Add(nodeId, v);
        foreach (var str in v) _nodeIdByValue.Index(str, nodeId);
    }
    public void Remove(int nodeId, object value) {
        var v = (string[])value;
        _valueByNodeId.Remove(nodeId);
        foreach (var str in v) _nodeIdByValue.DeIndex(str, nodeId);
    }
    public void RegisterAddDuringStateLoad(int nodeId, object value) => Add(nodeId, value);
    public void RegisterRemoveDuringStateLoad(int nodeId, object value) => Remove(nodeId, value);
    public bool ContainsValue(string value) => _nodeIdByValue.ContainsValue(value);
    public IEnumerable<string> GetUniqueValues() {
        return _nodeIdByValue.Values;
    }
    public int MaxCount(IndexOperator op, string value) {
        switch (op) {
            case IndexOperator.Equal:
                return 1;
            case IndexOperator.NotEqual:
            case IndexOperator.Greater:
            case IndexOperator.Smaller:
            case IndexOperator.GreaterOrEqual:
            case IndexOperator.SmallerOrEqual:
                return _valueByNodeId.Count;
            default: break;
        }
        throw new NotSupportedException(GetType().Name + " types does not support the " + op.ToString().ToUpper() + " operator. ");
    }
    public IdSet FilterInValues(IdSet set, List<string> values) {
        var result = set;
        List<IdSet> matches = [];
        foreach (var value in values) {
            if (_nodeIdByValue.TryGetValueIdSet(value, out var ids)) {
                var matchForOneValue = _sets.Intersection(set, ids);
                if (matchForOneValue.Count > 0) matches.Add(matchForOneValue);
            }
        }
        return _sets.Union(matches);

        //bool evalByValue = _nodeIdByValue.Count < 1000;
        //if (evalByValue) { // by value, faster if few unique values in total
        //    List<DerivedIdSet> matches = new();
        //    foreach (var value in values) {
        //        if (_nodeIdByValue.TryGetValue(value, out var nodeIds)) {
        //            matches.Add(SetOperations.Intersection(set, nodeIds));
        //        }
        //    }
        //    return SetOperations.Union(matches);
        //} else { // by id, faster if many unique values, but not too many values selected
        //    HashSet<int> ids = new();
        //    foreach (var nodeId in set) {
        //        var v = _valueByNodeId[nodeId];
        //        foreach (var value in values) {
        //            if (v == value) {
        //                ids.Add(nodeId);
        //                break;
        //            }
        //        }
        //    }
        //    return new(ids);
        //}
    }
    public void WriteNewTimestampDueToRewriteHotswap(long newTimestamp, Guid walFileId) {
        var fileName = _fileKeys.Index_GetFileKey(UniqueKey);
        using var stream = _io.OpenAppend(fileName);
        stream.WriteVerifiedLong(newTimestamp);
        stream.WriteGuid(walFileId);
        PersistedTimestamp = newTimestamp;
    }
    public void SaveStateForMemoryIndexes(long logTimestamp, Guid walFileId) {
        var fileName = _fileKeys.Index_GetFileKey(UniqueKey);
        _io.DeleteIfItExists(fileName); // could be optimized to keep old file
        using var stream = _io.OpenAppend(fileName);
        stream.WriteVerifiedInt(_valueByNodeId.Count);
        foreach (var kv in _valueByNodeId) {
            stream.WriteUInt((uint)kv.Key);
            stream.WriteStringArray(kv.Value);
        }
        stream.WriteVerifiedLong(logTimestamp);
        stream.WriteGuid(walFileId);
        PersistedTimestamp = logTimestamp;
    }
    public void ReadStateForMemoryIndexes(Guid walFileId) {
        PersistedTimestamp = 0;
        var fileName = _fileKeys.Index_GetFileKey(UniqueKey);
        if (_io.DoesNotExistsOrIsEmpty(fileName)) return;
        using var stream = _io.OpenRead(fileName, 0);
        var count_valueByNodeId = stream.ReadVerifiedInt();
        for (var i = 0; i < count_valueByNodeId; i++) {
            var k = (int)stream.ReadUInt();
            var v = stream.ReadStringArray();
            Add(k, v);
        }
        Guid walId = Guid.Empty;
        while (stream.More()) {
            PersistedTimestamp = stream.ReadVerifiedLong();
            walId = stream.ReadGuid();
        }
        if (walId != walFileId) throw new Exception("WAL file ID mismatch when reading index state. ");
    }
    public void CompressMemory() { }
    public void Dispose() { }
    public void ClearCache() { }
    public long PersistedTimestamp { get; set; }
    public string FriendlyName { get; }
}
