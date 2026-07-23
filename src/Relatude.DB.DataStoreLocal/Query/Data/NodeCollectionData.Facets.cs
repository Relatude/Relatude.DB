using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Sets;

namespace Relatude.DB.Query.Data {
    internal partial class NodeCollectionData : IStoreNodeDataCollection, IFacetSource {
        public Datamodel Datamodel { get => _def.Datamodel; }
        public Dictionary<Guid, Facets> EvaluateFacetsAndFilter(Dictionary<Guid, Facets> givenById, Dictionary<Guid, Facets> selection, out IFacetSource filteredSource, int pageIndex, int? pageSize, QueryContext ctx) {
            var ids = _ids;
            var relevantProps = findRelevantProperties(givenById, true, _def, ids).ToList();
            var result = new Dictionary<Guid, Facets>();
            var innerSet = ids;
            var specialSetsForSelectedFacets = new Dictionary<Guid, IdSet>();
            var propsWithSelection = new Dictionary<Guid, Property>();
            foreach (var prop in relevantProps) {
                var facets = prop.GetDefaultFacets(givenById.TryGetValue(prop.Id, out var g) ? g : null, ctx);
                facets.Sort();
                result.Add(prop.Id, facets);
                if (selection.TryGetValue(prop.Id, out var selected))
                    facets.SetSelected(selected.HasValues() ? selected.Values : null);
                if (facets.HasSelected()) {
                    innerSet = prop.FilterFacets(facets, innerSet, ctx);
                    propsWithSelection.Add(prop.Id, prop);
                }
            }
            // drill-sideways sets and per-property counting are independent of each other, so on
            // large sources they run on all cores. Safe because everything they touch is either
            // immutable snapshot state (writers are blocked by the store's read lock for the whole
            // query) or the lock-guarded set/aggregate caches, and each job writes only its own
            // property's Facets. Small sources stay sequential - their work is microseconds and
            // the parallel overhead would only add latency:
            var parallel = ids.Count >= 262_144;
            IdSet sidewaysSet(Property prop) { // all OTHER selections applied, so the facet's own alternatives stay visible
                var specialSet = ids;
                foreach (var otherProp in propsWithSelection.Values) {
                    if (otherProp.Id == prop.Id) continue;
                    specialSet = otherProp.FilterFacets(result[otherProp.Id], specialSet, ctx);
                }
                return specialSet;
            }
            var selectedProps = propsWithSelection.Values.ToList();
            if (parallel && selectedProps.Count > 1) {
                var sets = new IdSet[selectedProps.Count];
                Parallel.For(0, selectedProps.Count, i => sets[i] = sidewaysSet(selectedProps[i]));
                for (var i = 0; i < selectedProps.Count; i++) specialSetsForSelectedFacets.Add(selectedProps[i].Id, sets[i]);
            } else {
                foreach (var prop in selectedProps) specialSetsForSelectedFacets.Add(prop.Id, sidewaysSet(prop));
            }
            void countFacets(Property prop) {
                var facets = result[prop.Id];
                var set = specialSetsForSelectedFacets.TryGetValue(prop.Id, out var s) ? s : innerSet;
                prop.CountFacets(set, facets, ctx);
                facets.ApplyOptions(); // MinCount/MaxValues/SortByCount need the counts, so this must run after counting
            }
            if (parallel && relevantProps.Count > 1) Parallel.ForEach(relevantProps, countFacets);
            else foreach (var prop in relevantProps) countFacets(prop);
            filteredSource = new NodeCollectionData(_db, _ctx, _metrics, innerSet, this._nodeType, _includeBranches);
            if (pageSize.HasValue) {
                filteredSource = (NodeCollectionData)filteredSource.Page(pageIndex, pageSize.Value);
            }
            return result;
        }
        IEnumerable<Property> findRelevantProperties(Dictionary<Guid, Facets> givenById, bool addAllFacets, Definition def, IdSet nodeIds) {
            if (givenById.Count > 0 || !addAllFacets) { // if any given, only look at these:
                return givenById.Keys.Where(def.Properties.ContainsKey).Select(pId => def.Properties[pId]).Where(p => p.CanBeFacet());
            } else if (addAllFacets) {
                return def.GetFacetPropertiesForSet(nodeIds);
            } else {
                return [];
            }
        }
    }
}
