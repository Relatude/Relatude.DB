using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Stores;

class ids{
    readonly StateIdTracker _state = new();
    public long StateId { get => _state.Current; }
    HashSet<int> _ids = [];
    IdSet? _lastSet;
    object _lock = new();
    public void Index(int id) {
        _ids.Add(id);
        _state.RegisterAddition(id);
        _lastSet = null;
    }
    public void DeIndex(int id) {
        _ids.Remove(id);
        _state.RegisterRemoval(id);
        _lastSet = null;
    }
    public int Count => _ids.Count;
    public IdSet AsUnmutableIdSet() {
        if (_lastSet != null) return _lastSet;
        return _lastSet ??= new(_ids, _state.Current);
    }
}

internal class NodeIds {
    readonly Definition _definition;

    Dictionary<int, NodeMeta[]> _nodeMetasById = new();    
    Dictionary<QueryContextKey, ids> _indexedNodeIdsByContext = new();

    internal NodeIds(Definition definition) {
        _definition = definition;
    }
    ids evaluateRelevantIds(Guid typeId, QueryContext ctx) { 
        throw new NotImplementedException();
    }
    public IdSet GetAllNodeIdsForTypeFilteredByContext(Guid typeId, QueryContext ctx) {
         throw new NotImplementedException();
    }
    public IdSet GetAllNodeIdsForTypeNoFilter(Guid typeId, bool excludeDecendants) {
        throw new NotImplementedException();
    }
    public int GetCountForTypeForStatusInfo(Guid typeId) {
        throw new NotImplementedException();
    }
    public Guid GetType(int id) {
        throw new NotImplementedException();
    }
    public bool TryGetType(int id, out Guid typeId) {
        throw new NotImplementedException();
    }
    public void RegisterActionDuringStateLoad(PrimitiveNodeAction na, bool throwOnErrors, Action<string, Exception?> log) {
    }
    public void Index(INodeData node) {
    }
    public void DeIndex(INodeData node) {
    }
    public void SaveState(IAppendStream stream) {
    }
    public void ReadState(IReadStream stream) {
    }
}
