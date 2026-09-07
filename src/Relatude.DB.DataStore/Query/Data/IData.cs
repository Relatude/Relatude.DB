using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Query.Expressions;
using Relatude.DB.Datamodels.Properties;
using System.Text;
using Relatude.DB.DataStores;

namespace Relatude.DB.Query.Data;
public interface ICollectionBase {
    double DurationMs { get; set; }
    int Count { get; }
    int TotalCount { get; }
}
public interface ICollectionData : ICollectionBase {
    IEnumerable<object?> Values { get; }
    ICollectionData ReOrder(IEnumerable<int> newPos);
    ICollectionData Filter(bool[] keep);
    ICollectionData Page(int pageIndex, int pageSize);
    ICollectionData Take(int take);
    ICollectionData Skip(int skip);
    int PageIndexUsed { get; }
    int? PageSizeUsed { get; }
    PropertyType GetPropertyType(string name);
}
public interface IStoreNodeDataCollection : ICollectionData, IIncludeBranches {
    IStoreNodeDataCollection FilterAsMuchAsPossibleUsingIndexes(Variables vars, IExpression orgFilter, out IExpression? remainingFilter);
    IStoreNodeDataCollection Relates(Guid propertyId, Guid nodeId);
    IStoreNodeDataCollection RelatesNot(Guid propertyId, Guid nodeId);
    IStoreNodeDataCollection RelatesAny(Guid propertyId, IEnumerable<Guid> nodeId);
    IStoreNodeDataCollection WhereIn(Guid propertyId, IEnumerable<object?> values);
    IStoreNodeDataCollection WhereInIds(IEnumerable<Guid> values);
    IEnumerable<INodeDataExternal> NodeValues { get; }
    IEnumerable<int> NodeIds { get; }
    IEnumerable<Guid> NodeGuids { get; }
    ObjectData ToObjectCollection();
    bool TryOrderByIndexes(string propertyName, bool descending);
    IStoreNodeDataCollection FilterByTypes(Guid[] types, bool includeDescendants);
}
public interface ISearchQueryResultData : IIncludeBranches, ICollectionBase {
    bool Capped { get; }
    double InnerSearchTimeMs { get; }
    List<SearchResultHitData> Hits { get; }
    int PageIndexUsed { get; }
    int? PageSizeUsed { get; }
    string Search { get; }
}
public interface IGraphCollection : IStoreNodeDataCollection {
    IStoreNodeDataCollection Traverse(Guid relationPropertyId, int minLevel, int maxLevel, GraphDirection direction, int? maxVisited);
    IGraphPathResultData ShortestPath(Guid relationPropertyId, Guid fromNodeId, Guid toNodeId, int maxLevel, GraphDirection direction, int? maxVisited);
}
public interface IGraphPathResultData : IIncludeBranches, ICollectionBase {
    bool Found { get; }
    int Length { get; } // number of edges in the path, 0 when not found or from == to
    List<Guid> NodeIds { get; } // node ids along the path, from -> to, inclusive
    List<INodeDataExternal> Nodes { get; } // node data along the path, from -> to, inclusive
}
public interface IFacetSource : IStoreNodeDataCollection {
    Dictionary<Guid, Facets> EvaluateFacetsAndFilter(Dictionary<Guid, Facets> given, Dictionary<Guid, Facets> set, out IFacetSource filteredSource, int pageIndex, int? pageSize, QueryContext ctx);
    /// <summary>Applies the facet selection as a filter and nothing else: no buckets are built or counted.</summary>
    IFacetSource FilterBySelection(Dictionary<Guid, Facets> given, Dictionary<Guid, Facets> set, QueryContext ctx);
    Datamodel Datamodel { get; }
}
public interface IPivotSource : IStoreNodeDataCollection {
    PivotQueryResultData EvaluatePivot(PivotSpec spec, QueryContext ctx);
}
/// <summary>
/// One bucket of a collection: a value of a property - or a range of them - and the ids of the
/// collection's nodes that hold it. A null <see cref="FacetValue.Value"/> is the nodes without a value.
/// </summary>
public sealed class ValueBucket {
    public ValueBucket(FacetValue value, IEnumerable<int> ids, int count) {
        Value = value;
        Ids = ids;
        Count = count;
    }
    public FacetValue Value { get; }
    /// <summary>The ids of this collection's nodes in the bucket, in the bucket's own order.</summary>
    public IEnumerable<int> Ids { get; }
    public int Count { get; }
}
/// <summary>
/// A collection whose nodes can be sorted into buckets by a property without reading any of them:
/// the facet machinery, handed back as sets of ids rather than counts. What a view that has to place
/// every node of a large result by a value of it needs - a colour per value, a bar per value.
/// </summary>
public interface IBucketSource : IStoreNodeDataCollection {
    /// <summary>
    /// Buckets the nodes of this collection by a property the way a facet does: one bucket per value,
    /// or ranges when <paramref name="isRange"/> says so (null lets the property decide, as the
    /// automatic facets do - a scalar with many distinct values gets ranges). Nodes without a value
    /// form a bucket of their own when <paramref name="includeMissing"/>. Empty buckets are left out.
    /// When there are more buckets than <paramref name="maxBuckets"/> (0 = no limit) the largest are
    /// kept, so a node may end up in no bucket at all. Buckets come in the property's natural order:
    /// values sorted, ranges as generated, the missing bucket last.
    /// </summary>
    IReadOnlyList<ValueBucket> Bucket(Guid propertyId, bool? isRange, bool includeMissing, int maxBuckets, QueryContext ctx);
}
public interface ISearchCollection : IStoreNodeDataCollection {
    ISearchQueryResultData Search(string search, Guid searchPropertyId, double? ratioSemantic, float? minimumVectorSimilarity, bool? orSearch, int pageIndex, int pageSize, int? maxHitsEvaluated, int? maxWordsEvaluated);
    IStoreNodeDataCollection FilterBySearch(string text, Guid propertyId, double? ratioSemantic, float? minimumVectorSimilarity, bool? orSearch, int? maxWordVariations);
}
public interface IStoreNodeData {
    IDataStore Store { get; }
    INodeDataExternal NodeData { get; }
    object? GetValue(string propertyName);
    ObjectData ToObjectData();
}


