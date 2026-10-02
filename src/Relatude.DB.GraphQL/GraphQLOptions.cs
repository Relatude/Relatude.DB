using Microsoft.AspNetCore.Http;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Transactions;

namespace Relatude.DB.GraphQL;

/// <summary>
/// Code-first options for an endpoint that reflects the whole datamodel. Endpoints defined in the admin UI
/// carry the same limits in their <see cref="GraphQLEndpointDefinition"/>; the hooks below apply to both.
/// </summary>
public sealed class GraphQLOptions {
    /// <summary>Maximum nesting depth of an incoming GraphQL document (fragments included).</summary>
    public int MaxQueryDepth { get; set; } = 16;
    /// <summary>Maximum depth of relation traversal (translated to query Include paths).</summary>
    public int MaxIncludeDepth { get; set; } = 8;
    /// <summary>Page size used when a list field is queried without an explicit pageSize argument.</summary>
    public int DefaultPageSize { get; set; } = 25;
    /// <summary>Hard cap for the pageSize argument.</summary>
    public int MaxPageSize { get; set; } = 200;
    /// <summary>Serve __schema / __type. Disable on hardened public endpoints.</summary>
    public bool EnableIntrospection { get; set; } = true;
    /// <summary>Allow GET ?query=... requests (POST is always enabled).</summary>
    public bool EnableGetRequests { get; set; } = true;
    /// <summary>Expose create/update/delete mutations for every type.</summary>
    public bool AllowMutations { get; set; }
    /// <summary>Expose the built-in system node types (users, groups, collections, cultures). Off by default.</summary>
    public bool IncludeSystemTypes { get; set; } = false;
    /// <summary>Return false to keep a node type out of the schema. Applied after the built-in exclusions.</summary>
    public Func<NodeTypeModel, bool>? TypeFilter { get; set; }
    /// <summary>Per-request query context (user, culture, publishing state). Null → the store default.</summary>
    public Func<HttpContext, QueryContext?>? QueryContextFactory { get; set; }
    /// <summary>Per-request store resolution for the endpoint. Defaults to resolving IDataStore from request services.</summary>
    public Func<HttpContext, IDataStore?>? StoreResolver { get; set; }
    /// <summary>
    /// Runs a mutation's transaction. Defaults to <see cref="IDataStore.Execute"/>; the server sets it to go through
    /// the NodeStore so transaction plugins see the change.
    /// </summary>
    public Func<IDataStore, TransactionData, QueryContext?, TransactionResult>? TransactionExecutor { get; set; }
}
