using System.Diagnostics;
using System.Text.Json;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores;
using Relatude.DB.GraphQL;
using Relatude.DB.GraphQL.Endpoints;
using Relatude.DB.Nodes;
using Relatude.DB.NodeServer.Json;
using Relatude.DB.NodeServer.UI;
using Relatude.DB.Query.Data;

namespace Relatude.DB.NodeServer.GraphQL;

/// <summary>
/// The facet search a GraphQL endpoint serves beside its schema when <see cref="GraphQLEndpointDefinition.EnableFacetSearch"/>
/// is on: the admin UI's query section (UIQuery) answering the endpoint's own page, a facet rail and a visual pivot of the
/// exposed types. The requests are "?facets=action" on the endpoint's url:
///
///   GET  model        the types that can be searched, with their counts, and the card ceiling
///   POST search       {typeId, text, selections, expanded}: the total and the facet rail
///   POST pivot-model  {typeId}: the properties the picture can be coloured, stacked and sorted by
///   POST visual       {typeId, text, selections, properties, sortBy, sortDescending}: the cards, as the admin picture gets them
///   POST cards        {ids}: the names and picture properties of the cards on screen
///   POST card-images  {level, items}: their pictures, streamed (no tiles)
///
/// Three things differ from the admin UI, and all three are about who is asking. The search reads in the endpoint's
/// query context, so it sees what a GraphQL query of the endpoint sees and not what an administrator does. Only the
/// endpoint's types and the properties behind their fields take part (<see cref="GraphQLFacetScope"/>): a facet of
/// anything else is left out of the rail, and a selection, grouping or sort by it is ignored; a card is named and
/// pictured from what its type shows. And the text is matched by words only: a semantic search embeds the text with
/// the AI provider, which is a paid call per keystroke on a page anyone can open. Names are the datamodel's own -
/// the endpoint's renames are for its schema.
/// </summary>
public sealed class GraphQLFacetSearch : IGraphQLFacetSearch {
    readonly RelatudeDBServer _server;
    internal GraphQLFacetSearch(RelatudeDBServer server) { _server = server; }

    static readonly JsonSerializerOptions _read = new(RelatudeDBJsonOptions.Default) { PropertyNameCaseInsensitive = true };

    sealed class BadRequest(string message) : Exception(message) { }

