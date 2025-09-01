using Relatude.DB.Common;
using System.Text;
using Relatude.DB.Query.Data;
using System.Collections.Generic;

namespace Relatude.DB.Query;
public class ResultSetFacets<T> : ResultSet<T> {
    internal protected FacetQueryResultData result;
    internal ResultSetFacets(IEnumerable<T> values, FacetQueryResultData result)
        : base(values, result.Count, result.TotalCount, result.PageIndexUsed, result.PageSizeUsed ?? 0, result.DurationMs) {
        this.result = result;
        Facets = result.Facets.Values;
        SourceCount = result.SourceCount;
    }
    public IEnumerable<Facets> Facets { get; }
    public int SourceCount { get; }
    public override string ToString() {
        var sb = new StringBuilder();
        sb.AppendLine("DURATION: " + DurationMs.ToString("0.00ms"));
        sb.AppendLine("HITS: " + TotalCount.To1000N());
        sb.AppendLine("FACETS: ");
        foreach (var facet in Facets) {
            sb.AppendLine(facet.DisplayName + ": " + facet.Values.Sum(v => v.Count));
            foreach (var value in facet.Values) {
                sb.AppendLine("  " + value.ToString() + " (" + value.Count + ") " + (value.Selected ? " * " : ""));
            }
            sb.AppendLine();
        }
        sb.AppendLine();
        return sb.ToString();
    }
    public ResultSetFacetsNotEnumerable<T> ToNotEnumerable() {
        return new ResultSetFacetsNotEnumerable<T>(Values, result);
    }

}

public sealed class ResultSetFacetsNotEnumerable<T> : ResultSetNotEnumerable<T> {
    internal ResultSetFacetsNotEnumerable(IEnumerable<T> values, FacetQueryResultData result)
        : base(values, result.Count, result.TotalCount, result.PageIndexUsed, result.PageSizeUsed ?? 0, result.DurationMs, false) {
        Facets = result.Facets.Values;
        SourceCount = result.SourceCount;
    }
    public IEnumerable<Facets> Facets { get; }
    public int SourceCount { get; }
}
