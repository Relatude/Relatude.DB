using WAF.Common;
using WAF.DataStores.Indexes;
using WAF.DataStores.Definitions;
using WAF.DataStores.Sets;
using WAF.IO;
namespace WAF.DataStores.Indexes;
public class StringArrayIndex : IIndex {
    readonly IdByValue<string> _nodeIdByValue;
    readonly Dictionary<int, string[]> _valueByNodeId;
    readonly SetRegister _sets;
    internal StringArrayIndex(Definition def, string uniqueKey, Guid propertyId) {
        _nodeIdByValue = new(def.Sets);
        _valueByNodeId = new();
        UniqueKey = uniqueKey;
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
    public void RegisterAddDuringStateLoad(int nodeId, object value, long timestampId) => Add(nodeId, value);
    public void RegisterRemoveDuringStateLoad(int nodeId, object value, long timestampId) => Remove(nodeId, value);
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
    public void SaveState(IAppendStream stream) {
        stream.WriteVerifiedInt(_valueByNodeId.Count);
        foreach (var kv in _valueByNodeId) {
            stream.WriteUInt((uint)kv.Key);
            stream.WriteStringArray(kv.Value);
        }
    }
    public void ReadState(IReadStream stream) {
        var count_valueByNodeId = stream.ReadVerifiedInt();
        for (var i = 0; i < count_valueByNodeId; i++) {
            var k = (int)stream.ReadUInt();
            var v = stream.ReadStringArray();
            Add(k, v);
        }
    }
    public void CompressMemory() {
    }
    public void Dispose() { }
    public void ClearCache() { }
}
