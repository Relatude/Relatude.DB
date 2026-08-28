namespace Relatude.DB.NodeServer.Models;
public class QueryModel {
    public string Query { get; set; } = string.Empty;
    public ParameterModel[] Parameters { get; set; } = [];
    /// <summary>Optional reading context. Left out, the query reads with the context of the store.</summary>
    public QueryContextModel? Context { get; set; }
}
