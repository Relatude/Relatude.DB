using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Definitions.PropertyTypes;

internal class RelationProperty : Property {
    public RelationProperty(RelationPropertyModel pm, Definition def) : base(pm, def) {
        RelationId = pm.RelationId;
        RelModel = pm;
    }

    public override PropertyType PropertyType => PropertyType.Relation;
    public Guid RelationId { get; }
    public RelationPropertyModel RelModel { get; }

    public override object ForceValueType(object value, out bool changed) {
        throw new NotImplementedException();
    }
    public override void ValidateValue(object value, INodeData node) {
        throw new NotImplementedException();
    }
    internal override void Initalize(DataStoreLocal store, Definition def, SettingsLocal config, IIOProvider io, AIEngine? ai) {
        //throw new NotImplementedException();
    }
    public override bool AreValuesEqual(object v1, object v2) {
        throw new NotImplementedException();
    }

    // ---- facets ----
    // Buckets are the related nodes: their ids are the ones passed to GetRelated(bucketId, dir)
    // with the same direction negation as relates() in NodeCollectionData.Relates.cs. The bucket
    // Value is the node data of the related node (converted to a typed node object in the
    // NodeStore layer); selections arrive as guid strings and are coerced back to ids here.
    // Resolved lazily: relations initialize after properties, and RelationId is assigned late.
    Relation relation => Definition.Relations[RelModel.RelationId];

    public override bool CanBeFacet() => RelModel.Facet;

    // selections must never match the wrong bucket: unresolvable input yields no match
    bool tryCoerceToNodeId(object value, out int id) {
        if (value is INodeData nd) { id = nd.__Id; return true; }
        Guid guid;
        if (value is Guid g) guid = g;
        else if (value is string s && Guid.TryParse(s, out var parsed)) guid = parsed;
        else { id = 0; return false; }
        return Definition.Store._guids.TryGetId(guid, out id);
    }
    string displayNameOfNode(INodeData node) {
        var name = Definition.Datamodel.NodeTypes[node.NodeType].GetDisplayName(node);
        return string.IsNullOrEmpty(name) ? node.Id.ToString() : name;
    }

    public override Facets GetDefaultFacets(Facets? given, QueryContext ctx) {
        var facets = new Facets(Model);
        facets.CopyOptionsFrom(given);
        facets.IsRangeFacet = false; // ranges and the missing-value bucket are not supported for relations
        facets.IncludeMissing = false;
        var db = Definition.Store;
        if (given != null && given.HasValues()) {
            // given buckets (typically re-posted selections) hold guids; resolve them to node data
            // so the buckets stay homogeneous with the default enumeration below
            foreach (var f in given.Values) {
                var clone = f.Clone();
                if (clone.Value != null && tryCoerceToNodeId(clone.Value, out var id) && db._nodes.TryGet(id, out var node, out _)
                    && tryToOuter(node, ctx, out var outer)) {
                    clone.Value = outer;
                    if (clone.ExplicitDisplayName == null) clone.DisplayName = displayNameOfNode(outer);
                }
                facets.AddValue(clone); // unresolvable values stay as given and count 0
            }
        } else {
            var bucketIds = relation.DistinctIds(!RelModel.FromTargetToSource).ToArray();
            var nodes = db._nodes.Get(bucketIds); // batched: one disk read for all cache misses
            var values = new List<FacetValue>(nodes.Length);
            foreach (var node in nodes) {
                // revision/context correct, same as include queries; a node with no revision
                // visible in this context (e.g. unpublished) is simply not a bucket
                if (!tryToOuter(node, ctx, out var outer)) continue;
                values.Add(new FacetValue(outer) { DisplayName = displayNameOfNode(outer) });
            }
            // deterministic order: Facets.Sort() cannot sort node data values (not IComparable)
            foreach (var fv in values.OrderBy(v => v.DisplayName, StringComparer.Ordinal)) facets.AddValue(fv);
        }
        return facets;
    }
    bool tryToOuter(INodeDataInternal node, QueryContext ctx, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out INodeDataExternal outer) {
        try {
            outer = Definition.Store.ToOuter(node, ctx);
            return true;
        } catch {
            outer = null; // no suitable revision for this context
            return false;
        }
    }
    public override void CountFacets(IdSet nodeIds, Facets facets, QueryContext ctx, bool nodeIdsCoverIndex) { // covered counting not needed: relation sets are cached in memory
        var rel = relation;
        var dir = !RelModel.FromTargetToSource;
        var sets = Definition.Sets;
        foreach (var facetValue in facets.Values) {
            if (facetValue.Value == null || !tryCoerceToNodeId(facetValue.Value, out var bucketId)) { facetValue.Count = 0; continue; } // missing-value bucket not supported for relations
            facetValue.Count = sets.CountIntersection(nodeIds, rel.GetRelated(bucketId, dir));
        }
    }
    public override IdSet FilterFacets(Facets facets, IdSet nodeIds, QueryContext ctx) {
        var rel = relation;
        var dir = !RelModel.FromTargetToSource;
        var sets = Definition.Sets;
        List<IdSet> matches = [];
        var hasSelected = false;
        foreach (var facetValue in facets.Values) {
            if (!facetValue.Selected || facetValue.Value == null) continue;
            hasSelected = true;
            if (!tryCoerceToNodeId(facetValue.Value, out var bucketId)) continue; // unresolvable selection matches nothing
            var matchForOneValue = sets.Intersection(nodeIds, rel.GetRelated(bucketId, dir));
            if (matchForOneValue.Count > 0) matches.Add(matchForOneValue);
        }
        if (hasSelected) nodeIds = sets.Union(matches); // OR semantics across selected values
        return nodeIds;
    }
}
