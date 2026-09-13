using Relatude.DB.Common;
using Relatude.DB.Datamodels.Properties;

namespace Relatude.DB.Query.Data;

/// <summary>
/// The store-level answer to a Coordinates() clause: the located part of the result - the id and the
/// position of every node that has one - and per property chained on, the buckets of the whole
/// result. Count is the number of located nodes, <see cref="Read"/> how many nodes the collection
/// held, TotalCount the size of the result before any paging.
/// </summary>
public class CoordinatesQueryResultData : ICollectionData {
    public CoordinatesQueryResultData(Guid propertyId, CoordinateSet located, int read, int totalCount, PropertyBuckets[] properties) {
        PropertyId = propertyId;
        Located = located;
        Read = read;
        TotalCount = totalCount;
        Properties = properties;
    }
    /// <summary>The geo coordinate property the nodes are placed by.</summary>
    public Guid PropertyId { get; }
    public CoordinateSet Located { get; }
    /// <summary>The nodes that have a position, in the collection's order.</summary>
    public int[] Ids => Located.Ids;
    /// <summary>Where each of them is: <c>Coordinates[i]</c> belongs to <c>Ids[i]</c>.</summary>
    public GeoCoordinate[] Coordinates => Located.Coordinates;
    /// <summary>How many nodes the collection held, located or not.</summary>
    public int Read { get; }
    /// <summary>The buckets of the collection by the properties chained on; a bucket holds every node of the collection, located or not.</summary>
    public PropertyBuckets[] Properties { get; }
    public int TotalCount { get; }
    public int Count => Located.Count;
    public double DurationMs { get; set; }
    public IEnumerable<object?> Values => Located.Coordinates.Select(c => (object?)c);
    public int PageIndexUsed => 0;
    public int? PageSizeUsed => null;
    public ICollectionData ReOrder(IEnumerable<int> newPos) => throw new NotSupportedException("A coordinate result cannot be reordered; order the nodes before Coordinates().");
    public ICollectionData Filter(bool[] keep) => throw new NotSupportedException("A coordinate result cannot be filtered; filter the nodes before Coordinates().");
    public ICollectionData Page(int pageIndex, int pageSize) => throw new NotSupportedException("A coordinate result cannot be paged; page the nodes before Coordinates().");
    public ICollectionData Take(int take) => throw new NotSupportedException("A coordinate result cannot be paged; page the nodes before Coordinates().");
    public ICollectionData Skip(int skip) => throw new NotSupportedException("A coordinate result cannot be paged; page the nodes before Coordinates().");
    public PropertyType GetPropertyType(string name) => throw new NotSupportedException();
}
