using Relatude.DB.Common;
using Relatude.DB.Datamodels.Properties;

namespace Relatude.DB.Query.Data;

/// <summary>
/// The store-level answer to a pivot query: the <see cref="PivotResult"/> wrapped as collection data so
/// it travels the same path as every other query result (duration stamping, logging). Count is the
/// number of row groups on the page, TotalCount the number of row groups before paging.
/// </summary>
public class PivotQueryResultData : ICollectionData {
    public PivotQueryResultData(PivotResult result) {
        Result = result;
    }
    public PivotResult Result { get; }
    public double DurationMs { get => Result.DurationMs; set => Result.DurationMs = value; }
    public int Count => Result.Rows.Groups.Length;
    public int TotalCount => Result.Rows.TotalGroupCount;
    public IEnumerable<object?> Values => Result.EnumerateRows();
    public int PageIndexUsed => Result.Rows.PageIndex;
    public int? PageSizeUsed => Result.Rows.PageSize > 0 ? Result.Rows.PageSize : null;
    public ICollectionData ReOrder(IEnumerable<int> newPos) => throw new NotSupportedException("A pivot result cannot be reordered; sort the axes with SetRowOptions / SetColumnOptions.");
    public ICollectionData Filter(bool[] keep) => throw new NotSupportedException("A pivot result cannot be filtered; filter the nodes before Pivot().");
    public ICollectionData Page(int pageIndex, int pageSize) => throw new NotSupportedException("A pivot result cannot be paged; use SetRowPaging before executing.");
    public ICollectionData Take(int take) => throw new NotSupportedException("A pivot result cannot be paged; use SetRowPaging before executing.");
    public ICollectionData Skip(int skip) => throw new NotSupportedException("A pivot result cannot be paged; use SetRowPaging before executing.");
    public PropertyType GetPropertyType(string name) => throw new NotSupportedException();
}
