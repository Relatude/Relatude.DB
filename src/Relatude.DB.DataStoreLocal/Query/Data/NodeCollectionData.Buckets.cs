using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Definitions.PropertyTypes;

namespace Relatude.DB.Query.Data;

internal partial class NodeCollectionData : IBucketSource {
    /// <summary>
    /// The facet primitives handed back as id sets (see <see cref="IBucketSource"/>). The buckets are
    /// the property's default facets - the same ones a facet query or a pivot level would use - and
    /// each is cut against this collection with FilterFacets, so a bucket holds exactly the nodes of
    /// this result that carry the value. No node is read.
    ///
    /// When the property has more distinct values than buckets are wanted, the buckets are counted
    /// first (one pass, CountFacets) and only the largest are filtered: filtering every value of a
    /// high-cardinality property would build one id set per value, most of them tiny.
    /// </summary>
    public IReadOnlyList<ValueBucket> Bucket(Guid propertyId, bool? isRange, bool includeMissing, int maxBuckets, QueryContext ctx) {
        if (!_def.Properties.TryGetValue(propertyId, out var prop)) throw new Exception("Unknown property " + propertyId + ". ");
        if (!prop.CanBeFacet()) {
            var why = prop is RelationProperty ? "relation properties must opt in with [RelationProperty(Facet = true)]"
                : !prop.Indexed ? "it is not indexed"
                : prop.Model.NotFacet ? "it is marked NotFacet" : "values of type " + prop.PropertyType + " cannot be bucketed";
            throw new Exception("The property \"" + prop.CodeName + "\" cannot be bucketed: " + why + ". ");
        }
        if (_ids.Count == 0) return [];
        var given = new Facets(prop.Model, isRange) { IncludeMissing = includeMissing };
        var facets = prop.GetDefaultFacets(given, ctx);
        facets.Sort(); // the natural order: values sorted, ranges as generated, the missing bucket last
        var values = facets.Values;
        if (values.Count == 0) return [];
        var rangeFacet = facets.IsRangeFacet == true;
        if (maxBuckets > 0 && values.Count > maxBuckets) {
            prop.CountFacets(_ids, facets, ctx, nodeIdsCoverIndex: false);
            // the largest survive; OrderByDescending is stable, so equal counts keep the natural order
            var keep = values.Where(v => v.Count > 0).OrderByDescending(v => v.Count).Take(maxBuckets).ToHashSet();
            values = values.Where(keep.Contains).ToList();
        }
        var result = new List<ValueBucket>(values.Count);
        foreach (var fv in values) {
            var selected = fv.Clone();
            selected.Selected = true;
            var set = prop.FilterFacets(new Facets(prop.Model, rangeFacet, [selected]), _ids, ctx);
            if (set.Count == 0) continue;
            var value = fv.Clone();
            value.Selected = false;
            value.Count = set.Count;
            result.Add(new ValueBucket(value, set.Enumerate(), set.Count));
        }
        return result;
    }
}
