using Relatude.DB.DataStores.Sets;

namespace Relatude.DB.Query.Data;
internal partial class NodeCollectionData : IStoreNodeDataCollection, IFacetSource {
    public IStoreNodeDataCollection WhereIn(Guid propertyId, IEnumerable<object?> values) {
        var property = _def.Properties[propertyId];
        IdSet idset;
        if (property.ValueIndex == null) {
            // slow without index
            HashSet<int> ids = new();
            foreach (var id in _ids.Enumerate()) {
                var node = _db._nodes.Get(id);
                if (node.TryGetValue(propertyId, out var value)) {
                    if (_db.Logger.RecordingPropertyHits) _db.Logger.RecordPropertyHit(propertyId);
                    foreach (var v in values) {
                        if (value.Equals(v)) {
                            ids.Add(id);
                        }
                    }
                }
            }
            idset = IdSet.UncachableSet(ids);
        } else {
            idset = property.WhereIn(_ids, values);
        }
        return new NodeCollectionData(_db, _metrics, idset, _nodeType, _includeBranches);
    }
    public IStoreNodeDataCollection WhereInIds(IEnumerable<Guid> values) {
        // room for optimization....
        HashSet<int> set = new();
        foreach (var value in values) {
            if (_db._guids.TryGetId(value, out var id)) {
                set.Add(id);
            }
        }
        var idSet = IdSet.UncachableSet(set);  // not cachable....
        var newSet = _db._definition.Sets.Intersection(_ids, idSet);
        return new NodeCollectionData(_db, _metrics, newSet, _nodeType, _includeBranches);
    }

}