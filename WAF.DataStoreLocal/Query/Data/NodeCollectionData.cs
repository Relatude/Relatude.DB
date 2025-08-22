using WAF.Datamodels;
using WAF.Datamodels.Properties;
using WAF.DataStores;
using WAF.DataStores.Definitions;
using WAF.DataStores.Sets;
using WAF.Serialization;
using System.Text;
namespace WAF.Query.Data;
internal partial class NodeCollectionData : IStoreNodeDataCollection, IFacetSource {

    IdSet _ids;
    DataStoreLocal _db;
    Definition _def;
    NodeType _nodeType;
    INodeData[]? _nodes;
    Metrics _metrics;

    public DataType DataType => DataType.IStoreNodeDataCollection;
    public NodeCollectionData(DataStoreLocal db, Metrics metrics, IdSet ids, NodeType nodeType, List<IncludeBranch>? includeBranch, int? totalCount = null, int pageIndexUsed = 0, int? pageSizeUsed = null) {
        _db = db;
        _metrics = metrics;
        _def = db._definition;
        _ids = ids;
        _includeBranches = includeBranch;
        _nodeType = nodeType;
        TotalCount = totalCount == null ? _ids.Count : totalCount.Value;
        PageIndexUsed = pageIndexUsed;
        PageSizeUsed = pageSizeUsed;
    }
    public int Count => _ids.Count;
    public IEnumerable<object> Values {
        get {
            var allpropertyIdsByName = _def.Datamodel.NodeTypes[_nodeType.Id].AllPropertyIdsByName;
            foreach (var node in NodeValues) {
                yield return new NodeObjectData(_db, node, _def, allpropertyIdsByName);
            }
        }
    }

    public IEnumerable<INodeData> NodeValues {
        get {
            if (_nodes == null) _nodes = IncludeUtil.GetNodesWithIncludes(_metrics, _ids, _db, _includeBranches);
            return _nodes;
        }
    }
    public IEnumerable<int> NodeIds {
        get {
            return _ids.Enumerate();
        }
    }
    public IEnumerable<Guid> NodeGuids {
        get {
            foreach (var id in _ids.Enumerate()) {
                yield return _db._guids.GetGuid(id);
            }
        }
    }
    public int TotalCount { get; }
    public int PageIndexUsed { get; }
    public int? PageSizeUsed { get; }
    public double DurationMs { get; set; }
    public object Evaluate(IVariables vars) {
        throw new NotImplementedException();
    }
    public ICollectionData Filter(bool[] mask) {
        var ids = new HashSet<int>();
        var i = 0;
        foreach (var id in _ids.Enumerate()) {
            if (mask[i++]) ids.Add(id);
        }
        var newIds = IdSet.UncachableSet(ids); // It is not possible to cache the result of a filter
        return new NodeCollectionData(_db, _metrics, newIds, _nodeType, _includeBranches, newIds.Count);
    }
    public ObjectData ToObjectCollection() {
        throw new NotImplementedException();
    }
    public ICollectionData Page(int pageIndex, int pageSize) {
        var newIds = _def.Sets.Page(_ids, pageIndex, pageSize);
        return new NodeCollectionData(_db, _metrics, newIds, _nodeType, _includeBranches, TotalCount, pageIndex, pageSize);
    }
    public ICollectionData Skip(int skip) {
        var newIds = _def.Sets.Skip(_ids, skip);
        return new NodeCollectionData(_db, _metrics, newIds, _nodeType, _includeBranches, TotalCount, 0, null);
    }
    public ICollectionData Take(int take) {
        var newIds = _def.Sets.Take(_ids, take);
        return new NodeCollectionData(_db, _metrics, newIds, _nodeType, _includeBranches, TotalCount, 0, take);
    }
    public void BuildTypeScriptTypeInfo(StringBuilder sb) {
        throw new NotImplementedException();
    }
    public PropertyType GetPropertyType(string name) {
        throw new NotImplementedException();
    }
    public bool TryOrderByIndexes(string propertyName, bool descending) {
        var prop = _def.NodeTypes[_nodeType.Id].AllPropertiesByName[propertyName];
        if (!prop.TryReorder(_ids, descending, out var sorted)) return false;
        _ids = sorted;
        return true;
    }
    public ICollectionData ReOrder(IEnumerable<int> newPos) {
        //newPos = newPos.ToArray();
        var oldOrder = _ids.ToArray();
        var newIds = new int[_ids.Count];
        var i = 0;
        foreach (var pos in newPos) newIds[i++] = oldOrder[pos];
        var orderedSet = new FixedOrderedSet(newIds, _ids.Count);
        _ids = IdSet.UncachableSet(orderedSet);
        //if(_includeBranches!=null) foreach(var b in _includeBranches) b.Reset();
        if (_nodes != null) {
            var newNodeDatas = new INodeData[_nodes.Length];
            i = 0;
            foreach (var pos in newPos) newNodeDatas[i++] = _nodes[pos];
            _nodes = newNodeDatas;
        }

        return this;
    }
}

