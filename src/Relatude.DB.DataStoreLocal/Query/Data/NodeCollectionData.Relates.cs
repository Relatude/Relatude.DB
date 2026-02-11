using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Definitions.PropertyTypes;
using Relatude.DB.DataStores.Sets;

namespace Relatude.DB.Query.Data;
internal partial class NodeCollectionData : IStoreNodeDataCollection, IFacetSource {
    public IStoreNodeDataCollection Relates(Guid propertyId, Guid toNodeGuid) {
        var property = _def.Datamodel.Properties[propertyId];
        if (property is not RelationPropertyModel relProp) throw new ArgumentException("Property is not a relation property");
        var relation = _def.Relations[relProp.RelationId];
        IdSet ids = relates(_ids, relation, relProp, toNodeGuid);
        return new NodeCollectionData(_db, _ctx, _metrics, ids, _nodeType, _includeBranches);
    }
    public IStoreNodeDataCollection RelatesNot(Guid propertyId, Guid toNodeGuid) {
        var property = _def.Datamodel.Properties[propertyId];
        if (property is not RelationPropertyModel relProp) throw new ArgumentException("Property is not a relation property");
        var relation = _def.Relations[relProp.RelationId];
        IdSet ids = relatesNot(_ids, relation, relProp, toNodeGuid);
        return new NodeCollectionData(_db, _ctx, _metrics, ids, _nodeType, _includeBranches);
    }
    public IStoreNodeDataCollection RelatesAny(Guid propertyId, IEnumerable<Guid> toNodeGuid) {
        var property = _def.Datamodel.Properties[propertyId];
        if (property is not RelationPropertyModel relProp) throw new ArgumentException("Property is not a relation property");
        var relation = _def.Relations[relProp.RelationId];
        var ids = IdSet.Empty;
        foreach (var toGuid in toNodeGuid) {
            ids = _def.Sets.Union(ids, relates(_ids, relation, relProp, toGuid));
            //ids = ids.Union(relates(_ids, relation, relProp, toGuid));
        }        
        return new NodeCollectionData(_db, _ctx, _metrics, ids, _nodeType, _includeBranches);
    }
    IdSet relates(IdSet currentSet, Relation relation, RelationPropertyModel relProp, Guid toNodeGuid) {
        if (!_db._guids.TryGetId(toNodeGuid, out var toNodeId)) return IdSet.Empty;
        var possibleFromRelSet = relation.GetRelated(toNodeId, !relProp.FromTargetToSource);
        return _def.Sets.Intersection(currentSet, possibleFromRelSet);
    }
    IdSet relatesNot(IdSet currentSet, Relation relation, RelationPropertyModel relProp, Guid toNodeGuid) {
        if (!_db._guids.TryGetId(toNodeGuid, out var toNodeId)) return IdSet.Empty;
        var possibleFromRelSet = relation.GetRelated(toNodeId, !relProp.FromTargetToSource);
        return _def.Sets.Difference(currentSet, possibleFromRelSet);
    }

}


