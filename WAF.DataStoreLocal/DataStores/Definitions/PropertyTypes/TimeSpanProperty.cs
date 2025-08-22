using System.Diagnostics.CodeAnalysis;
using WAF.AI;
using WAF.Common;
using WAF.Datamodels.Properties;
using WAF.DataStores.Indexes;
using WAF.DataStores.Sets;
using WAF.IO;
namespace WAF.DataStores.Definitions.PropertyTypes;
internal class TimeSpanProperty : Property, IPropertyContainsValue {
    public TimeSpanProperty(TimeSpanPropertyModel pm, Definition def) : base(pm, def) {
        MinValue = pm.MinValue;
        MaxValue = pm.MaxValue;
        DefaultValue = pm.DefaultValue;
    }
    internal override void Initalize(DataStoreLocal store, Definition def, SettingsLocal config, IIOProvider io, IAIProvider? ai) {
        if (Indexed) {
            Index = IndexFactory.CreateValueIndex(store, def.Sets, this, null, write, read);
            Indexes.Add(Index);
        }
    }
    void write(TimeSpan v, IAppendStream stream) => stream.WriteLong(v.Ticks);
    TimeSpan read(IReadStream stream) => TimeSpan.FromTicks(stream.ReadLong());
    public override IRangeIndex? ValueIndex => Index;
    public override bool TryReorder(IdSet unsorted, bool descending, [MaybeNullWhen(false)] out IdSet sorted) {
        if (Index != null) {
            sorted = Index.ReOrder(unsorted, descending);
            return true;
        }
        return base.TryReorder(unsorted, descending, out sorted);
    }
    public override PropertyType PropertyType => PropertyType.TimeSpan;
    public TimeSpan DefaultValue;
    public TimeSpan MinValue = TimeSpan.MinValue;
    public TimeSpan MaxValue = TimeSpan.MaxValue;
    public IValueIndex<TimeSpan>? Index;
    public override object ForceValueType(object value, out bool changed) {
        return TimeSpanPropertyModel.ForceValueType(value, out changed);
    }
    public override void ValidateValue(object value) {
        var v = (TimeSpan)value;
        if (v > MaxValue) throw new Exception("Value is more than maximum value allowed. ");
        if (v < MinValue) throw new Exception("Value is less than minimum value allowed. ");
    }
    public override object GetDefaultValue() => DefaultValue;
    public static object GetValue(byte[] bytes) => BitConverter.ToInt32(bytes, 0);
    public bool ContainsValue(object value) {
        if (Index == null) throw new Exception("Index is null. ");
        return Index.ContainsValue((TimeSpan)value);
    }

    // Acets Needs improvement...

    public override bool CanBeFacet() => Indexed;
    public override Facets GetDefaultFacets(Facets? given) {

        if (Index == null) throw new NullReferenceException("Index is null. ");
        var facets = new Facets(Model);
        if (given?.DisplayName != null) facets.DisplayName = given.DisplayName;
        facets.IsRangeFacet = (given != null && given.IsRangeFacet.HasValue) ? given.IsRangeFacet.HasValue : true; // default true...
        if (given != null && given.HasValues()) {
            foreach (var f in given.Values) {
                if (f.Value.ToString() == "1" && (f.Value2 + "") == "0") {
                    f.Value = Index.MinValue();
                    f.Value2 = Index.MaxValue();
                }
                facets.AddValue(new FacetValue(f.Value, f.Value2, f.DisplayName));
            }
        } else {
            if (facets.IsRangeFacet.Value) {
                var v1 = Index.MinValue();
                var v2 = Index.MaxValue();
                var ranges = RangeGenerators.TimeSpans.GetRanges(v1, v2, facets.RangeCount, facets.RangePowerBase, 20);
                foreach (var r in ranges) facets.AddValue(new FacetValue(r.Item1, r.Item2, null));
            } else {
                var possibleValues = Index.UniqueValues;
                foreach (var value in possibleValues) facets.AddValue(new FacetValue(value));
            }
        }
        return facets;
    }
    public override IdSet FilterFacets(Facets facets, IdSet nodeIds) {
        if (Index == null) throw new NullReferenceException("Index is null. ");
        var useRange = facets.IsRangeFacet.HasValue ? facets.IsRangeFacet.Value : true; // default true...
        if (useRange) {
            List<Tuple<TimeSpan, TimeSpan>> selectedRanges = new();
            foreach (var facetValue in facets.Values) {
                var from = TimeSpanPropertyModel.ForceValueType(facetValue.Value, out _);
                var to = facetValue.Value2 == null ? TimeSpan.MaxValue : TimeSpanPropertyModel.ForceValueType(facetValue.Value2, out _);
                if (facetValue.Selected) selectedRanges.Add(new(from, to));
            }
            if (selectedRanges.Count > 0) nodeIds = Index.FilterRanges(nodeIds, selectedRanges);
        } else {
            List<TimeSpan> selectedValues = new();
            foreach (var facetValue in facets.Values) {
                var v = TimeSpanPropertyModel.ForceValueType(facetValue.Value, out _);
                if (facetValue.Selected) selectedValues.Add(v);
            }
            if (selectedValues.Count > 0) nodeIds = Index.FilterInValues(nodeIds, selectedValues);
        }
        return nodeIds;
    }
    public override void CountFacets(IdSet nodeIds, Facets facets) {
        if (Index == null) throw new NullReferenceException("Index is null. ");
        var useRange = facets.IsRangeFacet.HasValue ? facets.IsRangeFacet.Value : true; // default true...
        if (useRange) {
            foreach (var facetValue in facets.Values) {
                var from = TimeSpanPropertyModel.ForceValueType(facetValue.Value, out _);
                var to = facetValue.Value2 == null ? TimeSpan.MaxValue : TimeSpanPropertyModel.ForceValueType(facetValue.Value2, out _);
                facetValue.Count = Index.CountInRangeEqual(nodeIds, from, to, facetValue.FromInclusive, facetValue.ToInclusive);
            }
        } else {
            foreach (var facetValue in facets.Values) {
                var v = TimeSpanPropertyModel.ForceValueType(facetValue.Value, out _);
                facetValue.Count = Index.CountEqual(nodeIds, v);
            }
        }
    }
    public override bool SatisfyValueRequirement(object value1, object value2, ValueRequirement requirement) {
        var v1 = TimeSpanPropertyModel.ForceValueType(value1, out _);
        var v2 = TimeSpanPropertyModel.ForceValueType(value2, out _);
        return requirement switch {
            ValueRequirement.Equal => v1 == v2,
            ValueRequirement.NotEqual => v1 != v2,
            ValueRequirement.Greater => v1 > v2,
            ValueRequirement.GreaterOrEqual=> v1 >= v2,
            ValueRequirement.Less => v1 < v2,
            ValueRequirement.LessOrEqual => v1 <= v2,
            _ => throw new NotSupportedException(),
        };
    }
    public override bool AreValuesEqual(object v1, object v2) {
        if(v1 is TimeSpan ts1 && v2 is TimeSpan ts2) return ts1 == ts2;
        return false;
    }
}
