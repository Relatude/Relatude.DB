using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Stores;
internal class NodeTypesByIds {
    readonly Definition _definition;
    readonly NodeTypesByIdsNoMeta _noMetas;
    readonly NodeTypesByIdsWithMeta _metas;
    internal NodeTypesByIds(Definition definition) {
        _definition = definition;
        _noMetas = new(definition);
        _metas = new(definition);
    }
    bool involvesComplexTypes(NodeTypeModel nodeType, bool excludeDecendants) {
        if (excludeDecendants) return nodeType.IsComplex();
        return nodeType.ThisOrDescendingTypesAreComplex();
    }
    public IdSet GetAllNodeIdsForTypeFilteredByContext(Guid typeId, QueryContext ctx) {
        var typeDef = _definition.NodeTypes[typeId].Model;
        var typesNoMetas = _noMetas.GetAllNodeIdsForType(typeId, ctx.ExcludeDecendants);
        if (involvesComplexTypes(typeDef, ctx.ExcludeDecendants)) {
            var typesWithMetas = _metas.GetAllNodeIdsForType(typeDef, ctx);
            return _definition.Sets.Union(typesWithMetas, typesNoMetas);
        } else {
            return typesNoMetas;
        }
    }
    public IdSet GetAllNodeIdsForTypeNoFilter(Guid typeId, bool excludeDecendants) {
        var typeDef = _definition.NodeTypes[typeId].Model;
        var typesNoMetas = _noMetas.GetAllNodeIdsForType(typeId, excludeDecendants);
        if (involvesComplexTypes(typeDef, excludeDecendants)) {
            var typesWithMetas = _metas.GetAllNodeIdsForTypeNoAccessControl(typeDef, excludeDecendants);
            return _definition.Sets.Union(typesWithMetas, typesNoMetas);
        } else {
            return typesNoMetas;
        }
    }
    public int GetCountForTypeForStatusInfo(Guid typeId) {
        // Optimization possible!
        return GetAllNodeIdsForTypeNoFilter(typeId, true).Count;
    }
    public Guid GetType(int id) {
        Guid typeId;
        if (_noMetas.TryGetType(id, out typeId)) return typeId;
        if (_metas.TryGetType(id, out typeId)) return typeId;
        throw new Exception("Internal error. Unable to determine type of unknown node with id: " + id);
    }
    public bool TryGetType(int id, out Guid typeId) {
        if (_noMetas.TryGetType(id, out typeId)) return true;
        if (_metas.TryGetType(id, out typeId)) return true;
        return false;
    }
    public void RegisterActionDuringStateLoad(PrimitiveNodeAction na, bool throwOnErrors, Action<string, Exception?> log) {
        var node = na.Node;
        try {
            switch (na.Operation) {
                case PrimitiveOperation.Add: Index(node); break;
                case PrimitiveOperation.Remove: DeIndex(node); break;
                default: break;
            }
        } catch (Exception ex) {
            var msg = "Error registering action during index type state load for node id: " + node.__Id + " operation: " + na.Operation + " . Error: " + ex.Message;
            log(msg, ex);
            if (throwOnErrors) throw new Exception(msg, ex);
        }
    }
    public void Index(INodeData node) {
        var nodeType = _definition.Datamodel.NodeTypes[node.NodeType];
        if (nodeType.IsComplex()) {
            _metas.Insert(node, nodeType);
        } else {
            _noMetas.Insert(node, nodeType);
        }
    }
    public void DeIndex(INodeData node) {
        var nodeType = _definition.Datamodel.NodeTypes[node.NodeType];
        if (nodeType.IsComplex()) {
            _metas.Delete(node, nodeType);
        } else {
            _noMetas.Delete(node, nodeType);
        }
    }
    public void SaveState(IAppendStream stream) {
        _noMetas.SaveState(stream);
        _metas.SaveState(stream);
    }
    public void ReadState(IReadStream stream) {
        _noMetas.ReadState(stream);
        _metas.ReadState(stream);
    }
}
