using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Definitions.PropertyTypes;
internal class StringArrayProperty : Property, IPropertyContainsValue {
    public StringArrayProperty(StringArrayPropertyModel pm, Definition def) : base(pm, def) {
    }
    internal override void Initalize(DataStoreLocal store, Definition def, SettingsLocal config, IIOProvider io, IAIProvider? ai) {
        if (Indexed) Index = new StringArrayIndex(def, Id + nameof(StringArrayIndex), Id);
        if (Index != null) Indexes.Add(Index);
    }
    public override IRangeIndex? ValueIndex => null;
    public override PropertyType PropertyType => PropertyType.StringArray;
    public StringArrayIndex? Index;
    public override object ForceValueType(object value, out bool changed) {
        return StringArrayPropertyModel.ForceValueType(value, out changed);
    }
    public override void ValidateValue(object value) {
    }
    public override object GetDefaultValue() => Array.Empty<string>();
    public bool ContainsValue(object value) {
        if (Index == null) throw new Exception("Index is null. ");
        return Index.ContainsValue((string)value);
    }
    public override bool CanBeFacet() => Indexed;
    public override Facets GetDefaultFacets(Facets? given) {
        if (Index == null) throw new NullReferenceException("Index is null. ");
        var facets = new Facets(Model);
        if (given?.DisplayName != null) facets.DisplayName = given.DisplayName;
        facets.IsRangeFacet = false;
        if (given != null && given.HasValues()) {
            foreach (var f in given.Values) facets.AddValue(new FacetValue(f.Value, f.Value2, f.DisplayName));
        } else {
            var possibleValues = Index.GetUniqueValues();
            foreach (var value in possibleValues) facets.AddValue(new FacetValue(value));
        }
        return facets;
    }
    public override void CountFacets(IdSet nodeIds, Facets facets) {
        if (Index == null) throw new NullReferenceException("Index is null. ");
        foreach (var facetValue in facets.Values) {
            var v = StringPropertyModel.ForceValueType(facetValue.Value, out _);
            facetValue.Count = Index.CountEqual(nodeIds, v);
        }
    }
    public override IdSet FilterFacets(Facets facets, IdSet nodeIds) {
        if (Index == null) throw new NullReferenceException("Index is null. ");
        List<string> selectedValues = new();
        foreach (var facetValue in facets.Values) {
            var v = StringPropertyModel.ForceValueType(facetValue.Value, out _);
            if (facetValue.Selected) selectedValues.Add(v);
        }
        if (selectedValues.Count > 0) nodeIds = Index.FilterInValues(nodeIds, selectedValues);
        return nodeIds;
    }
    public override bool AreValuesEqual(object v1, object v2) {
        var a1 = StringArrayPropertyModel.ForceValueType(v1, out _);
        var a2 = StringArrayPropertyModel.ForceValueType(v2, out _);
        if(a1 == null && a2 == null) return true; // both are null
        if (a1 == null || a2 == null) return false; // one is null, the other is not
        if (a1.Length != a2.Length) return false; // different lengths
        for (int i = 0; i < a1.Length; i++) {
            if (a1[i] != a2[2]) return false; // compare each string
        }
        return true; // all strings are equal
    }
}
