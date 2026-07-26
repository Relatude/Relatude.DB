using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Definitions.PropertyTypes;

internal class GuidArrayProperty : Property, IPropertyContainsValue {
    IndexUtil<IGuidArrayIndex> _indexUtil = new();
    public IGuidArrayIndex GetIndex(QueryContext ctx) => _indexUtil.GetIndex(ctx);
    public GuidArrayProperty(GuidArrayPropertyModel pm, Definition def) : base(pm, def) {
    }
    internal override void Initalize(DataStoreLocal store, Definition def, SettingsLocal config, IIOProvider io, AIEngine? ai) {
        if (Indexed) _indexUtil.Initalize(IndexFactory.CreateGuidArrayIndexes(store, this, null), Model.CultureSensitive, AllIndexes);
    }
    public override PropertyType PropertyType => PropertyType.GuidArray;
    public override object ForceValueType(object value, out bool changed) {
        return GuidArrayPropertyModel.ForceValueType(value, out changed);
    }
    public override void ValidateValue(object value, INodeData node) {
    }
    public bool ContainsValue(object value, QueryContext ctx) {
        var index = GetIndex(ctx);
        var v = GuidPropertyModel.ForceValueType(value, out _);
        return index.ContainsValue(v);
    }
    // facet values are single guids; unparsable input must yield no match rather than collapse to
    // Guid.Empty (a legitimate value), hence the explicit TryParse instead of ForceValueType
    static bool tryCoerceToGuid(object value, out Guid guid) {
        if (value is Guid g) { guid = g; return true; }
        if (value is string s && Guid.TryParse(s, out guid)) return true;
        guid = Guid.Empty;
        return false;
    }
    public override bool CanBeFacet() => Indexed;
    public override Facets GetDefaultFacets(Facets? given, QueryContext ctx) {
        var index = GetIndex(ctx);
        if (index == null) throw new NullReferenceException("Index is null. ");
        var facets = new Facets(Model);
        facets.CopyOptionsFrom(given);
        facets.IsRangeFacet = false; // ranges and the missing-value bucket are not supported for guid arrays
        facets.IncludeMissing = false;
        if (given != null && given.HasValues()) {
            foreach (var f in given.Values) facets.AddValue(f.Clone());
        } else {
            var possibleValues = index.GetUniqueValues();
            foreach (var value in possibleValues) facets.AddValue(new FacetValue(value));
        }
        return facets;
    }
    public override void CountFacets(IdSet nodeIds, Facets facets, QueryContext ctx, bool nodeIdsCoverIndex) { // covered counting not needed: the index mirror counts in memory
        var index = GetIndex(ctx);
        foreach (var facetValue in facets.Values) {
            if (facetValue.Value == null || !tryCoerceToGuid(facetValue.Value, out var v)) { facetValue.Count = 0; continue; } // missing-value bucket not supported for guid arrays
            facetValue.Count = index.CountEqual(nodeIds, v);
        }
    }
    public override IdSet FilterFacets(Facets facets, IdSet nodeIds, QueryContext ctx) {
        var index = GetIndex(ctx);
        List<Guid> selectedValues = new();
        foreach (var facetValue in facets.Values) {
            if (!facetValue.Selected || facetValue.Value == null) continue;
            if (tryCoerceToGuid(facetValue.Value, out var v)) selectedValues.Add(v);
        }
        if (selectedValues.Count > 0) nodeIds = index.FilterInValues(nodeIds, selectedValues);
        return nodeIds;
    }
    public override bool AreValuesEqual(object v1, object v2) {
        var a1 = GuidArrayPropertyModel.ForceValueType(v1, out _);
        var a2 = GuidArrayPropertyModel.ForceValueType(v2, out _);
        if (a1 == null && a2 == null) return true; // both are null
        if (a1 == null || a2 == null) return false; // one is null, the other is not
        if (a1.Length != a2.Length) return false; // different lengths
        for (int i = 0; i < a1.Length; i++) {
            if (a1[i] != a2[i]) return false; // compare each guid
        }
        return true; // all guids are equal
    }
}
