using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.GraphQL.Endpoints;
using Relatude.DB.GraphQL.Execution;
using Relatude.DB.GraphQL.Introspection;
using Relatude.DB.GraphQL.Schema;
using Relatude.DB.Transactions;

namespace Relatude.DB.GraphQL;

/// <summary>
/// A GraphQL endpoint over a Relatude.DB data store. The schema is generated from the store's datamodel and the
/// endpoint definition at construction time; instances are immutable and safe for concurrent use.
/// </summary>
public sealed class RelatudeGraphQL {
    readonly IDataStore _store;
    readonly Lazy<string> _sdl;
    readonly Lazy<GraphQLFacetScope> _facetScope;

    public GqlSchema Schema { get; }
    /// <summary>The store the endpoint reads and writes.</summary>
    public IDataStore Store => _store;
    /// <summary>The types and properties the endpoint's facet search may look at (see <see cref="GraphQLEndpointDefinition.EnableFacetSearch"/>).</summary>
    public GraphQLFacetScope FacetScope => _facetScope.Value;
    /// <summary>The facet search is switched on and there is something to answer it.</summary>
    public bool FacetSearchAvailable => Definition.EnableFacetSearch && Options.FacetSearch != null;
    public GraphQLEndpointDefinition Definition { get; }
    public GraphQLOptions Options { get; }
    internal IntrospectionData Introspection { get; }
    /// <summary>What the schema builder left out or renamed, for the endpoint editor.</summary>
    public IReadOnlyList<string> Warnings => Schema.Warnings;

    /// <summary>An endpoint reflecting the whole datamodel in GraphQL-style names, configured in code.</summary>
    public RelatudeGraphQL(IDataStore store, GraphQLOptions? options = null)
        : this(store, GraphQLEndpointDefinition.FromOptions(options ?? new GraphQLOptions()), options) { }

    public RelatudeGraphQL(IDataStore store, GraphQLEndpointDefinition definition, GraphQLOptions? options = null) {
        _store = store;
        Definition = definition;
        Options = options ?? new GraphQLOptions();
        Schema = SchemaBuilder.Build(store.Datamodel, definition, Options);
        Introspection = IntrospectionData.Build(Schema);
        _sdl = new Lazy<string>(() => SdlWriter.Write(Schema), LazyThreadSafetyMode.ExecutionAndPublication);
        _facetScope = new Lazy<GraphQLFacetScope>(() => GraphQLFacetScope.Build(Schema), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The generated schema as GraphQL SDL.</summary>
    public string ToSDL() => _sdl.Value;

    /// <summary>Builds the schema a definition would give over a datamodel, without a store: for previews in the endpoint editor.</summary>
    public static GqlSchema BuildSchema(Datamodel datamodel, GraphQLEndpointDefinition definition, GraphQLOptions? options = null)
        => SchemaBuilder.Build(datamodel, definition, options ?? new GraphQLOptions());

    /// <summary>Executes a GraphQL request. Pass a <see cref="QueryContext"/> to scope it (user, culture, publishing state).</summary>
    public GraphQLResult Execute(GraphQLRequest request, QueryContext? queryContext = null)
        => QueryExecutor.Execute(this, _store, request, queryContext);

    public Task<GraphQLResult> ExecuteAsync(GraphQLRequest request, QueryContext? queryContext = null)
        => Task.FromResult(Execute(request, queryContext)); // store queries are synchronous under the hood

    /// <summary>Convenience overload for tests and simple hosts.</summary>
    public GraphQLResult Execute(string query, QueryContext? queryContext = null)
        => Execute(new GraphQLRequest { Query = query }, queryContext);

    static readonly TimeSpan _explorerDataLifetime = TimeSpan.FromMinutes(1);
    readonly object _explorerLock = new();
    (GraphQLExplorerData Data, DateTime Made)? _explorerData;

    /// <summary>
    /// What the explorer page needs: the schema described, one stored node per type and the guide's examples, the
    /// nodes read in the given query context. Without a context the answer is shared for a minute: the samples cost
    /// one query per type, and a page can be reloaded often.
    /// </summary>
    public GraphQLExplorerData GetExplorerData(QueryContext? queryContext = null) {
        if (queryContext != null) return ExplorerData.Build(Schema, ExplorerData.StoreSampler(_store, queryContext));
        lock (_explorerLock) {
            if (_explorerData is { } cached && DateTime.UtcNow - cached.Made < _explorerDataLifetime) return cached.Data;
            var data = ExplorerData.Build(Schema, ExplorerData.StoreSampler(_store, null));
            _explorerData = (data, DateTime.UtcNow);
            return data;
        }
    }

    internal TransactionResult RunTransaction(TransactionData transaction, QueryContext? queryContext)
        => Options.TransactionExecutor != null
            ? Options.TransactionExecutor(_store, transaction, queryContext)
            : _store.Execute(transaction, null, queryContext);
}
