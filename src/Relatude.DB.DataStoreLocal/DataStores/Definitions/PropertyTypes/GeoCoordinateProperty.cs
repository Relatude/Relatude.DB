using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
using Relatude.DB.Query.Data;
namespace Relatude.DB.DataStores.Definitions.PropertyTypes;

/// <summary>
/// A property that holds a position, and can say where a whole set of nodes is in one pass (see
/// <see cref="ICoordinateSource"/>). Only the geo coordinate property is one.
/// </summary>
internal interface IGeoProperty {
    /// <summary>
    /// The coordinates of the given nodes, read from the index. False when there is no index to read
    /// - an unindexed property - and the caller has to go to the nodes themselves instead.
    /// </summary>
    bool TryReadCoordinates(IdSet ids, QueryContext ctx, out CoordinateSet coordinates);
}

internal class GeoCoordinateProperty : ValueProperty<GeoCoordinate>, IGeoProperty {
    public GeoCoordinateProperty(GeoCoordinatePropertyModel pm, Definition def) : base(pm, def) {
    }
    /// <summary>
    /// Where every node of the set is. The index holds the value of each id, so this is one probe an
    /// id - on all cores when there are enough of them to be worth splitting - and reads no nodes at
    /// all. On a persisted index a probe is a tree read each, so a set that covers most of the index
    /// is answered from the index's own sequential walk instead, exactly as the pivot's numeric pass
    /// does (see Property.AggregateGrid); that walk comes out in the index's order rather than the
    /// set's, which is why the answer promises no order.
    /// </summary>
    public bool TryReadCoordinates(IdSet ids, QueryContext ctx, out CoordinateSet coordinates) {
        coordinates = null!;
        if (!TryValueGetIndex(ctx, out var index)) return false;
        if (index is OptimizedValueIndex<GeoCoordinate> optimized) index = optimized.DequeueAndGetInner();
        var count = ids.Count;
        if (count == 0) {
            coordinates = new CoordinateSet([], []);
            return true;
        }
        if (!index.HasFastPointLookup && (long)count * 8 >= index.IdCount) {
            var walkIds = new List<int>(Math.Min(count, index.IdCount));
            var walkValues = new List<GeoCoordinate>(walkIds.Capacity);
            foreach (var e in index.Entries) {
                if (e.Value.IsEmpty || !ids.Has(e.Key)) continue;
                walkIds.Add(e.Key);
                walkValues.Add(e.Value);
            }
            coordinates = new CoordinateSet([.. walkIds], [.. walkValues]);
            return true;
        }
        var slices = ids.Partition((int)Math.Min(Environment.ProcessorCount, (long)count / _minIdsPerSlice));
        if (slices.Length <= 1) {
            coordinates = read(index, ids.Enumerate(), count);
            return true;
        }
        var parts = new CoordinateSet[slices.Length];
        Parallel.For(0, slices.Length, i => parts[i] = read(index, slices[i], count / slices.Length + 16));
        var total = 0;
        foreach (var part in parts) total += part.Count;
        var outIds = new int[total];
        var outValues = new GeoCoordinate[total];
        var at = 0;
        foreach (var part in parts) {
            Array.Copy(part.Ids, 0, outIds, at, part.Count);
            Array.Copy(part.Coordinates, 0, outValues, at, part.Count);
            at += part.Count;
        }
        coordinates = new CoordinateSet(outIds, outValues);
        return true;
    }
    const int _minIdsPerSlice = 65_536; // below this the threads cost more than the probes they save
    static CoordinateSet read(IValueIndex<GeoCoordinate> index, IEnumerable<int> ids, int capacity) {
        var foundIds = new List<int>(capacity);
        var values = new List<GeoCoordinate>(capacity);
        foreach (var id in ids) {
            // Empty never enters the index (ShouldIndexValue), so a hit is a real position; the
            // guard is for an index built before that rule and costs nothing
            if (!index.TryGetValue(id, out var v) || v.IsEmpty) continue;
            foundIds.Add(id);
            values.Add(v);
        }
        return new CoordinateSet([.. foundIds], [.. values]);
    }
    protected override void WriteValue(GeoCoordinate v, IAppendStream stream) => stream.WriteULong(v.StorageValue);
    protected override GeoCoordinate ReadValue(IReadStream stream) => GeoCoordinate.FromStorageValue(stream.ReadULong());
    public override PropertyType PropertyType => PropertyType.GeoCoordinate;
    // empty coordinates mean "no location" and never enter the index: spatial filters cannot
    // match them and the missing-value facet bucket counts them via absence from the index
    public override bool ShouldIndexValue(object value) => value is GeoCoordinate g && !g.IsEmpty;
    // the index order is the Morton code (a space filling curve): spatially coherent for range
    // scans, but meaningless as a user-facing sort order
    public override bool TryReorder(IdSet unsorted, bool descending, QueryContext ctx, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out IdSet sorted) {
        sorted = null;
        return false;
    }
    public override bool CanBeFacet() => false;
    public override void ValidateValue(object value, INodeData node) {
        if (value is not GeoCoordinate) throw new Exception("Value must be a GeoCoordinate. ");
    }
    public override bool SatisfyValueRequirement(object? value1, object? value2, ValueRequirement requirement) {
        var v1 = GeoCoordinatePropertyModel.ForceValueType(value1, out _);
        var v2 = GeoCoordinatePropertyModel.ForceValueType(value2, out _);
        return requirement switch {
            ValueRequirement.Equal => v1 == v2,
            ValueRequirement.NotEqual => v1 != v2,
            _ => throw new NotSupportedException("GeoCoordinate values only support equality requirements. "),
        };
    }
    public override bool AreValuesEqual(object v1, object v2) {
        if (v1 is GeoCoordinate g1 && v2 is GeoCoordinate g2) return g1 == g2;
        return false;
    }
}
