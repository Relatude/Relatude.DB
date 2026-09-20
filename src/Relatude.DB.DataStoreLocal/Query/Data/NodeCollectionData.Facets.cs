using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Sets;

namespace Relatude.DB.Query.Data {
    internal partial class NodeCollectionData : IStoreNodeDataCollection, IFacetSource {
        public Datamodel Datamodel { get => _def.Datamodel; }
        public Dictionary<Guid, Facets> EvaluateFacetsAndFilter(Dictionary<Guid, Facets> givenById, Dictionary<Guid, Facets> selection, FacetDiscovery discovery, out IFacetSource filteredSource, int pageIndex, int? pageSize, QueryContext ctx) {
            var ids = _ids;
            var counted = facetProperties(givenById, discovery, ids, ctx).ToList();
            var relevantProps = counted.ToList();
            foreach (var id in selection.Keys) // a selection filters whether or not its property is counted
                if (!relevantProps.Any(p => p.Id == id) && _def.Properties.TryGetValue(id, out var added) && added.CanBeFacet()) relevantProps.Add(added);
            var result = new Dictionary<Guid, Facets>();
            var innerSet = ids;
            var specialSetsForSelectedFacets = new Dictionary<Guid, IdSet>();
            var propsWithSelection = new List<Property>();
            foreach (var prop in relevantProps) {
                var facets = prop.GetDefaultFacets(givenById.TryGetValue(prop.Id, out var g) ? g : null, ctx);
                // no sort here: the buckets are ordered by ApplyOptions once they have been counted,
                // which is the only place that knows whether a sort was asked for
                result.Add(prop.Id, facets);
                if (selection.TryGetValue(prop.Id, out var selected))
                    facets.SetSelected(selected.HasValues() ? selected.Values : null);
                if (facets.HasSelected()) propsWithSelection.Add(prop);
            }
            // most selective selection first (cheap worst-case estimates, same idea as
            // AndNativeExpression): every later FilterFacets then runs against the smallest
            // possible set, and the sideways chains below walk the same order so they share the
            // longest possible prefixes in the set operation cache. OrderBy is stable, so
            // properties without a cheap estimate keep their relative order at the end.
            if (propsWithSelection.Count > 1) {
                var estimates = propsWithSelection.ToDictionary(p => p.Id, p => p.EstimateFilterFacetsMaxCount(result[p.Id], ids, ctx));
                propsWithSelection = propsWithSelection.OrderBy(p => estimates[p.Id]).ToList();
            }
            foreach (var prop in propsWithSelection) {
                if (innerSet.Count == 0) break; // empty stays empty: skip the remaining filters
                innerSet = prop.FilterFacets(result[prop.Id], innerSet, ctx);
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
                foreach (var otherProp in propsWithSelection) {
                    if (otherProp.Id == prop.Id) continue;
                    if (specialSet.Count == 0) break; // empty stays empty: skip the remaining filters
                    specialSet = otherProp.FilterFacets(result[otherProp.Id], specialSet, ctx);
                }
                return specialSet;
            }
            var selectedProps = propsWithSelection;
            if (parallel && selectedProps.Count > 1) {
                var sets = new IdSet[selectedProps.Count];
                Parallel.For(0, selectedProps.Count, i => sets[i] = sidewaysSet(selectedProps[i]));
                for (var i = 0; i < selectedProps.Count; i++) specialSetsForSelectedFacets.Add(selectedProps[i].Id, sets[i]);
            } else {
                foreach (var prop in selectedProps) specialSetsForSelectedFacets.Add(prop.Id, sidewaysSet(prop));
            }
            // when the counting set is the pristine, unfiltered full type set, every id a property's
            // index holds is in the set (for props declared within the query type), so buckets can be
            // counted from the index's own maintained counts - O(log n) tree probes instead of
            // materializing and intersecting id sets. This is what makes the first facet query on a
            // large persisted store instant instead of walking the whole value tree per bucket:
            var sourceIsFullTypeSet = false;
            if (!ctx.ExcludeDescendants) {
                var ctxSet = _def.GetAllIdsForType(_nodeType.Id, ctx);
                if (ids.StateId == ctxSet.StateId) {
                    // the context set is a subset of the unfiltered set, so equal counts means equal sets
                    sourceIsFullTypeSet = ctxSet.Count == _def.GetAllIdsForTypeNoAccessControl(_nodeType.Id, true).Count;
                }
            }
            void countFacets(Property prop) {
                var facets = result[prop.Id];
                var set = specialSetsForSelectedFacets.TryGetValue(prop.Id, out var s) ? s : innerSet;
                var covered = sourceIsFullTypeSet && set.StateId == ids.StateId && prop.IndexCoveredByQueryType(_nodeType.Id);
                prop.CountFacets(set, facets, ctx, covered);
                facets.ApplyOptions(); // MinCount/MaxValues/SortByCount need the counts, so this must run after counting
            }
            if (parallel && counted.Count > 1) Parallel.ForEach(counted, countFacets);
            else foreach (var prop in counted) countFacets(prop);
            foreach (var prop in relevantProps.Skip(counted.Count)) result.Remove(prop.Id);
            filteredSource = new NodeCollectionData(_db, _ctx, _metrics, innerSet, this._nodeType, _includeBranches);
            if (pageSize.HasValue) {
                filteredSource = (NodeCollectionData)filteredSource.Page(pageIndex, pageSize.Value);
            }
            return result;
        }
        // the selection part of EvaluateFacetsAndFilter on its own: what a clause chained onto the
        // facet clause (Pivot) evaluates against. Every selection is applied, whether or not its
        // property was named with AddFacet - a selection is a filter the caller asked for
        public IFacetSource FilterBySelection(Dictionary<Guid, Facets> givenById, Dictionary<Guid, Facets> selection, QueryContext ctx) {
            var innerSet = _ids;
            foreach (var (propId, selected) in selection) {
                if (innerSet.Count == 0) break; // empty stays empty: skip the remaining filters
                if (!_def.Properties.TryGetValue(propId, out var prop) || !prop.CanBeFacet()) continue;
                var facets = prop.GetDefaultFacets(givenById.TryGetValue(propId, out var g) ? g : null, ctx);
                facets.SetSelected(selected.HasValues() ? selected.Values : null);
                if (!facets.HasSelected()) continue;
                innerSet = prop.FilterFacets(facets, innerSet, ctx);
            }
            return new NodeCollectionData(_db, _ctx, _metrics, innerSet, _nodeType, _includeBranches);
        }
        // named first, then what the scopes add - the result's own types unless told otherwise - minus the excluded
        IEnumerable<Property> facetProperties(Dictionary<Guid, Facets> givenById, FacetDiscovery discovery, IdSet ids, QueryContext ctx) {
            var scopes = discovery.Scopes;
            if (givenById.Count == 0 && scopes.Count == 0 && !discovery.OnlyNamed) scopes = [new FacetScope(null, false, 0)];
            var props = givenById.Keys.Where(_def.Properties.ContainsKey).Select(id => _def.Properties[id]);
            foreach (var scope in scopes) {
                var max = scope.MaxDistinctValues == 0 ? Property.MaxAutomaticFacetValues : scope.MaxDistinctValues < 0 ? int.MaxValue : scope.MaxDistinctValues;
                var found = scope.TypeId is Guid typeId ? _def.GetFacetPropertiesForType(typeId, scope.IncludeDescendants) : _def.GetFacetPropertiesForSet(ids);
                props = props.Concat(found.Where(p => p.CanBeAutomaticFacet(ctx, max)));
            }
            return props.Where(p => p.CanBeFacet() && !discovery.Excluded.Contains(p.Id)).DistinctBy(p => p.Id);
        }
    }
}
