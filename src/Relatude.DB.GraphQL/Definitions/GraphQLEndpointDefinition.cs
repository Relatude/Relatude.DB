using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relatude.DB.GraphQL;

public enum GraphQLEndpointMode {
    /// <summary>Only the node types and properties listed in <see cref="GraphQLEndpointDefinition.Types"/> are exposed.</summary>
    Selected,
    /// <summary>Every node type and property of the datamodel is exposed, following the datamodel as it changes.</summary>
    WholeDatamodel,
}

/// <summary>
/// A GraphQL endpoint as stored in its JSON file: the url it answers on, what it exposes and under which names.
/// Node types and properties are referenced by their datamodel ids, so renames in the datamodel do not break the endpoint.
/// </summary>
public sealed class GraphQLEndpointDefinition {
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Request path, e.g. "/graphql". Matched case-insensitively.</summary>
    public string Url { get; set; } = "/graphql";
    public bool Enabled { get; set; } = true;
    public GraphQLEndpointMode Mode { get; set; } = GraphQLEndpointMode.Selected;
    /// <summary>Use datamodel names verbatim (type "Article", field "Title", root "Articles") instead of GraphQL-style camelCase fields.</summary>
    public bool ExactNames { get; set; }
    /// <summary>Expose create/update/delete mutations. Types marked <see cref="GraphQLTypeDefinition.ReadOnly"/> are left out.</summary>
    public bool AllowMutations { get; set; }
    public bool EnableIntrospection { get; set; } = true;
    public bool EnableGetRequests { get; set; } = true;
    /// <summary>
    /// Serve the explorer, a page to build, run and read about queries, on the endpoint's url: a browser asking for
    /// the url gets the page instead of an error. It reads the schema through the endpoint, so it needs introspection,
    /// and asks for the API key when the endpoint has one.
    /// </summary>
    public bool EnableExplorer { get; set; }
    /// <summary>
    /// Serve a facet search over the endpoint's node types beside the schema (the "?facets=" requests), and a page
    /// showing it as a visual pivot on the endpoint's url - the page a browser gets first when this is on. Only the
    /// exposed types and properties take part, under their datamodel names: renames apply to the schema alone, and
    /// everything else works as the admin UI's query section does. It needs a host that provides the search
    /// (<see cref="GraphQLOptions.FacetSearch"/>), which the Relatude.DB server does for the endpoints it serves.
    /// </summary>
    public bool EnableFacetSearch { get; set; }
    /// <summary>How many nodes the facet search's picture may hold; a larger result shows its first ones.</summary>
    public int MaxFacetCards { get; set; } = DefaultMaxFacetCards;
    public const int DefaultMaxFacetCards = 200_000;
    /// <summary>
    /// The node type the explorer's first query and guide, and the facet search's page, open on. Null, or a type the
    /// endpoint does not expose, leaves the choice to them: the facet search opens on the first exposed type, the
    /// explorer on the type with the most to show.
    /// </summary>
    public Guid? DefaultNodeTypeId { get; set; }
    /// <summary>Whole-datamodel mode only: also expose the built-in system node types (users, groups, cultures...).</summary>
    public bool IncludeSystemTypes { get; set; }
    /// <summary>When set, requests must carry it in an "X-Api-Key" header or as a bearer token.</summary>
    public string? ApiKey { get; set; }
    public int MaxQueryDepth { get; set; } = 16;
    public int MaxIncludeDepth { get; set; } = 8;
    public int DefaultPageSize { get; set; } = 25;
    public int MaxPageSize { get; set; } = 200;
    public List<GraphQLTypeDefinition> Types { get; set; } = [];
    public List<GraphQLViewDefinition> Views { get; set; } = [];

    static readonly JsonSerializerOptions _json = new() {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
    public static JsonSerializerOptions JsonOptions => _json;

    public string ToJson() => JsonSerializer.Serialize(this, _json);

    public static GraphQLEndpointDefinition FromJson(string json) {
        var def = JsonSerializer.Deserialize<GraphQLEndpointDefinition>(json, _json) ?? throw new JsonException("The endpoint definition is empty.");
        def.Types ??= [];
        def.Views ??= [];
        return def;
    }

    /// <summary>The definition the code-first <see cref="GraphQLOptions"/> API stands for: the whole datamodel in GraphQL-style names.</summary>
    public static GraphQLEndpointDefinition FromOptions(GraphQLOptions options) => new() {
        Name = "GraphQL",
        Mode = GraphQLEndpointMode.WholeDatamodel,
        ExactNames = false,
        AllowMutations = options.AllowMutations,
        EnableIntrospection = options.EnableIntrospection,
        EnableGetRequests = options.EnableGetRequests,
        EnableExplorer = options.EnableExplorer,
        EnableFacetSearch = options.EnableFacetSearch,
        MaxFacetCards = options.MaxFacetCards,
        IncludeSystemTypes = options.IncludeSystemTypes,
        MaxQueryDepth = options.MaxQueryDepth,
        MaxIncludeDepth = options.MaxIncludeDepth,
        DefaultPageSize = options.DefaultPageSize,
        MaxPageSize = options.MaxPageSize,
    };

    /// <summary>Normalizes the url to "/segment/segment" form; null when it is not a usable path.</summary>
    public static string? NormalizeUrl(string? url) {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var path = url.Trim();
        var q = path.IndexOfAny(['?', '#']);
        if (q >= 0) path = path[..q];
        path = "/" + path.Trim('/');
        if (path.Length < 2) return null;
        if (path.Contains("//", StringComparison.Ordinal) || path.Any(char.IsWhiteSpace)) return null;
        return path;
    }
}

public sealed class GraphQLTypeDefinition {
    public Guid NodeTypeId { get; set; }
    /// <summary>GraphQL type name; null = the datamodel name.</summary>
    public string? Name { get; set; }
    /// <summary>Root field returning one node by id; null = derived from the type name.</summary>
    public string? SingleName { get; set; }
    /// <summary>Root field querying a page of nodes; null = derived from the type name.</summary>
    public string? ListName { get; set; }
    /// <summary>No mutations for this type even when the endpoint allows them.</summary>
    public bool ReadOnly { get; set; }
    /// <summary>The properties to expose; null (or omitted in the file) exposes every property of the type.</summary>
    public List<GraphQLPropertyDefinition>? Properties { get; set; }
}

public sealed class GraphQLPropertyDefinition {
    public Guid PropertyId { get; set; }
    /// <summary>GraphQL field name; null = derived from the property name.</summary>
    public string? Name { get; set; }
}

/// <summary>A root field defined by a Relatude query, e.g. "Article.Where(a => a.Published == true)".</summary>
public sealed class GraphQLViewDefinition {
    public string Name { get; set; } = "";
    public string Query { get; set; } = "";
    public string? Description { get; set; }
}
