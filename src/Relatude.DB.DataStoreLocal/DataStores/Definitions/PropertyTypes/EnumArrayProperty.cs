using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
using Relatude.DB.Query.Expressions;
namespace Relatude.DB.DataStores.Definitions.PropertyTypes;

internal class EnumArrayProperty : Property, IPropertyContainsValue, IArrayProperty {
    IndexUtil<IIntArrayIndex> _indexUtil = new();
    readonly Dictionary<int, string> _nameByValue = new();
    readonly Dictionary<string, int> _valueByName = new();
    public IIntArrayIndex GetIndex(QueryContext ctx) => _indexUtil.GetIndex(ctx);
    public EnumArrayProperty(EnumArrayPropertyModel pm, Definition def) : base(pm, def) {
        if (pm.LegalValues != null && pm.LegalValueNames != null) {
            for (var i = 0; i < pm.LegalValues.Length && i < pm.LegalValueNames.Length; i++) {
                _nameByValue[pm.LegalValues[i]] = pm.LegalValueNames[i];
                _valueByName[pm.LegalValueNames[i]] = pm.LegalValues[i];
            }
        }
    }
    internal override void Initalize(DataStoreLocal store, Definition def, SettingsLocal config, IIOProvider io, AIEngine? ai) {
        if (Indexed) _indexUtil.Initalize(IndexFactory.CreateIntArrayIndexes(store, this, null), Model.CultureSensitive, AllIndexes);
    }
    public override PropertyType PropertyType => PropertyType.EnumArray;
    public override object ForceValueType(object value, out bool changed) {
        return EnumArrayPropertyModel.ForceValueType(value, out changed);
    }
    public override void ValidateValue(object value, INodeData node) {
        // elements are not validated against LegalValues, matching scalar enums (which also
        // accept any int - and [Flags] combinations would not be in LegalValues anyway)
    }
    public bool ContainsValue(object value, QueryContext ctx) {
        // the unique constraint passes the node's whole array; any element already indexed
        // elsewhere is a violation
        var index = GetIndex(ctx);
        var values = EnumArrayPropertyModel.ForceValueType(value, out _);
        foreach (var v in values) if (index.ContainsValue(v)) return true;
        return false;
    }
    // facet values are single ints; selections may arrive as ints, boxed enums, numeric strings
    // or enum NAME strings - unresolvable input must yield no match
    bool tryCoerceToInt(object value, out int result) {
        if (value is int i) { result = i; return true; }
        if (value is Enum e) { result = Convert.ToInt32(e); return true; }
        if (value is string s) {
            if (int.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out result)) return true;
            if (_valueByName.TryGetValue(s, out result)) return true;
        }
        result = 0;
        return false;
    }
    string displayNameOfValue(int value) => _nameByValue.TryGetValue(value, out var name) ? name : value.ToString();
    // Contains takes a boxed enum, an int or a numeric string, but deliberately not an enum NAME
    // string: unlike a facet selection (which arrives as text from a UI) a Contains value would then
    // only resolve on the indexed path, and row evaluation of a non indexed property has no name map.
    public IdSet FilterContainsElement(IdSet set, object? value, QueryContext ctx)
        => ArrayElementMatch.TryCoerce<int>(value, out var v) ? GetIndex(ctx).Filter(set, IndexOperator.Equal, v) : IdSet.Empty;
    public int MaxCountContainsElement(object? value, QueryContext ctx)
        => ArrayElementMatch.TryCoerce<int>(value, out var v) ? GetIndex(ctx).MaxCount(IndexOperator.Equal, v) : 0;
    public override bool CanBeFacet() => Indexed && !Model.NotFacet;
    public override long EstimateFilterFacetsMaxCount(Facets facets, IdSet source, QueryContext ctx) {
        var index = GetIndex(ctx);
        long total = 0; // selected values combine with OR: sum of the maintained per-value counts (unresolvable selections match nothing)
        foreach (var fv in facets.Values) {
            if (!fv.Selected || fv.Value == null) continue;
            if (tryCoerceToInt(fv.Value, out var v)) total += index.MaxCount(IndexOperator.Equal, v);
        }
        return total;
    }
    public override Facets GetDefaultFacets(Facets? given, QueryContext ctx) {
        var index = GetIndex(ctx);
        if (index == null) throw new NullReferenceException("Index is null. ");
        var facets = new Facets(Model);
        facets.CopyOptionsFrom(given);
        facets.IsRangeFacet = false; // ranges and the missing-value bucket are not supported for enum arrays
        facets.IncludeMissing = false;
        if (given != null && given.HasValues()) {
            foreach (var f in given.Values) facets.AddValue(f.Clone());
        } else {
            var possibleValues = index.GetUniqueValues();
            foreach (var value in possibleValues) facets.AddValue(new FacetValue(value));
        }
        foreach (var v in facets.Values) { // buckets show the enum name, not the int
            if (v.ExplicitDisplayName != null || v.Value == null) continue;
            if (tryCoerceToInt(v.Value, out var value)) v.DisplayName = displayNameOfValue(value);
        }
        return facets;
    }
    public override void CountFacets(IdSet nodeIds, Facets facets, QueryContext ctx, bool nodeIdsCoverIndex) { // covered counting not needed: the index mirror counts in memory
        var index = GetIndex(ctx);
        foreach (var facetValue in facets.Values) {
            if (facetValue.Value == null || !tryCoerceToInt(facetValue.Value, out var v)) { facetValue.Count = 0; continue; } // missing-value bucket not supported for enum arrays
            facetValue.Count = index.CountEqual(nodeIds, v);
        }
    }
    public override IdSet FilterFacets(Facets facets, IdSet nodeIds, QueryContext ctx) {
        var index = GetIndex(ctx);
        List<int> selectedValues = new();
        var hasSelected = false;
        foreach (var facetValue in facets.Values) {
            if (!facetValue.Selected || facetValue.Value == null) continue;
            hasSelected = true;
            if (tryCoerceToInt(facetValue.Value, out var v)) selectedValues.Add(v);
        }
        // any selection must filter, even when no value parsed: an unparsable selection matches
        // nothing (empty list -> empty set) rather than silently dropping the filter
        if (hasSelected) nodeIds = index.FilterInValues(nodeIds, selectedValues);
        return nodeIds;
    }
    public override bool AreValuesEqual(object v1, object v2) {
        var a1 = EnumArrayPropertyModel.ForceValueType(v1, out _);
        var a2 = EnumArrayPropertyModel.ForceValueType(v2, out _);
        if (a1 == null && a2 == null) return true; // both are null
        if (a1 == null || a2 == null) return false; // one is null, the other is not
        if (a1.Length != a2.Length) return false; // different lengths
        for (int i = 0; i < a1.Length; i++) {
            if (a1[i] != a2[i]) return false; // compare each element
        }
        return true; // all elements are equal
    }
}
