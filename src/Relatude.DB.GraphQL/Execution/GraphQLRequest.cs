using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relatude.DB.GraphQL;

/// <summary>A GraphQL request as posted by clients: { "query": "...", "operationName": "...", "variables": {...} }.</summary>
public sealed class GraphQLRequest {
    public string? Query { get; set; }
    public string? OperationName { get; set; }
    public JsonElement? Variables { get; set; }
    /// <summary>
    /// Scheme, host and path base the request came in on ("https://www.site.com"), which <c>url(absolute: true)</c> puts in
    /// front of a relative file url. Set by the host, never read from the request body.
    /// </summary>
    [JsonIgnore]
    public string? Origin { get; set; }
}
