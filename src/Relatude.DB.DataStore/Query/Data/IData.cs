using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Query.Expressions;
using Relatude.DB.Datamodels.Properties;
using System.Text;
using Relatude.DB.DataStores;

namespace Relatude.DB.Query.Data;
public interface ICollectionData {
    IEnumerable<object> Values { get; }
    int Count { get; }
    ICollectionData ReOrder(IEnumerable<int> newPos);
    ICollectionData Filter(bool[] keep);
    ICollectionData Page(int pageIndex, int pageSize);
    ICollectionData Take(int take);
    ICollectionData Skip(int skip);
    int TotalCount { get; }
    int PageIndexUsed { get; }
    int? PageSizeUsed { get; }
    double DurationMs { get; set; }
    PropertyType GetPropertyType(string name);
}
public interface IStoreNodeDataCollection : ICollectionData, IIncludeBranches {
    IStoreNodeDataCollection FilterAsMuchAsPossibleUsingIndexes(Variables vars, IExpression orgFilter, out IExpression? remainingFilter);
    IStoreNodeDataCollection Relates(Guid propertyId, Guid nodeId);
    IStoreNodeDataCollection RelatesNot(Guid propertyId, Guid nodeId);
    IStoreNodeDataCollection RelatesAny(Guid propertyId, IEnumerable<Guid> nodeId);
    IStoreNodeDataCollection WhereIn(Guid propertyId, IEnumerable<object> values);
    IStoreNodeDataCollection WhereInIds(IEnumerable<Guid> values);
    IEnumerable<INodeData> NodeValues { get; }
    IEnumerable<int> NodeIds { get; }
    IEnumerable<Guid> NodeGuids { get; }
    ObjectData ToObjectCollection();
    bool TryOrderByIndexes(string propertyName, bool descending);
    IStoreNodeDataCollection FilterByTypes(Guid[] types);
}
public interface ISearchQueryResultData : IIncludeBranches {
    bool Capped { get; }
    int Count { get; }
    double DurationMs { get; set; }
    List<SearchResultHitData> Hits { get; }
    int PageIndexUsed { get; }
    int? PageSizeUsed { get; }
    string Search { get; }
    int TotalCount { get; }    
}
public interface IFacetSource : IStoreNodeDataCollection {
    Dictionary<Guid, Facets> EvaluateFacetsAndFilter(Dictionary<Guid, Facets> given, Dictionary<Guid, Facets> set, out IFacetSource filteredSource, int pageIndex, int? pageSize);
    Datamodel Datamodel { get; }
}
public interface ISearchCollection : IStoreNodeDataCollection {
    ISearchQueryResultData Search(string search, Guid searchPropertyId, double? ratioSemantic, int pageIndex, int pageSize, int maxHitsEvaluated, int maxWordsEvaluated);
    IStoreNodeDataCollection FilterBySearch(string text, Guid propertyId, double? ratioSemantic);
}
public interface IStoreNodeData {
    IDataStore Store { get; }
    INodeData NodeData { get; }
    object? GetValue(string propertyName);
    ObjectData ToObjectData();
}


