using System.Runtime.CompilerServices;
using Relatude.DB.DataStores;
using Relatude.DB.GraphQL;
using Relatude.DB.GraphQL.Endpoints;
using Relatude.DB.IO;

namespace Relatude.DB.NodeServer.GraphQL;

/// <summary>
/// Serves the GraphQL endpoints defined for each database: the definitions are json files in two folders -
/// SETTINGS, graphql/ in the database's folder in relatude.settings, and DATA, overrides/graphql/ on its storage
/// (see <see cref="GraphQLEndpointStore"/>) - loaded on demand and mapped to their urls by a middleware.
/// Executors are built per store instance and definition, so a reopened database or a saved definition gets a
/// fresh schema.
/// </summary>
public sealed class GraphQLEndpointServer {
    sealed class Route(Guid containerId, GraphQLEndpointFile file) {
        public Guid ContainerId { get; } = containerId;
        public GraphQLEndpointFile File { get; } = file;
    }
    sealed class Executors {
        public Dictionary<Guid, (GraphQLEndpointDefinition Definition, RelatudeGraphQL Executor)> ByEndpoint = [];
    }

    readonly RelatudeDBServer _server;
    readonly object _lock = new();
    readonly ConditionalWeakTable<IDataStore, Executors> _executors = [];
    Dictionary<Guid, List<GraphQLEndpointFile>> _files = [];
    Dictionary<Guid, List<LayeredDefinition>> _layers = [];
    Dictionary<string, Route> _routes = new(StringComparer.OrdinalIgnoreCase);
    string? _loadedFor; // the container set the tables were built for

    internal GraphQLEndpointServer(RelatudeDBServer server) {
        _server = server;
        FacetSearch = new GraphQLFacetSearch(server);
    }

    /// <summary>
    /// The facet search the endpoints serve when theirs is switched on (<see cref="GraphQLEndpointDefinition.EnableFacetSearch"/>).
    /// Given to every endpoint defined here; a code-first endpoint gets it through <see cref="GraphQLOptions.FacetSearch"/>.
    /// </summary>
    public GraphQLFacetSearch FacetSearch { get; }

    /// <summary>Forgets the loaded definitions; the next request reads the files again.</summary>
    public void Invalidate() {
        lock (_lock) _loadedFor = null;
    }

    public GraphQLEndpointStore? StoreFor(NodeStoreContainer container) {
        var folders = _server.GraphQLDefinitionsFor(container.Settings);
        return folders == null ? null : new GraphQLEndpointStore(folders);
    }

    public List<GraphQLEndpointFile> Files(Guid containerId) {
        ensureLoaded();
        lock (_lock) return _files.TryGetValue(containerId, out var files) ? [.. files] : [];
    }

    public GraphQLEndpointFile? Find(Guid containerId, Guid endpointId)
        => Files(containerId).FirstOrDefault(f => f.Definition?.Id == endpointId);

    /// <summary>Every readable definition on the server, for url uniqueness checks.</summary>
    public List<GraphQLEndpointDefinition> AllDefinitions() {
        ensureLoaded();
        lock (_lock) return _files.Values.SelectMany(l => l).Where(f => f.Definition != null).Select(f => f.Definition!).ToList();
    }

