using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Definitions.PropertyTypes;

internal class StringArrayProperty : Property, IPropertyContainsValue {
    IndexUtil<StringArrayIndex> _indexUtil = new();
    public StringArrayIndex GetIndex(QueryContext ctx) => _indexUtil.GetIndex(ctx);
    public StringArrayProperty(StringArrayPropertyModel pm, Definition def) : base(pm, def) {
    }
    internal override void Initalize(DataStoreLocal store, Definition def, SettingsLocal config, IIOProvider io, AIEngine? ai) {
        if (Indexed) _indexUtil.Initalize(IndexFactory.CreateStringArrayIndexes(store, this, null), Model.CultureSensitive, AllIndexes);
    }
    public override PropertyType PropertyType => PropertyType.StringArray;
    public override object ForceValueType(object value, out bool changed) {
        return StringArrayPropertyModel.ForceValueType(value, out changed);
    }
    public override void ValidateValue(object value) {
    }
    public bool ContainsValue(object value, QueryContext ctx) {
        var index = GetIndex(ctx);
        var v = StringPropertyModel.ForceValueType(value, out _);
        return index.ContainsValue(v);
    }
    public override object GetDefaultValue() => Array.Empty<string>();
    public override bool CanBeFacet() => Indexed;
    public override Facets GetDefaultFacets(Facets? given, QueryContext ctx) {
        var index = GetIndex(ctx);
        if (index == null) throw new NullReferenceException("Index is null. ");
        var facets = new Facets(Model);
        if (given?.DisplayName != null) facets.DisplayName = given.DisplayName;
        facets.IsRangeFacet = false;
        if (given != null && given.HasValues()) {
            foreach (var f in given.Values) facets.AddValue(new FacetValue(f.Value, f.Value2, f.DisplayName));
        } else {
            var possibleValues = index.GetUniqueValues();
            foreach (var value in possibleValues) facets.AddValue(new FacetValue(value));
        }
        return facets;
    }
    public override void CountFacets(IdSet nodeIds, Facets facets, QueryContext ctx) {
        var index = GetIndex(ctx);
        foreach (var facetValue in facets.Values) {
            var v = StringPropertyModel.ForceValueType(facetValue.Value, out _);
            facetValue.Count = index.CountEqual(nodeIds, v);
        }
    }
    public override IdSet FilterFacets(Facets facets, IdSet nodeIds, QueryContext ctx) {
        var index = GetIndex(ctx);
        List<string> selectedValues = new();
        foreach (var facetValue in facets.Values) {
            var v = StringPropertyModel.ForceValueType(facetValue.Value, out _);
            if (facetValue.Selected) selectedValues.Add(v);
        }
        if (selectedValues.Count > 0) nodeIds = index.FilterInValues(nodeIds, selectedValues);
        return nodeIds;
    }
    public override bool AreValuesEqual(object v1, object v2) {
        var a1 = StringArrayPropertyModel.ForceValueType(v1, out _);
        var a2 = StringArrayPropertyModel.ForceValueType(v2, out _);
        if (a1 == null && a2 == null) return true; // both are null
        if (a1 == null || a2 == null) return false; // one is null, the other is not
        if (a1.Length != a2.Length) return false; // different lengths
        for (int i = 0; i < a1.Length; i++) {
            if (a1[i] != a2[2]) return false; // compare each string
        }
        return true; // all strings are equal
    }
}
