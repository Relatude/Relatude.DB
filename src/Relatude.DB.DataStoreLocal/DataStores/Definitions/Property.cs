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
    /// <summary>
    /// An array property whose index keeps a set of ids per unique element, so
    /// "the array holds this value" can be answered from the index instead of per row.
    /// Implemented by the string, enum and guid array properties. Float and byte arrays do not
    /// implement it (they have no per element index) and fall back to row evaluation.
    /// </summary>
    public interface IArrayProperty : IProperty {
        /// <summary>
        /// The ids in set whose array holds an element equal to value. Empty when value cannot be
        /// coerced to the element type, matching how an unparsable facet selection matches nothing.
        /// Only valid when the property is indexed.
        /// </summary>
        IdSet FilterContainsElement(IdSet set, object? value, QueryContext ctx);
        /// <summary>Worst case count of <see cref="FilterContainsElement"/>, used to order AND/OR operands.</summary>
        int MaxCountContainsElement(object? value, QueryContext ctx);
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
        public override bool CanBeFacet() => Indexed && !Model.NotFacet;
        public override bool CanBeAutomaticFacet(QueryContext ctx) {
            if (!CanBeFacet()) return false;
            if (!TryValueGetIndex(ctx, out var index)) return true; // no index to ask: unchanged behaviour, GetDefaultFacets decides
            if (useRangeBuckets(null, index)) return true; // range buckets: count bounded by RangeCount
            return index.ValueCount <= MaxAutomaticFacetValues; // one bucket per unique value
        }
        // selected buckets combine with OR, so the estimate is the sum of whole-index bucket
        // counts (maintained counts / O(log n) tree probes, never id enumeration - see
        // IValueIndex.CountEqual(value)/CountInRange)
        public override long EstimateFilterFacetsMaxCount(Facets facets, IdSet source, QueryContext ctx) {
            if (!TryValueGetIndex(ctx, out var index)) return long.MaxValue;
            long total = 0;
            foreach (var fv in facets.Values) {
                if (!fv.Selected) continue;
                if (fv.Value == null) total += Math.Max(0, source.Count - index.IdCount); // the missing-value bucket
                else if (fv.Value2 == null) total += index.CountEqual(coerce(fv.Value));
                else total += index.CountInRange(coerce(fv.Value), coerce(fv.Value2), fv.FromInclusive, fv.ToInclusive);
            }
            return total;
        }
        static readonly RangeGenerator<T>? _rangeGenerator = RangeGenerators.TryGet<T>();
        const int _autoRangeMinUniqueValues = 25; // scalar facets with more distinct values than this are bucketed into ranges unless value facets were explicitly requested
        protected virtual bool AutoRangeBuckets => true; // false suppresses the automatic value->range switch (ranges can still be requested explicitly)
        protected virtual T coerce(object v) => PropertyModel.ForceValueAnyType<T>(v, Model.PropertyType, out _);
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
                } else { // range bucket: one bounded in-range collect, not (>=from) ∩ (<=to) half-range sets
                    var inRange = Definition.Sets.WhereValueInRange(index, coerce(fv.Value), coerce(fv.Value2), fv.FromInclusive, fv.ToInclusive);
                    parts.Add(Definition.Sets.Intersection(nodeIds, inRange));
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
            if (!AutoRangeBuckets) return false;
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
                // the generator's first boundary is at or below the real min (it aligns down to a
                // "nice" step multiple), so it is replaced with the real min for display:
                var from = i == 0 ? min : ranges[i].Item1;
                var to = last ? ranges[i].Item2 : ranges[i + 1].Item1;
                facets.AddValue(new FacetValue(from, to, null) { ToInclusive = last });
            }
            facets.IsRangeFacet = true;
        }
        // Builds the caches the first FILTERED facet query would otherwise build inline: the
        // per-value id sets of the equality buckets and the default range-bucket sets. Unfiltered
        // queries never need them (they count from the index's own maintained counts, see
        // CountFacets), so on a persisted index the user's first facet selection pays a full
        // value-tree read per facet property - hundreds of ms at millions of nodes - unless these
        // sets are built here first (see DataStoreLocal.warmIndexesInBackground).
        const int _maxWarmBuckets = 256; // above any realistic facet UI; caps warm cost and cache churn
        internal override void WarmFacetCaches(QueryContext ctx) {
            if (!CanBeFacet()) return;
            if (!TryValueGetIndex(ctx, out var index)) return;
            if (index.HasFastPointLookup) return; // memory-backed: facet counting never reads a tree
            if (index.ValueCount == 0) return;
            // a high-cardinality property without a range generator would need one cached set per
            // distinct value; no realistic facet UI shows those, so leave it cold:
            if (_rangeGenerator == null && index.ValueCount > _maxWarmBuckets) return;
            var facets = GetDefaultFacets(null, ctx);
            if (facets.Values.Count > _maxWarmBuckets) return;
            var nodeIds = Definition.GetAllIdsForType(Model.NodeType, ctx);
            if (nodeIds.Count == 0) return;
            CountFacets(nodeIds, facets, ctx, nodeIdsCoverIndex: false);
        }
        public override void CountFacets(IdSet nodeIds, Facets facets, QueryContext ctx, bool nodeIdsCoverIndex) {
            var index = GetValueIndex(ctx);
            // the optimized wrapper re-checks its write-behind queue under a lock on EVERY call;
            // flush it once here and use the raw index, so the per-id counting loops below stay
            // lock free (they run millions of TryGetValue calls, possibly on several threads)
            if (index is OptimizedValueIndex<T> optimized) index = optimized.DequeueAndGetInner();
            if (nodeIdsCoverIndex) {
                // every id in the index is in nodeIds, so buckets are counted from the index's own
                // maintained counts - no set materialization or intersection at all
                foreach (var fv in facets.Values) {
                    if (fv.Value == null) fv.Count = Math.Max(0, nodeIds.Count - index.IdCount);
                    else if (fv.Value2 == null) fv.Count = index.CountEqual(coerce(fv.Value));
                    else fv.Count = index.CountInRange(coerce(fv.Value), coerce(fv.Value2), fv.FromInclusive, fv.ToInclusive);
                }
                return;
            }
            // equality buckets count against the per-value id sets (word-parallel when both sides
            // are bit sets, so fast at any scale); range buckets and the missing bucket count in
            // one pass over the set with per-id value lookups - unless lookups are tree/disk bound
            // (persisted index) and the set is large, where per-bucket range counts win:
            var onePass = (index.HasFastPointLookup || (long)nodeIds.Count * 16 < index.IdCount)
                && countRangesAndMissingInOnePass(index, nodeIds, facets);
            foreach (var fv in facets.Values) {
                if (fv.Value != null && fv.Value2 == null) {
                    fv.Count = index.CountEqual(nodeIds, coerce(fv.Value));
                } else if (!onePass) {
                    if (fv.Value == null) {
                        var having = index.IdCount == 0 ? 0 : index.CountInRangeEqual(nodeIds, index.MinValue()!, index.MaxValue()!, true, true);
                        fv.Count = nodeIds.Count - having;
                    } else {
                        fv.Count = index.CountInRangeEqual(nodeIds, coerce(fv.Value), coerce(fv.Value2!), fv.FromInclusive, fv.ToInclusive);
                    }
                }
            }
        }
        bool countRangesAndMissingInOnePass(IValueIndex<T> index, IdSet nodeIds, Facets facets) {
            List<(T from, T to, FacetValue fv)>? ranges = null;
            FacetValue? missing = null;
            foreach (var fv in facets.Values) {
                if (fv.Value == null) { missing = fv; fv.Count = 0; }
                else if (fv.Value2 != null) { (ranges ??= []).Add((coerce(fv.Value), coerce(fv.Value2), fv)); fv.Count = 0; }
            }
            if (ranges == null && missing == null) return true; // nothing that needs the pass
            var sets = Definition.Sets;
            // repeated queries over the same set (typically page navigation) are served from the
            // count cache; the keys match CountInRangeEqual's so both paths share entries:
            var allCached = true;
            if (missing != null && allCached) {
                if (sets.TryGetCachedCount(SetOperation.CountMissing, index.StateId, nodeIds.StateId, null, out var cached)) missing.Count = cached;
                else allCached = false;
            }
            if (ranges != null && allCached) {
                foreach (var r in ranges) {
                    if (sets.TryGetCachedCount(SetOperation.CountInRange, index.StateId, nodeIds.StateId, rangeKey(index, r.from, r.to, r.fv), out var cached)) r.fv.Count = cached;
                    else { allCached = false; break; }
                }
            }
            if (allCached) return true;
            if (missing != null) missing.Count = 0; // reset any partial cache assignments before the pass
            if (ranges != null) foreach (var r in ranges) r.fv.Count = 0;
            var comparer = ValueIndex<T>.comparer;
            // the auto-generated buckets are contiguous ascending half-open ranges, so every
            // value belongs to at most one bucket, found by a binary search over the shared
            // boundaries - much cheaper than testing each range per id:
            var contiguous = ranges != null && areContiguousAscending(ranges, comparer);
            T[]? froms = null;
            if (contiguous) {
                froms = new T[ranges!.Count];
                for (var i = 0; i < ranges.Count; i++) froms[i] = ranges[i].from;
            }
            // the pass is embarrassingly parallel: big sets are split into slices counted with
            // local counters on all cores, then merged. Everything a slice reads is immutable
            // snapshot state (writers are blocked by the store's read lock for the whole query)
            const int minIdsPerSlice = 131_072; // below this the parallel overhead outweighs the scan
            var slices = nodeIds.Partition((int)Math.Min(Environment.ProcessorCount, (long)nodeIds.Count / minIdsPerSlice));
            int[] rangeCounts;
            int missingCount;
            if (slices.Length == 1) {
                (rangeCounts, missingCount) = countSlice(slices[0], index, ranges, froms, comparer, missing != null);
            } else {
                rangeCounts = new int[ranges?.Count ?? 0];
                missingCount = 0;
                var mergeLock = new object();
                Parallel.ForEach(slices, slice => {
                    var (rc, mc) = countSlice(slice, index, ranges, froms, comparer, missing != null);
                    lock (mergeLock) {
                        for (var i = 0; i < rangeCounts.Length; i++) rangeCounts[i] += rc[i];
                        missingCount += mc;
                    }
                });
            }
            if (missing != null) missing.Count = missingCount;
            if (ranges != null) for (var i = 0; i < ranges.Count; i++) ranges[i].fv.Count = rangeCounts[i];
            if (missing != null) sets.SetCachedCount(SetOperation.CountMissing, index.StateId, nodeIds.StateId, null, missing.Count);
            if (ranges != null) foreach (var r in ranges) sets.SetCachedCount(SetOperation.CountInRange, index.StateId, nodeIds.StateId, rangeKey(index, r.from, r.to, r.fv), r.fv.Count);
            return true;
        }
        // counts one slice of the set into local counters (parallel-safe: reads only immutable
        // snapshot state, writes only its own arrays). froms != null selects the binary-search
        // path for contiguous ascending buckets; otherwise every range is tested per id.
        static (int[] rangeCounts, int missingCount) countSlice(IEnumerable<int> ids, IValueIndex<T> index,
            List<(T from, T to, FacetValue fv)>? ranges, T[]? froms, IComparer<T> comparer, bool countMissing) {
            var counts = new int[ranges?.Count ?? 0];
            var missing = 0;
            if (froms != null) {
                var last = ranges![^1];
                foreach (var id in ids) {
                    if (!index.TryGetValue(id, out var v)) {
                        if (countMissing) missing++;
                        continue;
                    }
                    if (comparer.Compare(v, froms[0]) < 0) continue; // below the first bucket
                    if (last.fv.ToInclusive ? comparer.Compare(v, last.to) > 0 : comparer.Compare(v, last.to) >= 0) continue; // beyond the last bucket
                    var idx = Array.BinarySearch(froms, v, comparer);
                    if (idx < 0) idx = ~idx - 1; // not an exact boundary: the bucket starting just before v
                    counts[idx]++;
                }
            } else {
                foreach (var id in ids) {
                    if (!index.TryGetValue(id, out var v)) {
                        if (countMissing) missing++;
                        continue;
                    }
                    if (ranges != null) {
                        for (var i = 0; i < ranges.Count; i++) {
                            var r = ranges[i];
                            if (r.fv.FromInclusive ? comparer.Compare(v, r.from) < 0 : comparer.Compare(v, r.from) <= 0) continue;
                            if (r.fv.ToInclusive ? comparer.Compare(v, r.to) <= 0 : comparer.Compare(v, r.to) < 0) counts[i]++;
                        }
                    }
                }
            }
            return (counts, missing);
        }
        // true when the buckets form one ascending chain of non-empty half-open ranges (each
        // interior bucket's exclusive end is the next bucket's inclusive start) - the shape
        // addRangeBuckets generates. Only then can bucket membership be found by binary search;
        // anything else (overlaps, gaps, inclusive interior ends) keeps the general per-range test.
        static bool areContiguousAscending(List<(T from, T to, FacetValue fv)> ranges, IComparer<T> comparer) {
            for (var i = 0; i < ranges.Count; i++) {
                var r = ranges[i];
                if (!r.fv.FromInclusive) return false;
                if (comparer.Compare(r.from, r.to) >= 0) return false; // empty or reversed bucket
                if (i < ranges.Count - 1) {
                    if (r.fv.ToInclusive) return false; // boundary value must belong to exactly one bucket
                    if (comparer.Compare(r.to, ranges[i + 1].from) != 0) return false;
                }
            }
            return true;
        }
        static object[] rangeKey(IValueIndex<T> index, T from, T to, FacetValue fv) =>
            [.. index.GetCacheKey(from, fv.FromInclusive ? QueryType.GreaterOrEqual : QueryType.Greater),
             .. index.GetCacheKey(to, fv.ToInclusive ? QueryType.LessOrEqual : QueryType.Less), fv.FromInclusive, fv.ToInclusive];
        public IdSet WhereIn(IdSet ids, IEnumerable<object?> values, QueryContext ctx) {
            List<T> typedValues = new();
            foreach (var value in values) {
                if (value == null) continue;
                typedValues.Add(PropertyModel.ForceValueAnyType<T>(value, Model.PropertyType, out _));
            }
            return GetValueIndex(ctx).FilterInValues(ids, typedValues);
        }

        // ── pivot support ──
        static readonly Func<T, double>? _toDouble = PivotNumeric.TryGetConverter<T>();
        internal override bool CanAggregate => Indexed;
        internal override bool IsNumeric => _toDouble != null;
        internal override bool TryGetMinMax(QueryContext ctx, out object? min, out object? max) {
            min = max = null;
            if (!TryValueGetIndex(ctx, out var index) || index.IdCount == 0) return false;
            min = index.MinValue();
            max = index.MaxValue();
            return min != null && max != null;
        }
        // one pass over the set with per-id value lookups: no node is read, only the index. On the
        // memory index a lookup is an array read; on a persisted index a tree/disk probe, so the
        // optimized wrapper is flushed once and bypassed, as CountFacets does for its counting loops
        internal override PivotAggregate Aggregate(IdSet set, QueryContext ctx, bool distinct) {
            var index = GetValueIndex(ctx);
            if (index is OptimizedValueIndex<T> optimized) index = optimized.DequeueAndGetInner();
            var agg = new PivotAggregate { Min = double.PositiveInfinity, Max = double.NegativeInfinity };
            HashSet<T>? seen = distinct ? new() : null;
            var toDouble = _toDouble;
            foreach (var id in set.Enumerate()) {
                if (!index.TryGetValue(id, out var v)) continue;
                agg.CountWithValue++;
                seen?.Add(v);
                if (toDouble == null) continue;
                var d = toDouble(v);
                agg.Sum += d;
                if (d < agg.Min) agg.Min = d;
                if (d > agg.Max) agg.Max = d;
            }
            agg.DistinctCount = seen?.Count ?? 0;
            return agg;
        }
    }
    /// <summary>What one pass over a set gives a pivot cell for one property (see Property.Aggregate).</summary>
    internal struct PivotAggregate {
        public int CountWithValue;
        public double Sum;
        public double Min;
        public double Max;
        public int DistinctCount;
    }
    internal static class PivotNumeric {
        // the numeric property types a Sum/Average/Min/Max measure accepts, each read as a double
        internal static Func<T, double>? TryGetConverter<T>() {
            if (typeof(T) == typeof(int)) return (Func<T, double>)(object)(Func<int, double>)(v => v);
            if (typeof(T) == typeof(long)) return (Func<T, double>)(object)(Func<long, double>)(v => v);
            if (typeof(T) == typeof(double)) return (Func<T, double>)(object)(Func<double, double>)(v => v);
            if (typeof(T) == typeof(float)) return (Func<T, double>)(object)(Func<float, double>)(v => v);
            if (typeof(T) == typeof(decimal)) return (Func<T, double>)(object)(Func<decimal, double>)(v => (double)v);
            if (typeof(T) == typeof(byte)) return (Func<T, double>)(object)(Func<byte, double>)(v => v);
            return null;
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
            if (pm is GeoCoordinatePropertyModel geo) return new GeoCoordinateProperty(geo, def);
            if (pm is TimeSpanPropertyModel t) return new TimeSpanProperty(t, def);
            if (pm is StringPropertyModel p) return new StringProperty(p, def);
            if (pm is StringArrayPropertyModel pa) return new StringArrayProperty(pa, def);
            if (pm is ReferencesPropertyModel rs) return new ReferencesProperty(rs, def); // before GuidArrayPropertyModel: ReferencesPropertyModel derives from it
            if (pm is GuidArrayPropertyModel ga) return new GuidArrayProperty(ga, def);
            if (pm is EnumArrayPropertyModel ea) return new EnumArrayProperty(ea, def);
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
        // Facets evaluated automatically (the query asked for facets without naming any property)
        // skip properties that would produce one bucket per unique value when there are too many of
        // them: hundreds of string/guid/relation buckets are useless in a facet UI and expensive to
        // build and count. Properties bucketed into ranges are unaffected, their bucket count is
        // bounded by the range count. Explicitly requested facets are never skipped, no matter the
        // cardinality. TODO: make the limit configurable (per store and/or per query).
        public const int MaxAutomaticFacetValues = 100;
        public virtual bool CanBeAutomaticFacet(QueryContext ctx) => CanBeFacet();
        // Bounded count: never enumerates more than the limit allows, whatever the cardinality.
        protected static bool tooManyValuesForAutomaticFacet<T>(IEnumerable<T> uniqueValues)
            => uniqueValues.Take(MaxAutomaticFacetValues + 1).Count() > MaxAutomaticFacetValues;
        // Cheap worst-case estimate of how many ids FilterFacets can return for the current
        // selection, used to run the most selective selection filters first. Same contract as
        // IBooleanNativeExpression.MaxCount: must be fast and is only an estimation.
        // long.MaxValue = no cheap estimate available; such properties filter last.
        public virtual long EstimateFilterFacetsMaxCount(Facets facets, IdSet source, QueryContext ctx) => long.MaxValue;
        public virtual void CountFacets(IdSet nodeIds, Facets facets, QueryContext ctx, bool nodeIdsCoverIndex) => throw new NotSupportedException();
        // Builds whatever caches the first filtered facet query on this property would otherwise
        // build inline (see DataStoreLocal.warmIndexesInBackground). Default: nothing to warm.
        internal virtual void WarmFacetCaches(QueryContext ctx) { }
        // true when every id this property's index can contain is of the query type or one of its
        // descendants (the property's declaring type lies within the query type's subtree), so a
        // count over the whole index equals a count against the full type set
        public bool IndexCoveredByQueryType(Guid queryTypeId) =>
            Definition.Datamodel.NodeTypes.TryGetValue(Model.NodeType, out var declaring) && declaring.ThisAndAllInheritedTypes.ContainsKey(queryTypeId);
        public virtual IdSet FilterFacets(Facets facets, IdSet nodeIds, QueryContext ctx) => throw new NotSupportedException();
        public virtual Facets GetDefaultFacets(Facets? given, QueryContext ctx) => throw new NotSupportedException();
        // pivot measures: only indexed scalar value properties can be aggregated (see ValueProperty<T>);
        // Sum/Average/Min/Max need a numeric one, CountDistinct any of them
        internal virtual bool CanAggregate => false;
        internal virtual bool IsNumeric => false;
        internal virtual bool TryGetMinMax(QueryContext ctx, out object? min, out object? max) { min = max = null; return false; }
        internal virtual PivotAggregate Aggregate(IdSet set, QueryContext ctx, bool distinct) => throw new NotSupportedException("The property " + CodeName + " of type " + PropertyType + " cannot be aggregated. ");

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
        // false excludes a specific VALUE from this property's indexes (e.g. empty GeoCoordinates,
        // which mean "no location"). Must be a pure function of the value: index and de-index of
        // the same value have to agree, or removals would target entries that were never added.
        public virtual bool ShouldIndexValue(object value) => true;
        public virtual bool SatisfyValueRequirement(object? value1, object? value2, ValueRequirement requirement) {
            throw new NotImplementedException("The property " + CodeName + " of type " + PropertyType + " cannot support value requirements. ");
        }
        public abstract bool AreValuesEqual(object v1, object v2);// => v1.Equals(v2);
    }
}
