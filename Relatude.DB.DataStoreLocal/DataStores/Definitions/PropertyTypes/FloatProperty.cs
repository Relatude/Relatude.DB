using System.Diagnostics.CodeAnalysis;
using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Definitions.PropertyTypes {
    internal class FloatProperty : Property {
        public FloatProperty(FloatPropertyModel pm, Definition def) : base(pm, def) {
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
        void write(float v, IAppendStream stream) => stream.WriteFloat(v);
        float read(IReadStream stream) => stream.ReadFloat();

        public override bool TryReorder(IdSet unsorted, bool descending, [MaybeNullWhen(false)] out IdSet sorted) {
            if (Index != null) {
                sorted = Index.ReOrder(unsorted, descending);
                return true;
            }
            return base.TryReorder(unsorted, descending, out sorted);
        }
        public IValueIndex<float>? Index;
        public override PropertyType PropertyType => PropertyType.Float;
        public float DefaultValue;
        public override IRangeIndex? ValueIndex => Index;
        public float MinValue = float.MinValue;
        public float MaxValue = float.MaxValue;
        public override object ForceValueType(object value, out bool changed) {
            return FloatPropertyModel.ForceValueType(value, out changed);
        }
        public override void ValidateValue(object value) {
            var v = (float)value;
            if (v > MaxValue) throw new Exception("Value is more than maximum value allowed. ");
            if (v < MinValue) throw new Exception("Value is less than minimum value allowed. ");
        }
        public override object GetDefaultValue() => DefaultValue;
        public static object GetValue(byte[] bytes) => BitConverter.ToSingle(bytes, 0);
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
                    var ranges = RangeGenerators.Floats.GetRanges(v1, v2, facets.RangeCount, facets.RangePowerBase, 20);
                    foreach (var r in ranges) facets.AddValue(new FacetValue(r.Item1, r.Item2, null));
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
                    var from = FloatPropertyModel.ForceValueType(facetValue.Value, out _);
                    var to = facetValue.Value2 == null ? int.MaxValue : FloatPropertyModel.ForceValueType(facetValue.Value2, out _);
                    facetValue.Count = Index.CountInRangeEqual(nodeIds, from, to, facetValue.FromInclusive, facetValue.ToInclusive);
                }
            } else {
                foreach (var facetValue in facets.Values) {
                    var v = FloatPropertyModel.ForceValueType(facetValue.Value, out _);
                    facetValue.Count = Index.CountEqual(nodeIds, v);
                }
            }
        }
        public override bool SatisfyValueRequirement(object value1, object value2, ValueRequirement requirement) {
            var v1 = FloatPropertyModel.ForceValueType(value1, out _);
            var v2 = FloatPropertyModel.ForceValueType(value2, out _);
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
            if (v1 is float f1 && v2 is float f2) return f1 == f2;
            return false;
        }
    }
}
