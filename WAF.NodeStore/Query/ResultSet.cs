using System.Collections;

namespace WAF.Query;
public class ResultSet<T> : IEnumerable<T> {
    public ResultSet(IEnumerable<T> values, int count, int totalCount, int pageIndex, int? pageSize, double durationMs = 0) {
        Values = values;
        Count = count;
        TotalCount = totalCount;
        PageIndex = pageIndex;
        PageSize = pageSize.HasValue ? pageSize.Value : 0;
        IsAll = totalCount == count;
        PageCount = PageCount > 0 ? (int)Math.Ceiling((double)totalCount / pageSize!.Value) : 1;
        DurationMs = durationMs;
        IsLastPage = pageIndex + 1 > PageCount;
    }
    public IEnumerable<T> Values { get; }
    public bool IsAll { get; }
    public bool IsLastPage { get; }
    public int TotalCount { get; }
    public int PageIndex { get; }
    public int PageSize { get; }
    public int PageCount { get; }
    public int Count { get; }
    public double DurationMs { get; set; }
    public IEnumerator<T> GetEnumerator() => Values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => Values.GetEnumerator();
}
public class ResultSetNotEnumerable<T> {
    public ResultSetNotEnumerable(IEnumerable<T> values, int count, int totalCount, int pageIndex, int? pageSize, double durationMs, bool capped) {
        Values = values;
        Count = count;
        TotalCount = totalCount;
        Capped = capped;
        PageIndex = pageIndex;
        PageSize = pageSize.HasValue ? pageSize.Value : 0;
        IsAll = totalCount == count;
        PageCount = PageCount > 0 ? (int)Math.Ceiling((double)totalCount / pageSize!.Value) : 1;
        DurationMs = durationMs;
        IsLastPage = pageIndex + 1 > PageCount;
    }
    public IEnumerable<T> Values { get; }
    public bool IsAll { get; }
    public bool IsLastPage { get; }
    public int TotalCount { get; }
    public bool Capped{ get; }
    public int PageIndex { get; }
    public int PageSize { get; }
    public int PageCount { get; }
    public int Count { get; }
    public double DurationMs { get; set; }
}
