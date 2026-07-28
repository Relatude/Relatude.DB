using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Relatude.DB.DataStores;

namespace Relatude.DB.GraphQL;

public static class GraphQLEndpointExtensions {

    /// <summary>
    /// Maps a GraphQL endpoint whose schema reflects the Relatude.DB datamodel.
    /// POST {path} accepts {"query","operationName","variables"}; GET {path}?query=... is optional;
    /// GET {path}?sdl returns the schema as SDL text.
    /// </summary>
    public static IEndpointRouteBuilder MapRelatudeDBGraphQL(this IEndpointRouteBuilder app, string path = "/graphql", Action<GraphQLOptions>? configure = null) {
        var options = new GraphQLOptions();
        configure?.Invoke(options);
        var cache = new ExecutorCache(options);

        app.MapPost(path, async (HttpContext http) => {
            if (!tryGetExecutor(http, options, cache, out var executor, out var unavailable)) return unavailable;
            GraphQLRequest? request;
            try {
                request = await http.Request.ReadFromJsonAsync<GraphQLRequest>();
            } catch (JsonException ex) {
                return Results.BadRequest(new { errors = new[] { new { message = "Invalid JSON request body: " + ex.Message } } });
            }
            if (request == null) return Results.BadRequest(new { errors = new[] { new { message = "Empty request body." } } });
            var result = executor.Execute(request, options.QueryContextFactory?.Invoke(http));
            return graphQLJson(result);
        });

        app.MapGet(path, (HttpContext http) => {
            if (!tryGetExecutor(http, options, cache, out var executor, out var unavailable)) return unavailable;
            if (http.Request.Query.ContainsKey("sdl")) {
                return Results.Text(executor.ToSDL(), "text/plain; charset=utf-8");
            }
            if (!options.EnableGetRequests) return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
            string? query = http.Request.Query["query"];
            if (string.IsNullOrEmpty(query)) {
                return Results.BadRequest(new { errors = new[] { new { message = "Pass a GraphQL query via ?query=... or POST a JSON body." } } });
            }
            var request = new GraphQLRequest {
                Query = query,
                OperationName = http.Request.Query["operationName"],
            };
            string? variables = http.Request.Query["variables"];
            if (!string.IsNullOrEmpty(variables)) {
                try {
                    request.Variables = JsonSerializer.Deserialize<JsonElement>(variables);
                } catch (JsonException) {
                    return Results.BadRequest(new { errors = new[] { new { message = "The variables parameter is not valid JSON." } } });
                }
            }
            var result = executor.Execute(request, options.QueryContextFactory?.Invoke(http));
            return graphQLJson(result);
        });

        return app;
    }

    static IResult graphQLJson(GraphQLResult result)
        => Results.Text(result.ToJson(), "application/graphql-response+json; charset=utf-8");

    static bool tryGetExecutor(HttpContext http, GraphQLOptions options, ExecutorCache cache, out RelatudeGraphQL executor, out IResult unavailable) {
        IDataStore? store = null;
        try {
            store = options.StoreResolver != null
                ? options.StoreResolver(http)
                : http.RequestServices.GetService<IDataStore>();
        } catch {
            // resolution failures (e.g. the store has not been started yet) fall through to 503
        }
        if (store == null) {
            executor = null!;
            unavailable = Results.Json(
                new { errors = new[] { new { message = "The Relatude.DB store is not available yet. Try again shortly." } } },
                statusCode: StatusCodes.Status503ServiceUnavailable);
            return false;
        }
        executor = cache.Get(store);
        unavailable = null!;
        return true;
    }

    /// <summary>One executor per store instance; rebuilt automatically when the host swaps stores (e.g. a restart).</summary>
    sealed class ExecutorCache(GraphQLOptions options) {
        readonly ConditionalWeakTable<IDataStore, RelatudeGraphQL> _executors = [];
        public RelatudeGraphQL Get(IDataStore store) {
            if (_executors.TryGetValue(store, out var existing)) return existing;
            lock (_executors) {
                if (_executors.TryGetValue(store, out existing)) return existing;
                var created = new RelatudeGraphQL(store, options);
                _executors.Add(store, created);
                return created;
            }
        }
    }
}
