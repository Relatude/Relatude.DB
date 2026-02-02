using System.Diagnostics.CodeAnalysis;
using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
using Relatude.DB.Transactions;
namespace Relatude.DB.DataStores.Definitions.PropertyTypes;
internal class DateTimeProperty : Property, IPropertyContainsValue {
    public DateTimeProperty(DateTimePropertyModel pm, Definition def) : base(pm, def) {
        MinValue = pm.MinValue;
        MaxValue = pm.MaxValue;
        DefaultValue = pm.DefaultValue;
    }
    internal override void Initalize(DataStoreLocal store, Definition def, SettingsLocal config, IIOProvider io, AIEngine? ai) {
        if (Indexed) {
            Index = IndexFactory.CreateValueIndex(store, def.Sets, this, null, write, read);
            Indexes.Add(Index);
        }
    }
    void write(DateTime v, IAppendStream stream) => stream.WriteDateTimeUtc(v);
    DateTime read(IReadStream stream) => stream.ReadDateTimeUtc();
    public override bool TryReorder(IdSet unsorted, bool descending, [MaybeNullWhen(false)] out IdSet sorted) {
        if (Index != null) {
            sorted = Index.ReOrder(unsorted, descending);
            return true;
        }
        return base.TryReorder(unsorted, descending, out sorted);
    }
    public override PropertyType PropertyType => PropertyType.DateTime;
    public override IRangeIndex? ValueIndex => Index;
    public DateTime DefaultValue;
    public DateTime MinValue = DateTime.MinValue;
    public DateTime MaxValue = DateTime.MaxValue;
    public IValueIndex<DateTime>? Index;
    public override object ForceValueType(object value, out bool changed) {
        return DateTimePropertyModel.ForceValueType(value, out changed);
    }
    public override void ValidateValue(object value) {
        var v = (DateTime)value;
        if (v > MaxValue) throw new Exception("Value is more than maximum value allowed. ");
        if (v < MinValue) throw new Exception("Value is less than minimum value allowed. ");
    }
    public override object GetDefaultValue() => DefaultValue;
    public bool ContainsValue(object value) {
        if (Index == null) throw new Exception("Index is null. ");
        return Index.ContainsValue((DateTime)value);
    }
    // Facets Needs improvement...
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
                facets.AddValue(new FacetValue(v1, v2, null));  // data ranges not supported yet... so just take max to min...
                //var ranges = RangeGenerators.DateTimes.GetRanges(v1, v2, facets.RangeCount, facets.RangePowerBase, 20);
                //foreach (var r in ranges) facets.AddValue(new FacetValue(r.Item1, r.Item2, null));
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
            List<Tuple<DateTime, DateTime>> selectedRanges = new();
            foreach (var facetValue in facets.Values) {
                var from = DateTimePropertyModel.ForceValueType(facetValue.Value, out _);
                var to = facetValue.Value2 == null ? DateTime.MaxValue : DateTimePropertyModel.ForceValueType(facetValue.Value2, out _);
                if (facetValue.Selected) selectedRanges.Add(new(from, to));
            }
            if (selectedRanges.Count > 0) nodeIds = Index.FilterRanges(nodeIds, selectedRanges);
        } else {
            List<DateTime> selectedValues = new();
            foreach (var facetValue in facets.Values) {
                var v = DateTimePropertyModel.ForceValueType(facetValue.Value, out _);
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
                var from = DateTimePropertyModel.ForceValueType(facetValue.Value, out _);
                var to = facetValue.Value2 == null ? DateTime.MaxValue : DateTimePropertyModel.ForceValueType(facetValue.Value2, out _);
                facetValue.Count = Index.CountInRangeEqual(nodeIds, from, to, facetValue.FromInclusive, facetValue.ToInclusive);
            }
        } else {
            foreach (var facetValue in facets.Values) {
                var v = DateTimePropertyModel.ForceValueType(facetValue.Value, out _);
                facetValue.Count = Index.CountEqual(nodeIds, v);
            }
        }
    }
    public override bool SatisfyValueRequirement(object value1, object value2, ValueRequirement requirement) {
        var v1 = DateTimePropertyModel.ForceValueType(value1, out _);
        var v2 = DateTimePropertyModel.ForceValueType(value2, out _);
        return requirement switch {
            ValueRequirement.Equal => v1 == v2,
            ValueRequirement.NotEqual => v1 != v2,
            ValueRequirement.Greater => v1 > v2,
            ValueRequirement.GreaterOrEqual => v1 >= v2,
            ValueRequirement.Less => v1 < v2,
            ValueRequirement.LessOrEqual => v1 <= v2,
            _ => throw new NotSupportedException(),
        };
    }
    public override bool AreValuesEqual(object v1, object v2) {
        if (v1 is DateTime dt1 && v2 is DateTime dt2) return dt1 == dt2;
        return false;
    }
}
