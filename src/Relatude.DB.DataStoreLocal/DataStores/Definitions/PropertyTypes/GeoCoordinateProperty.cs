using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Definitions.PropertyTypes;

internal class GeoCoordinateProperty : ValueProperty<GeoCoordinate> {
    public GeoCoordinateProperty(GeoCoordinatePropertyModel pm, Definition def) : base(pm, def) {
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
