using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relatude.DB.GraphQL;

/// <summary>
/// A GraphQL execution result: { "data": ..., "errors": [...] }.
/// Failed fields are null in <see cref="Data"/> with a matching entry in <see cref="Errors"/> (partial data);
/// request-level failures (syntax, validation, unknown operation, variables) have no data at all.
/// </summary>
public sealed class GraphQLResult {
    public Dictionary<string, object?>? Data { get; init; }
    public List<GraphQLError>? Errors { get; init; }

    static readonly JsonSerializerOptions _jsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    /// <summary>Serializes to the standard GraphQL response JSON.</summary>
    public string ToJson(bool indented = false) {
        var options = indented ? new JsonSerializerOptions(_jsonOptions) { WriteIndented = true } : _jsonOptions;
        return JsonSerializer.Serialize(this, options);
    }
}
