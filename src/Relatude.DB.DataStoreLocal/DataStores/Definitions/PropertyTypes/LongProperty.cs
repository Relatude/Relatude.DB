using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.DataStores.Definitions.PropertyTypes;
internal class LongProperty : ValueProperty<long>, IPropertyContainsValue {
    public LongProperty(LongPropertyModel pm, Definition def) : base(pm, def) {
        MinValue = pm.MinValue;
        MaxValue = pm.MaxValue;
        DefaultValue = pm.DefaultValue;
    }
    protected override void WriteValue(long v, IAppendStream stream) => stream.WriteLong(v);
    protected override long ReadValue(IReadStream stream) => stream.ReadLong();
    public override bool TryReorder(IdSet unsorted, bool descending, [MaybeNullWhen(false)] out IdSet sorted) {
        if (Index != null) {
            sorted = Index.ReOrder(unsorted, descending);
            return true;
        }
        return base.TryReorder(unsorted, descending, out sorted);
    }
    public override PropertyType PropertyType => PropertyType.Long;
    public long DefaultValue;
    public override IRangeIndex? ValueIndex => Index;
    public long MinValue = long.MinValue;
    public long MaxValue = long.MaxValue;
    public IValueIndex<long>? Index;
    public override object ForceValueType(object value, out bool changed) {
        return LongPropertyModel.ForceValueType(value, out changed);
    }
    public override void ValidateValue(object value) {
        var v = (long)value;
        if (v > MaxValue) throw new Exception("Value is more than maximum value allowed. ");
        if (v < MinValue) throw new Exception("Value is less than minimum value allowed. ");
    }
    public override object GetDefaultValue() => DefaultValue;
    public static object GetValue(byte[] bytes) => BitConverter.ToInt32(bytes, 0);
    public bool ContainsValue(object value) {
        if (Index == null) throw new Exception("Index is null. ");
        return Index.ContainsValue((long)value);
    }
    public override bool CanBeFacet() => Indexed;
    public override Facets GetDefaultFacets(Facets? given, QueryContext ctx) {
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
                var ranges = RangeGenerators.Longs.GetRanges(v1, v2, facets.RangeCount, facets.RangePowerBase, 20);
                foreach (var r in ranges) facets.AddValue(new FacetValue(r.Item1, r.Item2, null));
            } else {
                var possibleValues = Index.UniqueValues;
                foreach (var value in possibleValues) facets.AddValue(new FacetValue(value));
            }
        }
        return facets;
    }
    public override IdSet FilterFacets(Facets facets, IdSet nodeIds, QueryContext ctx) {
        if (Index == null) throw new NullReferenceException("Index is null. ");
        var useRange = facets.IsRangeFacet.HasValue ? facets.IsRangeFacet.Value : true; // default true...
        if (useRange) {
            List<Tuple<long, long>> selectedRanges = new();
            foreach (var facetValue in facets.Values) {
                var from = LongPropertyModel.ForceValueType(facetValue.Value, out _);
                var to = facetValue.Value2 == null ? long.MaxValue : LongPropertyModel.ForceValueType(facetValue.Value2, out _);
                if (facetValue.Selected) selectedRanges.Add(new(from, to));
            }
            if (selectedRanges.Count > 0) nodeIds = Index.FilterRanges(nodeIds, selectedRanges);
        } else {
            List<long> selectedValues = new();
            foreach (var facetValue in facets.Values) {
                var v = LongPropertyModel.ForceValueType(facetValue.Value, out _);
                if (facetValue.Selected) selectedValues.Add(v);
            }
            if (selectedValues.Count > 0) nodeIds = Index.FilterInValues(nodeIds, selectedValues);
        }
        return nodeIds;
    }
    public override void CountFacets(IdSet nodeIds, Facets facets, QueryContext ctx) {
        if (Index == null) throw new NullReferenceException("Index is null. ");
        var useRange = facets.IsRangeFacet.HasValue ? facets.IsRangeFacet.Value : true; // default true...
        if (useRange) {
            foreach (var facetValue in facets.Values) {
                var from = LongPropertyModel.ForceValueType(facetValue.Value, out _);
                var to = facetValue.Value2 == null ? long.MaxValue : LongPropertyModel.ForceValueType(facetValue.Value2, out _);
                facetValue.Count = Index.CountInRangeEqual(nodeIds, from, to, facetValue.FromInclusive, facetValue.ToInclusive);
            }
        } else {
            foreach (var facetValue in facets.Values) {
                var v = LongPropertyModel.ForceValueType(facetValue.Value, out _);
                facetValue.Count = Index.CountEqual(nodeIds, v);
            }
        }
    }
    public override IdSet WhereIn(IdSet ids, IEnumerable<object?> values, QueryContext ctx) {
        if (Index == null) throw new NullReferenceException("Property is not indexed. ");
        return Index.FilterInValues(ids, values.Cast<long>().ToList());
    }
    public override bool SatisfyValueRequirement(object value1, object value2, ValueRequirement requirement) {
        var v1 = LongPropertyModel.ForceValueType(value1, out _);
        var v2 = LongPropertyModel.ForceValueType(value2, out _);
        return requirement switch {
            ValueRequirement.Equal => v1 == v2,
            ValueRequirement.NotEqual => v1 != v2,
            ValueRequirement.Less => v1 < v2,
            ValueRequirement.LessOrEqual => v1 <= v2,
            ValueRequirement.Greater => v1 > v2,
            ValueRequirement.GreaterOrEqual => v1 >= v2,
            _ => throw new NotImplementedException(),
        };
    }
    public override bool AreValuesEqual(object v1, object v2) {
        if (v1 is long l1 && v2 is long l2) return l1 == l2;
        return false;
    }
}
