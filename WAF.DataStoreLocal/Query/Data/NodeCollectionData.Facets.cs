using WAF.Common;
using WAF.Datamodels;
using WAF.DataStores.Definitions;
using WAF.DataStores.Sets;

namespace WAF.Query.Data {
    internal partial class NodeCollectionData : IStoreNodeDataCollection, IFacetSource {
        public Datamodel Datamodel { get => _def.Datamodel; }
        public Dictionary<Guid, Facets> EvaluateFacetsAndFilter(Dictionary<Guid, Facets> givenById, Dictionary<Guid, Facets> selection, out IFacetSource filteredSource, int pageIndex, int? pageSize) {
            var ids = _ids;
            var relevantProps = findRelevantProperties(givenById, true, _def, ids);
            var result = new Dictionary<Guid, Facets>();
            var innerSet = ids;
            var specialSetsForSelectedFacets = new Dictionary<Guid, IdSet>();
            var propsWithSelection = new Dictionary<Guid, Property>();
            foreach (var prop in relevantProps) {
                var facets = prop.GetDefaultFacets(givenById.TryGetValue(prop.Id, out var g) ? g : null);
                facets.Sort();
                result.Add(prop.Id, facets);
                if (selection.TryGetValue(prop.Id, out var selected))
                    facets.SetSelected(selected.HasValues() ? selected.Values : null);
                if (facets.HasSelected()) {
                    innerSet = prop.FilterFacets(facets, innerSet);
                    propsWithSelection.Add(prop.Id, prop);
                }
            }
            foreach (var prop in propsWithSelection.Values) {
                var specialSet = ids;
                var otherPropsWithSelection = propsWithSelection.Values.Where(p => p.Id != prop.Id);
                foreach (var otherProp in otherPropsWithSelection) specialSet = otherProp.FilterFacets(result[otherProp.Id], specialSet);
                specialSetsForSelectedFacets.Add(prop.Id, specialSet);
            }
            foreach (var prop in relevantProps) {
                var facets = result[prop.Id];
                var set = specialSetsForSelectedFacets.TryGetValue(prop.Id, out var s) ? s : innerSet;
                prop.CountFacets(set, facets);
            }
            filteredSource = new NodeCollectionData(_db, _metrics, innerSet, this._nodeType, _includeBranches);
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
