using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Sets;

namespace Relatude.DB.Query.Data;
internal partial class NodeCollectionData : IGraphCollection {
    public IStoreNodeDataCollection Traverse(Guid propertyId, int minLevel, int maxLevel, GraphDirection direction, int? maxVisited) {
        var relProp = getRelationPropertyAndValidate(propertyId, direction, out var relation);
        var reached = _def.Sets.TraverseRelation(_ids, relation, relProp.FromTargetToSource, direction, minLevel, maxLevel, maxVisited ?? GraphTraversalUtils.DefaultMaxVisited);
        // scope the result to the related node type filtered by the query context;
        // one intersection handles access control, culture and dropping off-type nodes
        // reached over relations with multiple end types:
        var relatedTypeId = relProp.NodeTypeOfRelated;
        var ids = _def.Sets.Intersection(reached, _def.GetAllIdsForType(relatedTypeId, _ctx));
        var nodeType = _def.NodeTypes[relatedTypeId];
        return new NodeCollectionData(_db, _ctx, _metrics, ids, nodeType, _includeBranches);
    }
    public IGraphPathResultData ShortestPath(Guid propertyId, Guid fromNodeGuid, Guid toNodeGuid, int maxLevel, GraphDirection direction, int? maxVisited) {
        var relProp = getRelationPropertyAndValidate(propertyId, direction, out var relation);
        if (!_db._guids.TryGetId(fromNodeGuid, out var fromId) || !_db._guids.TryGetId(toNodeGuid, out var toId)) {
            return new GraphPathResultData(_db, _ctx, []); // unknown node ids: no path, consistent with Relates on unknown ids
        }
        var path = GraphTraversalUtils.TryShortestPath(fromId, toId, relation, relProp.FromTargetToSource, direction, maxLevel, maxVisited ?? GraphTraversalUtils.DefaultMaxVisited);
        return new GraphPathResultData(_db, _ctx, path ?? []);
    }
    RelationPropertyModel getRelationPropertyAndValidate(Guid propertyId, GraphDirection direction, out Relation relation) {
        var property = _def.Datamodel.Properties[propertyId];
        if (property is not RelationPropertyModel relProp) throw new ArgumentException("Property is not a relation property");
        relation = _def.Relations[relProp.RelationId];
        if (direction == GraphDirection.Both && !relation.IsSymmetric && !relation.AllSourceTypes.Overlaps(relation.AllTargetTypes)) {
            throw new ArgumentException("GraphDirection.Both is only supported on relations where source and target types overlap (self relations). For cross-type patterns, chain multiple Traverse calls instead. ");
        }
        return relProp;
    }
}
