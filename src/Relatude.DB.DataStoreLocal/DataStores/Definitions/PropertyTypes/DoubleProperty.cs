using System.Diagnostics.CodeAnalysis;
using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
using Relatude.DB.Transactions;

namespace Relatude.DB.DataStores.Definitions.PropertyTypes {
    internal class DoubleProperty : Property {
        public DoubleProperty(DoublePropertyModel pm, Definition def) : base(pm, def) {
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
        void write(double v, IAppendStream stream) => stream.WriteDouble(v);
        double read(IReadStream stream) => stream.ReadDouble();
        public override PropertyType PropertyType => PropertyType.Double;
        public double DefaultValue;
        public override IRangeIndex? ValueIndex => Index;
        public double MinValue = double.MinValue;
        public double MaxValue = double.MaxValue;
        public IValueIndex<double>? Index;
        public override bool TryReorder(IdSet unsorted, bool descending, [MaybeNullWhen(false)] out IdSet sorted) {
            if (Index != null) {
                sorted = Index.ReOrder(unsorted, descending);
                return true;
            }
            return base.TryReorder(unsorted, descending, out sorted);
        }
        public override object ForceValueType(object value, out bool changed) {
            return DoublePropertyModel.ForceValueType(value, out changed);
        }
        public override void ValidateValue(object value) {
            var v = (double)value;
            if (v > MaxValue) throw new Exception("Value is more than maximum value allowed. ");
            if (v < MinValue) throw new Exception("Value is less than minimum value allowed. ");
        }
        public override object GetDefaultValue() => DefaultValue;
        public static object GetValue(byte[] bytes) => BitConverter.ToDouble(bytes, 0);
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
                    var ranges = RangeGenerators.Doubles.GetRanges(v1, v2, facets.RangeCount, facets.RangePowerBase, 20);
                    double to = 0;
                    for (var i = 0; i < ranges.Count; i++) {
                        var from = ranges[i].Item1;
                        if (i < ranges.Count - 1) {
                            to = ranges[i + 1].Item1;
                        } else {
                            to = ranges[i].Item2;
                        }
                        var facet = new FacetValue(from, to, null);
                        facet.FromInclusive = true;
                        facet.ToInclusive = i == ranges.Count - 1;
                        facets.AddValue(facet);
                    }
                } else {
                    var possibleValues = Index.UniqueValues;
                    foreach (var value in possibleValues) facets.AddValue(new FacetValue(value));
                }
            }
            return facets;

        }
        public override IdSet FilterFacets(Facets facets, IdSet nodeIds) {
            throw new NotSupportedException();
        }
        public override void CountFacets(IdSet nodeIds, Facets facets) {
            if (Index == null) throw new NullReferenceException("Index is null. ");
            var useRange = facets.IsRangeFacet.HasValue ? facets.IsRangeFacet.Value : true; // default true...
            if (useRange) {
                foreach (var facetValue in facets.Values) {
                    var from = DoublePropertyModel.ForceValueType(facetValue.Value, out _);
                    var to = facetValue.Value2 == null ? int.MaxValue : DoublePropertyModel.ForceValueType(facetValue.Value2, out _);
                    facetValue.Count = Index.CountInRangeEqual(nodeIds, from, to, facetValue.FromInclusive, facetValue.ToInclusive);
                }
            } else {
                foreach (var facetValue in facets.Values) {
                    var v = DoublePropertyModel.ForceValueType(facetValue.Value, out _);
                    facetValue.Count = Index.CountEqual(nodeIds, v);
                }
            }
        }
        public override bool SatisfyValueRequirement(object value1, object value2, ValueRequirement requirement) {
            var v1 = DoublePropertyModel.ForceValueType(value1, out _);
            var v2 = DoublePropertyModel.ForceValueType(value2, out _);
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
            if (v1 is double d1 && v2 is double d2) return d1 == d2;
            return false;
        }
    }
}