    public async Task HandleAsync(HttpContext http, RelatudeGraphQL endpoint, string action, QueryContext? queryContext) {
        var store = nodeStoreOf(endpoint.Store);
        if (store == null) {
            await GraphQLHttpHandler.WriteErrorsAsync(http, StatusCodes.Status503ServiceUnavailable, "The database is not open.");
            return;
        }
        var scope = endpoint.FacetScope;
        try {
            switch (action) {
                case "model":
                    await write(http, model(store, endpoint, queryContext));
                    return;
                case "search":
                    await write(http, await search(store, scope, await read<SearchRequest>(http), queryContext));
                    return;
                case "pivot-model":
                    await write(http, pivotModel(store, scope, await read<TypeRequest>(http)));
                    return;
                case "visual":
                    await write(http, await visual(store, scope, await read<VisualRequest>(http), queryContext, endpoint.Definition.MaxFacetCards));
                    return;
                case "cards": {
                        var request = await read<CardsRequest>(http);
                        await write(http, UIQuery.CardsOf(store, request.Ids, queryContext, rulesOf(scope)));
                        return;
                    }
                case "card-images": {
                        var request = await read<CardImagesRequest>(http);
                        // no tiles: the page draws its cards as solids, whose faces have no part of a picture "in view"
                        // to cut a sharper one out of - and a tile is the costliest conversion there is
                        await UIQuery.WriteCardImagesOf(http, store, request.Level, request.Items, queryContext, rulesOf(scope), tiles: false);
                        return;
                    }
                default:
                    await GraphQLHttpHandler.WriteErrorsAsync(http, StatusCodes.Status404NotFound, $"There is no facet request \"{action}\".");
                    return;
            }
        } catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) {
            // the page moved on: what it no longer wants is simply not sent
        } catch (Exception ex) when (!http.Response.HasStarted) {
            var status = ex is BadRequest or JsonException ? StatusCodes.Status400BadRequest : StatusCodes.Status500InternalServerError;
            await GraphQLHttpHandler.WriteErrorsAsync(http, status, ex.Message);
        }
    }

    NodeStore? nodeStoreOf(IDataStore dataStore) {
        foreach (var c in _server.Containers.Values) {
            if (c.IsOpen() && ReferenceEquals(c.Store!.Datastore, dataStore)) return c.Store;
        }
        return null;
    }

    static async Task<T> read<T>(HttpContext http) where T : class {
        if (!HttpMethods.IsPost(http.Request.Method)) throw new BadRequest("POST the request as JSON.");
        return await JsonSerializer.DeserializeAsync<T>(http.Request.Body, _read, http.RequestAborted) ?? throw new BadRequest("Empty request body.");
    }

    static async Task write(HttpContext http, object value) {
        http.Response.ContentType = "application/json; charset=utf-8";
        http.Response.Headers.CacheControl = "no-store";
        await JsonSerializer.SerializeAsync(http.Response.Body, value, RelatudeDBJsonOptions.Default, http.RequestAborted);
    }

    /// <summary>The type asked for when it is one the endpoint exposes; its first type when none is named.</summary>
    static NodeTypeModel typeOf(GraphQLFacetScope scope, Guid? typeId) {
        if (scope.Types.Count == 0) throw new BadRequest("The endpoint exposes no node types to search.");
        if (typeId is not Guid id) return scope.Types[0];
        return scope.Types.FirstOrDefault(t => t.Id == id) ?? throw new BadRequest("The endpoint does not expose that type.");
    }

    static UIQuery.FacetSelection[] selectionsOf(IReadOnlySet<Guid> allowed, UIQuery.FacetSelection[]? selections)
        => [.. (selections ?? []).Where(s => allowed.Contains(s.PropertyId))];

    /// <summary>A card is a node seen through the endpoint; its picture and its name come from what its type shows.</summary>
    static UIQuery.CardRules rulesOf(GraphQLFacetScope scope) => new(
        n => scope.ShowsType(n.NodeType),
        (n, propertyId) => scope.Shows(n.NodeType, propertyId),
        (dm, n) => UIQuery.nameOf(dm, n, propertyId => scope.Shows(n.NodeType, propertyId)).Name);

    // ---- the requests ----

    static object model(NodeStore s, RelatudeGraphQL endpoint, QueryContext? ctx) {
        var definition = endpoint.Definition;
        return new {
            Name = string.IsNullOrWhiteSpace(definition.Name) ? "GraphQL" : definition.Name.Trim(),
            definition.Description,
            Types = endpoint.FacetScope.Types.Select(t => new { t.Id, Name = t.CodeName, Count = count(s, t, ctx) }).ToArray(),
            MaxCards = Math.Max(1, definition.MaxFacetCards),
        };
    }

    static int count(NodeStore s, NodeTypeModel type, QueryContext? ctx) {
        try {
            return s.QueryType(type.Id, ctx).Count();
        } catch {
            return 0; // one type the engine cannot count must not take the page down
        }
    }

    async Task<object> search(NodeStore s, GraphQLFacetScope scope, SearchRequest r, QueryContext? ctx) {
        var dm = s.Datastore.Datamodel;
        var type = typeOf(scope, r.TypeId);
        var allowed = scope.PropertiesOf(type.Id);
        var payload = new UIQuery.SearchPayload(Guid.Empty, type.Id, r.Text, SemanticRatio: 0, MinimumSimilarity: null,
            Selections: selectionsOf(allowed, r.Selections), Expanded: null, Page: 0, PageSize: 1, Facets: true, Summary: true);
        var queryString = UIQuery.queryFor(s, dm, payload, type.Id, 0, 1);
        var sw = Stopwatch.StartNew();
        var data = await s.Datastore.QueryAsync(queryString, [], ctx);
        sw.Stop();
        var result = data as FacetQueryResultData;
        var nodes = result?.Result ?? data as IStoreNodeDataCollection ?? throw new Exception("The query did not return a collection of nodes.");
        var expanded = new HashSet<Guid>(r.Expanded ?? []);
        var facets = result == null ? [] : result.Facets.Values
            .Where(f => allowed.Contains(f.PropertyId))
            .Select(f => UIQuery.facetView(dm, f, expanded.Contains(f.PropertyId)))
            .Where(f => f != null)
            .OrderBy(f => f!.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new {
            TypeId = type.Id,
            TypeName = type.CodeName,
            Total = nodes.TotalCount,
            SourceCount = result?.SourceCount ?? nodes.TotalCount,
            DurationMs = sw.Elapsed.TotalMilliseconds,
            Facets = facets,
        };
    }

    /// <summary>The admin picture's property list (query-pivot-model) for the exposed properties that can group or sort.</summary>
    static object pivotModel(NodeStore s, GraphQLFacetScope scope, TypeRequest r) {
        var dm = s.Datastore.Datamodel;
        var type = typeOf(scope, r.TypeId);
        var allowed = scope.PropertiesOf(type.Id);
        var properties = dm.NodeTypes.Values
            .Where(t => t.ThisAndAllInheritedTypes.ContainsKey(type.Id))
            .SelectMany(t => t.AllProperties.Values)
            .DistinctBy(p => p.Id)
            .Where(p => !p.Internal && allowed.Contains(p.Id))
            .Select(p => new {
                p.Id,
                Name = p.CodeName,
                Type = p.PropertyType.ToString(),
                Groupable = UIQuery.isGroupable(p),
                Aggregatable = UIQuery.isAggregatable(p),
                Numeric = UIQuery.isNumeric(p),
                IsDate = p.PropertyType is PropertyType.DateTime or PropertyType.DateTimeOffset,
                Geo = false,
                Words = false,
                WordsCostMs = 0d,
                DeclaredBy = p.NodeType == type.Id ? null : dm.NodeTypes.TryGetValue(p.NodeType, out var declaring) ? declaring.CodeName : null,
            })
            .Where(p => p.Groupable || p.Aggregatable)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new { TypeId = type.Id, TypeName = type.CodeName, Properties = properties };
    }

    static async Task<object> visual(NodeStore s, GraphQLFacetScope scope, VisualRequest r, QueryContext? ctx, int maxCards) {
        var type = typeOf(scope, r.TypeId);
        var allowed = scope.PropertiesOf(type.Id);
        var payload = new UIQuery.VisualPayload(Guid.Empty, type.Id, r.Text, SemanticRatio: 0, MinimumSimilarity: null,
            Selections: selectionsOf(allowed, r.Selections),
            Properties: [.. (r.Properties ?? []).Where(p => allowed.Contains(p.PropertyId))],
            MaxCards: r.MaxCards,
            SortBy: r.SortBy is Guid sort && allowed.Contains(sort) ? sort : null,
            SortDescending: r.SortDescending);
        return await UIQuery.VisualOf(s, payload, ctx, Math.Max(1, maxCards));
    }

    sealed record TypeRequest(Guid? TypeId);
    sealed record SearchRequest(Guid? TypeId, string? Text, UIQuery.FacetSelection[]? Selections, Guid[]? Expanded);
    sealed record VisualRequest(Guid? TypeId, string? Text, UIQuery.FacetSelection[]? Selections, UIQuery.VisualLevelPayload[]? Properties,
        Guid? SortBy = null, bool SortDescending = false, int MaxCards = 0);
    sealed record CardsRequest(int[]? Ids);
    sealed record CardImagesRequest(int Level, UIQuery.CardImageItem[]? Items);
}
