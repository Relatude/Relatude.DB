using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Relatude.DB.DataStores;
using Relatude.DB.GraphQL.Endpoints;

namespace Relatude.DB.GraphQL;

public static class GraphQLEndpointExtensions {

    /// <summary>
    /// Maps a code-first GraphQL endpoint reflecting the whole datamodel on a fixed path.
    /// POST {path} accepts {"query","operationName","variables"}; GET {path}?query=... is optional;
    /// GET {path}?sdl returns the schema as SDL text. Endpoints defined in the admin UI need no mapping.
    /// </summary>
    public static IEndpointRouteBuilder MapRelatudeDBGraphQL(this IEndpointRouteBuilder app, string path = "/graphql", Action<GraphQLOptions>? configure = null) {
        var options = new GraphQLOptions();
        configure?.Invoke(options);
        var cache = new ExecutorCache(options);
        app.MapMethods(path, ["GET", "POST", "OPTIONS"], async (HttpContext http) => {
            if (!tryGetExecutor(http, options, cache, out var executor)) {
                await GraphQLHttpHandler.WriteErrorsAsync(http, StatusCodes.Status503ServiceUnavailable, "The Relatude.DB store is not available yet. Try again shortly.");
                return;
            }
            await GraphQLHttpHandler.HandleAsync(http, executor, options.QueryContextFactory?.Invoke(http));
        });
        return app;
    }

    static bool tryGetExecutor(HttpContext http, GraphQLOptions options, ExecutorCache cache, out RelatudeGraphQL executor) {
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
            return false;
        }
        executor = cache.Get(store);
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
