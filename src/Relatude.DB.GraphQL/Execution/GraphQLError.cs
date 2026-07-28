namespace Relatude.DB.GraphQL;

public sealed class ErrorLocation {
    public int Line { get; init; }
    public int Column { get; init; }
}

/// <summary>A GraphQL error per the spec response format.</summary>
public sealed class GraphQLError {
    public required string Message { get; init; }
    public List<ErrorLocation>? Locations { get; set; }
    /// <summary>Response path of the failed field (strings and list indexes).</summary>
    public List<object>? Path { get; set; }
}

/// <summary>Thrown for request-level failures (syntax, validation, variables): no data is returned.</summary>
internal sealed class GraphQLRequestException(GraphQLError error) : Exception(error.Message) {
    public GraphQLError Error { get; } = error;
}

/// <summary>Thrown while resolving a single field: the field becomes null and an error is recorded.</summary>
internal sealed class GraphQLFieldException(string message) : Exception(message) {
}
