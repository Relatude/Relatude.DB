using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Stores;

internal class NodeTypesByIdsStore {
    readonly Definition _definition;
    readonly NodeTypesByIdsNoMeta _noMetas;
    readonly NodeTypesByIdsWithMeta _metas;
    internal NodeTypesByIdsStore(Definition definition) {
        _definition = definition;
        _noMetas = new(definition);
        _metas = new(definition);
    }
    bool involvesComplexTypes(NodeTypeModel nodeType, bool excludeDecendants) {
        if (excludeDecendants) return nodeType.IsComplex();
        return nodeType.ThisOrDescendingTypesAreComplex();
    }
    public IdSet GetAllNodeIdsForType(Guid typeId, QueryContext ctx) {
        var typeDef = _definition.NodeTypes[typeId].Model;
        var typesNoMetas = _noMetas.GetAllNodeIdsForType(typeId, ctx.ExcludeDecendants);
        if (involvesComplexTypes(typeDef, ctx.ExcludeDecendants)) {
            var typesWithMetas = _metas.GetAllNodeIdsForType(typeDef, ctx);
            return _definition.Sets.Union(typesWithMetas, typesNoMetas);
        } else {
            return typesNoMetas;
        }
    }
    public IdSet GetAllNodeIdsForTypeNoAccessControl(Guid typeId, bool excludeDecendants) {
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
        return GetAllNodeIdsForTypeNoAccessControl(typeId, true).Count;
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
    public void RegisterActionDuringStateLoad(PrimitiveNodeAction na, bool throwOnErrors, Action<string, Exception> log) {
        var node = na.Node;
        try {
            switch (na.Operation) {
                case PrimitiveOperation.Add: insert(node); break;
                case PrimitiveOperation.Remove: delete(node); break;
                default: break;
            }
        } catch (Exception ex) {
            var msg = "Error registering action during index type state load for node id: " + node.__Id + " operation: " + na.Operation + " . Error: " + ex.Message;
            log(msg, ex);
            if (throwOnErrors) throw new Exception(msg, ex);
        }
    }
    public void Index(INodeData node) => insert(node);
    public void DeIndex(INodeData node) => delete(node);

    void insert(INodeData node) {
        var nodeType = _definition.Datamodel.NodeTypes[node.NodeType];
        if (nodeType.IsComplex()) {
            if (node is not INodeDataComplex cmx) throw new Exception("Internal error. Unable to index node with id: " + node.Id + " as complex node data is required.");
            _metas.Insert(cmx, nodeType);
        } else {
            if (node is not INodeDataComplex) throw new Exception("Internal error. Unable to index node with id: " + node.Id + " as non-complex node data is required.");
            _noMetas.Insert(node, nodeType);
        }
    }
    void delete(INodeData node) {
        var nodeType = _definition.Datamodel.NodeTypes[node.NodeType];
        if (nodeType.IsComplex()) {
            if (node is not INodeDataComplex cmx) throw new Exception("Internal error. Unable to index node with id: " + node.Id + " as complex node data is required.");
            _metas.Delete(cmx, nodeType);
        } else {
            if (node is INodeDataComplex) throw new Exception("Internal error. Unable to index node with id: " + node.Id + " as non-complex node data is required.");
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
