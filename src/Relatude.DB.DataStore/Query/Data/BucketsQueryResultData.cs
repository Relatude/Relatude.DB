using Relatude.DB.Datamodels.Properties;

namespace Relatude.DB.Query.Data;

/// <summary>The buckets of a result by one property: one per value, or per range of values, each with the ids of the result's nodes that hold it.</summary>
public sealed class PropertyBuckets {
    public PropertyBuckets(Guid propertyId, IReadOnlyList<ValueBucket> buckets) {
        PropertyId = propertyId;
        Buckets = buckets;
    }
    public Guid PropertyId { get; }
    /// <summary>In the property's natural order: values sorted, ranges as generated, the missing bucket last.</summary>
    public IReadOnlyList<ValueBucket> Buckets { get; }
    /// <summary>Whether the buckets are ranges of values rather than single values.</summary>
    public bool IsRange => Buckets.Any(b => b.Value.Value2 != null);
}

/// <summary>
/// The store-level answer to a Buckets() clause: the ids of the result, in its order, and per property
/// the buckets they fall in. Wrapped as collection data so it travels the same path as every other
/// query result (duration stamping, logging); Count is the number of ids, TotalCount the size of the
/// result before any paging.
/// </summary>
public class BucketsQueryResultData : ICollectionData {
    public BucketsQueryResultData(int[] ids, int[]? order, int totalCount, PropertyBuckets[] properties) {
        Ids = ids;
        Order = order;
        TotalCount = totalCount;
        Properties = properties;
    }
    /// <summary>The nodes bucketed, in the collection's order.</summary>
    public int[] Ids { get; }
    /// <summary>
    /// The ids in the order of the SortBy property, as positions into <see cref="Ids"/>: a permutation
    /// of them, the nodes the index has no value for last in the collection's own order. Null when no
    /// SortBy was asked for, or when the property has no index to sort by.
    /// </summary>
    public int[]? Order { get; }
    public PropertyBuckets[] Properties { get; }
    public int TotalCount { get; }
    public int Count => Ids.Length;
    public double DurationMs { get; set; }
    public IEnumerable<object?> Values => Ids.Select(id => (object?)id);
    public int PageIndexUsed => 0;
    public int? PageSizeUsed => null;
    public ICollectionData ReOrder(IEnumerable<int> newPos) => throw new NotSupportedException("A bucket result cannot be reordered; use SortBy, or order the nodes before Buckets().");
    public ICollectionData Filter(bool[] keep) => throw new NotSupportedException("A bucket result cannot be filtered; filter the nodes before Buckets().");
    public ICollectionData Page(int pageIndex, int pageSize) => throw new NotSupportedException("A bucket result cannot be paged; page the nodes before Buckets().");
    public ICollectionData Take(int take) => throw new NotSupportedException("A bucket result cannot be paged; page the nodes before Buckets().");
    public ICollectionData Skip(int skip) => throw new NotSupportedException("A bucket result cannot be paged; page the nodes before Buckets().");
    public PropertyType GetPropertyType(string name) => throw new NotSupportedException();
}