    /// <summary>What both folders have for every endpoint of a database, the ones taken away included.</summary>
    public List<LayeredDefinition> Layers(Guid containerId) {
        ensureLoaded();
        lock (_lock) return _layers.TryGetValue(containerId, out var layers) ? [.. layers] : [];
    }
    LayeredDefinition? layer(Guid containerId, Guid endpointId) {
        var id = endpointId.ToString();
        return Layers(containerId).FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Saves a definition to the database's DATA (see <see cref="GraphQLEndpointStore.Save"/>).</summary>
    public GraphQLEndpointFile Save(NodeStoreContainer container, GraphQLEndpointDefinition definition) {
        var store = StoreFor(container) ?? throw new Exception("The database has no storage provider for its endpoint files.");
        var existing = definition.Id == Guid.Empty ? null : layer(container.Settings.Id, definition.Id);
        var saved = store.Save(definition, existing);
        Invalidate();
        return saved;
    }

    /// <summary>Takes an endpoint away on this installation; a file that could not be read is deleted.</summary>
    public void Delete(NodeStoreContainer container, Guid endpointId) {
        var store = StoreFor(container) ?? throw new Exception("The database has no storage provider for its endpoint files.");
        var existing = Find(container.Settings.Id, endpointId) ?? throw new Exception("The endpoint was not found.");
        if (existing.Layer != null) store.Delete(existing.Layer);
        else store.DeleteFile(existing.Source, existing.Key);
        Invalidate();
    }

    /// <summary>Moves what DATA has for these endpoints into SETTINGS. Nothing that is served changes.</summary>
    public int MoveToSettings(NodeStoreContainer container, IEnumerable<Guid> endpointIds) {
        var store = StoreFor(container) ?? throw new Exception("The database has no storage provider for its endpoint files.");
        var moved = 0;
        foreach (var id in endpointIds.Distinct()) {
            if (layer(container.Settings.Id, id) is { } l && store.MoveToSettings(l)) moved++;
        }
        Invalidate();
        return moved;
    }

    /// <summary>Drops what DATA has for these endpoints: each goes back to its SETTINGS definition, or goes.</summary>
    public int DiscardData(NodeStoreContainer container, IEnumerable<Guid> endpointIds) {
        var store = StoreFor(container) ?? throw new Exception("The database has no storage provider for its endpoint files.");
        var discarded = 0;
        foreach (var id in endpointIds.Distinct()) {
            if (layer(container.Settings.Id, id) is { } l && store.DiscardData(l)) discarded++;
        }
        Invalidate();
        return discarded;
    }

    /// <summary>The executor for a definition over the container's open store; cached until either changes.</summary>
    public RelatudeGraphQL GetExecutor(NodeStoreContainer container, GraphQLEndpointDefinition definition) {
        var nodeStore = container.Store ?? throw new Exception("The database is not open.");
        var dataStore = nodeStore.Datastore;
        var executors = _executors.GetValue(dataStore, _ => new Executors());
        lock (executors) {
            if (executors.ByEndpoint.TryGetValue(definition.Id, out var entry) && ReferenceEquals(entry.Definition, definition)) return entry.Executor;
            var executor = new RelatudeGraphQL(dataStore, definition, OptionsFor(nodeStore, FacetSearch));
            executors.ByEndpoint[definition.Id] = (definition, executor);
            return executor;
        }
    }

    /// <summary>Mutations go through the NodeStore so transaction plugins see them; the facet search, when given, answers the page's facets.</summary>
    public static GraphQLOptions OptionsFor(Nodes.NodeStore nodeStore, IGraphQLFacetSearch? facetSearch = null) => new() {
        TransactionExecutor = (_, transaction, _) => nodeStore.Execute(transaction),
        FacetSearch = facetSearch,
    };

    void ensureLoaded() {
        var signature = string.Join(",", _server.Containers.Keys.OrderBy(k => k).Select(k => k.ToString("N")));
        lock (_lock) {
            if (_loadedFor == signature) return;
            var files = new Dictionary<Guid, List<GraphQLEndpointFile>>();
            var layers = new Dictionary<Guid, List<LayeredDefinition>>();
            var routes = new Dictionary<string, Route>(StringComparer.OrdinalIgnoreCase);
            foreach (var container in _server.GetContainers()) {
                List<GraphQLEndpointFile> list;
                List<LayeredDefinition> found = [];
                try {
                    list = StoreFor(container)?.Load(out found) ?? [];
                } catch (Exception ex) {
                    RelatudeDBServer.Trace($"GraphQL endpoints of {container.Settings.Name} could not be read: {ex.Message}");
                    list = [];
                }
                files[container.Settings.Id] = list;
                layers[container.Settings.Id] = found;
                foreach (var file in list) {
                    var def = file.Definition;
                    if (def == null || !def.Enabled) continue;
                    var url = GraphQLEndpointDefinition.NormalizeUrl(def.Url);
                    if (url == null) continue;
                    if (routes.TryGetValue(url, out var taken)) {
                        RelatudeDBServer.Trace($"GraphQL endpoint \"{def.Name}\" of {container.Settings.Name} is not served: {url} is already used by \"{taken.File.Definition!.Name}\".");
                        continue;
                    }
                    routes[url] = new Route(container.Settings.Id, file);
                }
            }
            _files = files;
            _layers = layers;
            _routes = routes;
            _loadedFor = signature;
        }
    }

    /// <summary>Answers requests to the urls of enabled endpoints; everything else passes through.</summary>
    public async Task Middleware(HttpContext http, Func<Task> next) {
        var path = http.Request.Path.Value;
        if (string.IsNullOrEmpty(path) || path.Length < 2) { await next(); return; }
        ensureLoaded();
        Route? route;
        lock (_lock) {
            if (_routes.Count == 0) route = null;
            else _routes.TryGetValue(path.TrimEnd('/'), out route);
        }
        if (route == null) { await next(); return; }
        if (!_server.Containers.TryGetValue(route.ContainerId, out var container)) { await next(); return; }
        var definition = route.File.Definition!;
        if (!container.IsOpen()) {
            await GraphQLHttpHandler.WriteErrorsAsync(http, StatusCodes.Status503ServiceUnavailable, $"The database \"{container.Settings.Name}\" is not open ({container.StateName}). Try again shortly.");
            return;
        }
        RelatudeGraphQL executor;
        try {
            executor = GetExecutor(container, definition);
        } catch (Exception ex) {
            await GraphQLHttpHandler.WriteErrorsAsync(http, StatusCodes.Status500InternalServerError, "The endpoint's schema could not be built: " + ex.Message);
            return;
        }
        await GraphQLHttpHandler.HandleAsync(http, executor, null);
    }
}
