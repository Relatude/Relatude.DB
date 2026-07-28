using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.GraphQL.Execution;
using Relatude.DB.GraphQL.Introspection;
using Relatude.DB.GraphQL.Schema;

namespace Relatude.DB.GraphQL;

/// <summary>
/// A read-only GraphQL endpoint over a Relatude.DB data store.
/// The schema is generated from the store's datamodel at construction time;
/// instances are immutable and safe for concurrent use.
/// </summary>
public sealed class RelatudeGraphQL {
    readonly IDataStore _store;
    readonly Lazy<string> _sdl;

    public GqlSchema Schema { get; }
    public GraphQLOptions Options { get; }
    internal IntrospectionData Introspection { get; }

    public RelatudeGraphQL(IDataStore store, GraphQLOptions? options = null) {
        _store = store;
        Options = options ?? new GraphQLOptions();
        Schema = SchemaBuilder.Build(store.Datamodel, Options);
        Introspection = IntrospectionData.Build(Schema);
        _sdl = new Lazy<string>(() => SdlWriter.Write(Schema), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The generated schema as GraphQL SDL.</summary>
    public string ToSDL() => _sdl.Value;

    /// <summary>Executes a GraphQL query. Pass a <see cref="QueryContext"/> to scope the request (user, culture, publishing state).</summary>
    public GraphQLResult Execute(GraphQLRequest request, QueryContext? queryContext = null)
        => QueryExecutor.Execute(this, _store, request, queryContext);

    public Task<GraphQLResult> ExecuteAsync(GraphQLRequest request, QueryContext? queryContext = null)
        => Task.FromResult(Execute(request, queryContext)); // store queries are synchronous under the hood

    /// <summary>Convenience overload for tests and simple hosts.</summary>
    public GraphQLResult Execute(string query, QueryContext? queryContext = null)
        => Execute(new GraphQLRequest { Query = query }, queryContext);
}
