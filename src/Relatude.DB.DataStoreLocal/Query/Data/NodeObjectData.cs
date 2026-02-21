using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Serialization;
using System.Text;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.Datamodels.Properties;

namespace Relatude.DB.Query.Data;

internal class NodeObjectData : IStoreNodeData {
    public double DurationMs { get; set; }
    INodeDataOuter _nodeData;
    Definition _def;
    Dictionary<string, Guid> _allPropertiesByName;
    DataStoreLocal _db;
    QueryContext _ctx;
    public IDataStore Store { get => _db; }
    public NodeObjectData(DataStoreLocal db, INodeDataOuter nodeData, Definition def, Dictionary<string, Guid> allPropertiesByName, QueryContext ctx) {
        _db = db;
        _nodeData = nodeData;
        _allPropertiesByName = allPropertiesByName;
        _ctx = ctx;
        _def = def;
    }
    public INodeDataOuter NodeData { get => _nodeData; }
    public int Count() {
        throw new NotImplementedException();
    }
    public ICollectionData Filter(bool[] keep) {
        throw new NotImplementedException();
    }
    public object? GetValue(string propertyName) {
        if (_allPropertiesByName.TryGetValue(propertyName, out var propertyId)) {
            if (_nodeData.TryGetValue(propertyId, out var value)) {
                if (_db.Logger.RecordingPropertyHits) _db.Logger.RecordPropertyHit(propertyId);
                return value;
            } else {
                var prop = _def.Datamodel.Properties[propertyId];
                if (prop is RelationPropertyModel rp) return getRelated(rp);
                throw new Exception($"Property {propertyName} not part of node object. ");
            }
        } else {
            var parts = propertyName.Split('.');
            if (parts.Length > 1) {
                var value = GetValue(parts[0]);
                var method = parts[1];
                if (method == "Count" || method == "Length") {
                    if (value is IEnumerable<object> e) return e.Cast<object>().Count();
                    throw new Exception("Count method can only be called on IEnumerable properties. ");
                }
            } else {
                // ID propertiy? // this should be more generalized later, what about the other named system props, should it be in datamodel? for faster lookup? ( enum ? )
                var typeDef = _def.Datamodel.NodeTypes[_nodeData.NodeType];
                if (propertyName == typeDef.NameOfPublicIdProperty) return _nodeData.Id;
                else if (propertyName == typeDef.NameOfInternalIdProperty) return _nodeData.__Id;
            }
            throw new Exception($"Property {propertyName} could not be evaluated. ");
        }
    }
    object? getRelated(RelationPropertyModel relProp) {
        var relation = _def.Relations[relProp.RelationId];
        var ids = relation.GetRelated(_nodeData.__Id, relProp.FromTargetToSource).ToArray();
        var tosInner = _db._nodes.Get(ids); // heavy operation
        var tos = _db.ToOuter(tosInner, _ctx).Select(n => new NodeDataWithRelations(n)).ToArray();
        var nodeTypes = _def.Datamodel.NodeTypes;
        var result = new NodeObjectData[tos.Length];
        for (var n = 0; n < tos.Length; n++) result[n] = new NodeObjectData(_db, tos[n], _def, _allPropertiesByName, _ctx);
        if (relProp.IsMany) return result;
        if (tos.Length == 0) return null;
        if (tos.Length == 1) return result[0];
        throw new Exception("Multiple relation on property " + relProp.CodeName + " for node " + _nodeData.__Id + " is not allowed.");
    }
    public ObjectData ToObjectData() {
        throw new NotImplementedException();
    }
    public void BuildTypeScriptTypeInfo(StringBuilder sb) {
        throw new NotImplementedException();
    }
}
