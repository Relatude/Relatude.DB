using GraphQLParser.AST;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.GraphQL.Schema;

namespace Relatude.DB.GraphQL.Execution;

/// <summary>Per-request execution state.</summary>
internal sealed class ExecutionContext {
    public required GqlSchema Schema { get; init; }
    public required GraphQLOptions Options { get; init; }
    public required IDataStore Store { get; init; }
    public required GraphQLDocument Document { get; init; }
    public required Dictionary<string, GraphQLFragmentDefinition> Fragments { get; init; }
    public Dictionary<string, object?> Variables { get; set; } = [];
    public QueryContext? QueryContext { get; init; }
    public List<GraphQLError> Errors { get; } = [];

    public void AddError(string message, ASTNode? node, IEnumerable<object>? path) {
        var error = new GraphQLError { Message = message };
        var loc = LocationOf(node);
        if (loc != null) error.Locations = [loc];
        if (path != null) error.Path = [.. path];
        Errors.Add(error);
    }

    public ErrorLocation? LocationOf(ASTNode? node) {
        if (node == null) return null;
        try {
            var loc = GraphQLParser.Location.FromLinearPosition(Document.Source, node.Location.Start);
            return new ErrorLocation { Line = loc.Line, Column = loc.Column };
        } catch { return null; }
    }

    public GraphQLRequestException RequestError(string message, ASTNode? node = null) {
        var error = new GraphQLError { Message = message };
        var loc = LocationOf(node);
        if (loc != null) error.Locations = [loc];
        return new GraphQLRequestException(error);
    }
}
