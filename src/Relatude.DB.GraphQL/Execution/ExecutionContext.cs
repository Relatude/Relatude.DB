using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.GraphQL.Language;
using Relatude.DB.GraphQL.Schema;

namespace Relatude.DB.GraphQL.Execution;

/// <summary>Per-request execution state.</summary>
internal sealed class ExecutionContext {
    public required RelatudeGraphQL Host { get; init; }
    public required GqlSchema Schema { get; init; }
    public required GraphQLEndpointDefinition Endpoint { get; init; }
    public required IDataStore Store { get; init; }
    public required Document Document { get; init; }
    public required Dictionary<string, FragmentDefinition> Fragments { get; init; }
    public Dictionary<string, object?> Variables { get; set; } = [];
    public QueryContext? QueryContext { get; init; }
    public List<GraphQLError> Errors { get; } = [];

    public void AddError(string message, AstNode? node, IEnumerable<object>? path) {
        var error = new GraphQLError { Message = message };
        var loc = LocationOf(node);
        if (loc != null) error.Locations = [loc];
        if (path != null) error.Path = [.. path];
        Errors.Add(error);
    }

    public ErrorLocation? LocationOf(AstNode? node) {
        if (node == null) return null;
        var (line, column) = Document.LineColumn(node.Position);
        return new ErrorLocation { Line = line, Column = column };
    }

    public GraphQLRequestException RequestError(string message, AstNode? node = null) {
        var error = new GraphQLError { Message = message };
        var loc = LocationOf(node);
        if (loc != null) error.Locations = [loc];
        return new GraphQLRequestException(error);
    }
}
