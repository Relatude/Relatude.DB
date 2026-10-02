using System.Runtime.CompilerServices;
using Relatude.DB.DataStores;
using Relatude.DB.GraphQL;
using Relatude.DB.GraphQL.Endpoints;

namespace Relatude.DB.NodeServer.GraphQL;

/// <summary>
/// Serves the GraphQL endpoints defined for each database: the definitions are "graphql/*.json" files on the
/// database's storage, loaded on demand and mapped to their urls by a middleware. Executors are built per store
/// instance and definition, so a reopened database or a saved definition gets a fresh schema.
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
    Dictionary<string, Route> _routes = new(StringComparer.OrdinalIgnoreCase);
    string? _loadedFor; // the container set the tables were built for

    internal GraphQLEndpointServer(RelatudeDBServer server) { _server = server; }

    /// <summary>Forgets the loaded definitions; the next request reads the files again.</summary>
    public void Invalidate() {
        lock (_lock) _loadedFor = null;
    }

    public GraphQLEndpointStore? StoreFor(NodeStoreContainer container) {
        var io = _server.GetOrNullIO(container.Settings.IoDatabase);
        return io == null ? null : new GraphQLEndpointStore(io);
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

    public GraphQLEndpointFile Save(NodeStoreContainer container, GraphQLEndpointDefinition definition) {
        var store = StoreFor(container) ?? throw new Exception("The database has no storage provider for its endpoint files.");
        var existing = definition.Id == Guid.Empty ? null : Find(container.Settings.Id, definition.Id);
        var saved = store.Save(definition, existing?.Key);
        Invalidate();
        return saved;
    }

    public void Delete(NodeStoreContainer container, Guid endpointId) {
        var store = StoreFor(container) ?? throw new Exception("The database has no storage provider for its endpoint files.");
        var existing = Find(container.Settings.Id, endpointId) ?? throw new Exception("The endpoint was not found.");
        store.Delete(existing.Key);
        Invalidate();
    }

    /// <summary>The executor for a definition over the container's open store; cached until either changes.</summary>
    public RelatudeGraphQL GetExecutor(NodeStoreContainer container, GraphQLEndpointDefinition definition) {
        var nodeStore = container.Store ?? throw new Exception("The database is not open.");
        var dataStore = nodeStore.Datastore;
        var executors = _executors.GetValue(dataStore, _ => new Executors());
        lock (executors) {
            if (executors.ByEndpoint.TryGetValue(definition.Id, out var entry) && ReferenceEquals(entry.Definition, definition)) return entry.Executor;
            var executor = new RelatudeGraphQL(dataStore, definition, OptionsFor(nodeStore));
            executors.ByEndpoint[definition.Id] = (definition, executor);
            return executor;
        }
    }

    /// <summary>Mutations go through the NodeStore so transaction plugins see them.</summary>
    public static GraphQLOptions OptionsFor(Nodes.NodeStore nodeStore) => new() {
        TransactionExecutor = (_, transaction, _) => nodeStore.Execute(transaction),
    };

    void ensureLoaded() {
        var signature = string.Join(",", _server.Containers.Keys.OrderBy(k => k).Select(k => k.ToString("N")));
        lock (_lock) {
            if (_loadedFor == signature) return;
            var files = new Dictionary<Guid, List<GraphQLEndpointFile>>();
            var routes = new Dictionary<string, Route>(StringComparer.OrdinalIgnoreCase);
            foreach (var container in _server.GetContainers()) {
                List<GraphQLEndpointFile> list;
                try {
                    list = StoreFor(container)?.Load() ?? [];
                } catch (Exception ex) {
                    RelatudeDBServer.Trace($"GraphQL endpoints of {container.Settings.Name} could not be read: {ex.Message}");
                    list = [];
                }
                files[container.Settings.Id] = list;
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
