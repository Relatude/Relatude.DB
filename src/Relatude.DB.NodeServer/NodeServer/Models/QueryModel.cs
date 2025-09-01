namespace Relatude.DB.NodeServer.Models;
public class QueryModel {
    public string Query { get; set; } = string.Empty;
    public ParameterModel[] Parameters { get; set; } = [];
}
