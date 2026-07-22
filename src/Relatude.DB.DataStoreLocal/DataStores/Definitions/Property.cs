using System.Diagnostics.CodeAnalysis;
using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Definitions.PropertyTypes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Definitions {
    public interface IPropertyContainsValue {
        bool ContainsValue(object value, QueryContext ctx);
    }
    public interface IProperty {
        object ForceValueType(object value, out bool changed);
    }
    public interface IValueProperty : IProperty {
        IdSet FilterRanges(IdSet set, object from, object to, QueryContext ctx);
        bool TryReorder(IdSet unsorted, bool descending, QueryContext ctx, [MaybeNullWhen(false)] out IdSet sorted);
        IdSet WhereIn(IdSet ids, IEnumerable<object?> values, QueryContext ctx);
    }
    internal abstract class ValueProperty<T> : Property, IValueProperty where T : notnull {
        IndexUtil<IValueIndex<T>> _indexUtil = new();
        public bool TryValueGetIndex(QueryContext ctx, [MaybeNullWhen(false)] out IValueIndex<T> index) => _indexUtil.TryGetIndex(ctx, out index);
        public IValueIndex<T> GetValueIndex(QueryContext ctx) => _indexUtil.GetIndex(ctx);
        public ValueProperty(PropertyModel pm, Definition def) : base(pm, def) {
        }
        internal override void Initalize(DataStoreLocal store, Definition def, SettingsLocal config, IIOProvider io, AIEngine? ai) {
            if (Indexed) _indexUtil.Initalize(IndexFactory.CreateValueIndexes<T>(store, this, null, WriteValue, ReadValue), Model.CultureSensitive, AllIndexes);
        }
        protected abstract void WriteValue(T v, IAppendStream stream);
        protected abstract T ReadValue(IReadStream stream);
        public override object ForceValueType(object value, out bool changed) => PropertyModel.ForceValueAnyType<T>(value, Model.PropertyType, out changed);
        public override bool TryReorder(IdSet unsorted, bool descending, QueryContext ctx, [MaybeNullWhen(false)] out IdSet sorted) {
            if (TryValueGetIndex(ctx, out var index)) {
                sorted = index.ReOrder(unsorted, descending);
                return true;
            }
            return base.TryReorder(unsorted, descending, ctx, out sorted);
        }
        public bool ContainsValue(object value, QueryContext ctx) {
            return GetValueIndex(ctx).ContainsValue((T)value);
        }
        public override bool CanBeFacet() => Indexed;
        static readonly RangeGenerator<T>? _rangeGenerator = RangeGenerators.TryGet<T>();
        const int _autoRangeMinUniqueValues = 25; // scalar facets with more distinct values than this are bucketed into ranges unless value facets were explicitly requested
        T coerce(object v) => PropertyModel.ForceValueAnyType<T>(v, Model.PropertyType, out _);
        public override IdSet FilterFacets(Facets facets, IdSet nodeIds, QueryContext ctx) {
            var index = GetValueIndex(ctx);
            List<T> values = new();
            List<IdSet> parts = new();
            foreach (var fv in facets.Values) {
                if (!fv.Selected) continue;
                if (fv.Value == null) { // the missing-value bucket
                    parts.Add(whereMissing(index, nodeIds));
                } else if (fv.Value2 == null) {
                    values.Add(coerce(fv.Value));
                } else { // range bucket
                    var s = index.Filter(nodeIds, fv.FromInclusive ? IndexOperator.GreaterOrEqual : IndexOperator.Greater, coerce(fv.Value));
                    parts.Add(index.Filter(s, fv.ToInclusive ? IndexOperator.SmallerOrEqual : IndexOperator.Smaller, coerce(fv.Value2)));
                }
            }
            if (values.Count > 0) parts.Add(index.FilterInValues(nodeIds, values));
            if (parts.Count == 0) return nodeIds;
            var result = parts[0];
            for (var i = 1; i < parts.Count; i++) result = Definition.Sets.Union(result, parts[i]);
            return result;
        }
        IdSet whereMissing(IValueIndex<T> index, IdSet nodeIds) {
            if (index.IdCount == 0) return nodeIds;
            var having = index.FilterRanges(nodeIds, [new Tuple<T, T>(index.MinValue()!, index.MaxValue()!)]);
            return Definition.Sets.Difference(nodeIds, having);
        }

        public IdSet FilterRanges(IdSet set, object from, object to, QueryContext ctx) {
            var index = GetValueIndex(ctx);
            return index.FilterRangesObject(set, from, to);
        }

        public override Facets GetDefaultFacets(Facets? given, QueryContext ctx) {
            var index = GetValueIndex(ctx);
            var facets = new Facets(Model);
            facets.CopyOptionsFrom(given);
            if (given != null && given.HasValues()) { // caller supplied the buckets (custom values or ranges)
                foreach (var f in given.Values) facets.AddValue(f.Clone());
                facets.IsRangeFacet = given.Values.Any(f => f.Value2 != null);
            } else if (useRangeBuckets(given, index)) {
                addRangeBuckets(facets, index);
            } else {
                foreach (var value in index.UniqueValues) facets.AddValue(new FacetValue(value));
                facets.IsRangeFacet = false;
            }
            if (facets.IncludeMissing) facets.AddValue(new FacetValue(null));
            return facets;
        }
        bool useRangeBuckets(Facets? given, IValueIndex<T> index) {
            if (_rangeGenerator == null || index.ValueCount < 2) return false;
            if (given?.IsRangeFacet != null) return given.IsRangeFacet.Value; // AddRangeFacet/AddValueFacet made the choice explicit
            if (Model is IScalarProperty sp && sp.FacetRangeCount > 0) return true;
            return index.ValueCount > _autoRangeMinUniqueValues;
        }
        void addRangeBuckets(Facets facets, IValueIndex<T> index) {
            var min = index.MinValue()!;
            var max = index.MaxValue()!;
            var ranges = _rangeGenerator!.GetRanges(min, max, facets.RangeCount, facets.RangePowerBase, 10);
            for (var i = 0; i < ranges.Count; i++) {
                var last = i == ranges.Count - 1;
                // half-open buckets built from the generated boundaries, so continuous types
                // (double, DateTime, ...) are fully covered with no gaps between buckets;
                // the first boundary is forced down to the real min since the generator rounds
                // boundaries to whole numbers (which could round the lowest values out):
                var from = i == 0 ? min : ranges[i].Item1;
                var to = last ? ranges[i].Item2 : ranges[i + 1].Item1;
                facets.AddValue(new FacetValue(from, to, null) { ToInclusive = last });
            }
            facets.IsRangeFacet = true;
            try {
                facets.MinValue = Convert.ToDouble(min);
                facets.MaxValue = Convert.ToDouble(max);
            } catch { } // not double-representable (DateTime/TimeSpan)
        }
        public override void CountFacets(IdSet nodeIds, Facets facets, QueryContext ctx) {
            var index = GetValueIndex(ctx);
            if (countFacetsInOnePassOverSet(index, nodeIds, facets)) return;
            foreach (var fv in facets.Values) {
                if (fv.Value == null) {
                    var having = index.IdCount == 0 ? 0 : index.CountInRangeEqual(nodeIds, index.MinValue()!, index.MaxValue()!, true, true);
                    fv.Count = nodeIds.Count - having;
                } else if (fv.Value2 == null) {
                    fv.Count = index.CountEqual(nodeIds, coerce(fv.Value));
                } else {
                    fv.Count = index.CountInRangeEqual(nodeIds, coerce(fv.Value), coerce(fv.Value2), fv.FromInclusive, fv.ToInclusive);
                }
            }
        }
        // When the set is much smaller than the index, one pass over the set with per-id value
        // lookups beats the per-bucket path above: the buckets' id sets are cached per index
        // state, but any write invalidates them, and rebuilding costs a scan of every indexed id
        // per facet. Small sets also probe faster than they'd intersect. For larger sets the
        // cached per-bucket intersections win, so the cutoff is deliberately conservative:
        bool countFacetsInOnePassOverSet(IValueIndex<T> index, IdSet nodeIds, Facets facets) {
            if (!facets.HasValues() || (long)nodeIds.Count * 16 > index.IdCount) return false;
            Dictionary<T, FacetValue>? byValue = null;
            List<(T from, T to, FacetValue fv)>? ranges = null;
            FacetValue? missing = null;
            foreach (var fv in facets.Values) {
                fv.Count = 0;
                if (fv.Value == null) missing = fv;
                else if (fv.Value2 == null) {
                    byValue ??= new();
                    if (!byValue.TryAdd(coerce(fv.Value), fv)) return false; // duplicate buckets: let the per-bucket path count them
                } else (ranges ??= []).Add((coerce(fv.Value), coerce(fv.Value2), fv));
            }
            var comparer = ValueIndex<T>.comparer;
            foreach (var id in nodeIds.Enumerate()) {
                if (!index.TryGetValue(id, out var v)) {
                    if (missing != null) missing.Count++;
                    continue;
                }
                if (byValue != null && byValue.TryGetValue(v, out var fv)) fv.Count++;
                if (ranges != null) {
                    foreach (var r in ranges) {
                        if (r.fv.FromInclusive ? comparer.Compare(v, r.from) < 0 : comparer.Compare(v, r.from) <= 0) continue;
                        if (r.fv.ToInclusive ? comparer.Compare(v, r.to) <= 0 : comparer.Compare(v, r.to) < 0) r.fv.Count++;
                    }
                }
            }
            return true;
        }
        public IdSet WhereIn(IdSet ids, IEnumerable<object?> values, QueryContext ctx) {
            List<T> typedValues = new();
            foreach (var value in values) {
                if (value == null) continue;
                typedValues.Add(PropertyModel.ForceValueAnyType<T>(value, Model.PropertyType, out _));
            }
            return GetValueIndex(ctx).FilterInValues(ids, typedValues);
        }
    }
    internal abstract class Property : IProperty {
        static int _idCnt = 0;
        public int __Id_transient;  // stateless
        public Property(PropertyModel pm, Definition def) {
            Id = pm.Id;
            __Id_transient = Interlocked.Increment(ref _idCnt);
            Model = pm;
            CodeName = pm.CodeName;
            ReadAccess = pm.ReadAccess;
            WriteAccess = pm.WriteAccess;
            Indexed = pm.Indexed || pm.UniqueValues;
            if (pm is IPropertyModelUniqueContraints pmuv) UniqueValues = pmuv.UniqueValues;
            AllIndexes = [];
            Definition = def;
        }
        public bool Indexed { get; }
        public virtual bool TryReorder(IdSet unsorted, bool descending, QueryContext ctx, [MaybeNullWhen(false)] out IdSet sorted) {
            sorted = null;
            return false;
        }
        public readonly PropertyModel Model;
        internal abstract void Initalize(DataStoreLocal store, Definition def, SettingsLocal config, IIOProvider io, AIEngine? ai);
        public static Property Create(PropertyModel pm, Definition def) {
            if (pm is BooleanPropertyModel b) return new BooleanProperty(b, def);
            if (pm is ByteArrayPropertyModel bt) return new ByteArrayProperty(bt, def);
            if (pm is IntegerPropertyModel i) return new IntegerProperty(i, def);
            if (pm is LongPropertyModel l) return new LongProperty(l, def);
            if (pm is DecimalPropertyModel de) return new DecimalProperty(de, def);
            if (pm is DoublePropertyModel d) return new DoubleProperty(d, def);
            if (pm is FloatPropertyModel f) return new FloatProperty(f, def);
            if (pm is GuidPropertyModel g) return new GuidProperty(g, def);
            if (pm is DateTimePropertyModel dt) return new DateTimeProperty(dt, def);
            if (pm is DateTimeOffsetPropertyModel dto) return new DateTimeOffsetProperty(dto, def);
            if (pm is TimeSpanPropertyModel t) return new TimeSpanProperty(t, def);
            if (pm is StringPropertyModel p) return new StringProperty(p, def);
            if (pm is StringArrayPropertyModel pa) return new StringArrayProperty(pa, def);
            if (pm is RelationPropertyModel ra) return new RelationProperty(ra, def);
            if (pm is FilePropertyModel fa) return new FileProperty(fa, def);
            if (pm is FloatArrayPropertyModel far) return new FloatArrayProperty(far, def);
            if(pm is EmbeddedPropertyModel inn) return new EmbeddedProperty(inn, def);
            if(pm is ReferencePropertyModel rf) return new ReferenceProperty(rf, def);
            throw new Exception("Unknown property type. ");
        }
        public abstract void ValidateValue(object value, INodeData node);
        public abstract object ForceValueType(object value, out bool changed);
        public virtual bool CanBeFacet() => false;
        public virtual void CountFacets(IdSet nodeIds, Facets facets, QueryContext ctx) => throw new NotSupportedException();
        public virtual IdSet FilterFacets(Facets facets, IdSet nodeIds, QueryContext ctx) => throw new NotSupportedException();
        public virtual Facets GetDefaultFacets(Facets? given, QueryContext ctx) => throw new NotSupportedException();

        readonly public Definition Definition;
        readonly public Guid Id;
        readonly public string CodeName;
        readonly public Guid ReadAccess;
        readonly public Guid WriteAccess;
        readonly public bool UniqueValues;
        internal List<IIndex> AllIndexes { get; }

        public abstract PropertyType PropertyType { get; }

        public void CompressMemory() {
            foreach (var item in AllIndexes) item.CompressMemory();
        }
        public virtual object TransformFromOuterToInnerValue(object value, INodeData? oldNodeData) {
            return value;
        }
        public virtual bool IsReferenceTypeAndMustCopy() {
            return false;
        }
        public virtual bool IsNodeRelevantForIndex(Guid nodeTypeId, IIndex index) => true;
        public virtual bool SatisfyValueRequirement(object? value1, object? value2, ValueRequirement requirement) {
            throw new NotImplementedException("The property " + CodeName + " of type " + PropertyType + " cannot support value requirements. ");
        }
        public abstract bool AreValuesEqual(object v1, object v2);// => v1.Equals(v2);
    }
}
