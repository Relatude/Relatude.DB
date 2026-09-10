using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Definitions.PropertyTypes;
namespace Relatude.DB.Query.Data;

internal partial class NodeCollectionData : ICoordinateSource {

    /// <summary>
    /// Where the nodes of this collection are (see <see cref="ICoordinateSource"/>). An indexed geo
    /// property answers from its index without touching a node; an unindexed one has nowhere else to
    /// look than the nodes, and is read one at a time - which is fine for the hundreds a form shows
    /// and slow for the millions a map can hold, so a property that is going to be mapped should be
    /// indexed.
    /// </summary>
    public CoordinateSet Coordinates(Guid propertyId, QueryContext ctx) {
        if (!_def.Properties.TryGetValue(propertyId, out var prop)) throw new Exception("Unknown property " + propertyId + ". ");
        if (prop is not IGeoProperty geo) throw new Exception("The property \"" + prop.CodeName + "\" holds " + prop.PropertyType + " rather than a position, so it has no coordinates. ");
        if (_ids.Count == 0) return new CoordinateSet([], []);
        if (geo.TryReadCoordinates(_ids, ctx, out var fromIndex)) return fromIndex;
        var ids = new List<int>();
        var values = new List<GeoCoordinate>();
        foreach (var id in _ids.Enumerate()) {
            if (!_db.TryGetValue<GeoCoordinate>(new PropertyPath(id, propertyId), out var value, ctx) || value.IsEmpty) continue;
            ids.Add(id);
            values.Add(value);
        }
        return new CoordinateSet([.. ids], [.. values]);
    }
}
