using Microsoft.AspNetCore.Http;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;

namespace Relatude.DB.GraphQL;

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
    /// <summary>Expose the built-in system node types (users, groups, collections, cultures). Off by default.</summary>
    public bool IncludeSystemTypes { get; set; } = false;
    /// <summary>Return false to keep a node type out of the schema. Applied after the built-in exclusions.</summary>
    public Func<NodeTypeModel, bool>? TypeFilter { get; set; }
    /// <summary>Per-request query context (user, culture, publishing state). Null → the store default.</summary>
    public Func<HttpContext, QueryContext?>? QueryContextFactory { get; set; }
    /// <summary>Per-request store resolution for the endpoint. Defaults to resolving IDataStore from request services.</summary>
    public Func<HttpContext, IDataStore?>? StoreResolver { get; set; }
}
