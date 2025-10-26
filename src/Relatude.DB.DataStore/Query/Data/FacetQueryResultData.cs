using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Serialization;
using System.Runtime.CompilerServices;
using System.Text;
using Relatude.DB.Datamodels.Properties;

namespace Relatude.DB.Query.Data;

public class FacetQueryResultData : ICollectionData {
    Datamodel _dm;
    public FacetQueryResultData(Dictionary<Guid, Facets> facets, int sourceCount, IStoreNodeDataCollection innerResult, Datamodel dm) {
        Facets = facets;
        Result = innerResult;
        SourceCount = sourceCount;
        _dm = dm;
    }
    public double DurationMs { get; set; }
    public Dictionary<Guid, Facets> Facets { get; }
    public IStoreNodeDataCollection Result { get; }
    public int TotalCount => Result.TotalCount;
    public int Count => Result.Count;
    public int SourceCount { get; }
    public IEnumerable<object?> Values => Result.Values;
    public int PageIndexUsed => Result.PageIndexUsed;
    public int? PageSizeUsed => Result.PageSizeUsed;

    public ICollectionData Filter(bool[] keep) {
        throw new NotImplementedException();
    }
    public ICollectionData Page(int pageIndex, int pageSize) {
        return new FacetQueryResultData(Facets, TotalCount, (IStoreNodeDataCollection)Result.Page(pageIndex, pageSize), _dm);
    }
    public void BuildTypeScriptTypeInfo(StringBuilder sb) {
    }
    public PropertyType GetPropertyType(string name) {
        throw new NotImplementedException();
    }
    public ICollectionData ReOrder(IEnumerable<int> newPos) {
        throw new NotImplementedException();
    }
    public ICollectionData Take(int take) {
        return new FacetQueryResultData(Facets, TotalCount, (IStoreNodeDataCollection)Result.Take(take), _dm);
    }
    public ICollectionData Skip(int skip) {
        return new FacetQueryResultData(Facets, TotalCount, (IStoreNodeDataCollection)Result.Skip(skip), _dm);
    }
}
