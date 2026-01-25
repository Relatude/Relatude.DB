using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
internal class NodeTypesByIdsWithMeta {
    readonly Definition _definition;
    Dictionary<int, NodeMeta[]> _metaById = [];
    public NodeTypesByIdsWithMeta(Definition definition) {
        _definition = definition;
    }
    internal void Insert(INodeData node, NodeTypeModel nodeType) {
        if (node is NodeDataVersionsContainer cont) {
            _metaById[cont.__Id] = [.. cont.Versions.Select(v => v.Meta!)];
        } else if (node is NodeData nd) {
            
        } else {
            throw new NotImplementedException();
        }
    }
    internal void Delete(INodeData node, NodeTypeModel nodeType) {
        _metaById.Remove(node.__Id);
    }
    internal IdSet GetAllNodeIdsForType(NodeTypeModel typeDef, QueryContext ctx) {
        throw new NotImplementedException();
    }
    internal IdSet GetAllNodeIdsForTypeNoAccessControl(NodeTypeModel typeDef, bool excludeDecendants) {

        throw new NotImplementedException();
    }
    internal bool TryGetType(int id, out Guid typeId) {
        throw new NotImplementedException();
    }
    internal void SaveState(IAppendStream stream) {
        // throw new NotImplementedException();
    }
    internal void ReadState(IReadStream stream) {
        // throw new NotImplementedException();
    }
}
