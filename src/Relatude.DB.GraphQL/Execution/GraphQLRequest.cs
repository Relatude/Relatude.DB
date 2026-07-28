using System.Text.Json;

namespace Relatude.DB.GraphQL;

/// <summary>A GraphQL request as posted by clients: { "query": "...", "operationName": "...", "variables": {...} }.</summary>
public sealed class GraphQLRequest {
    public string? Query { get; set; }
    public string? OperationName { get; set; }
    public JsonElement? Variables { get; set; }
}
