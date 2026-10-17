using Relatude.DB.Nodes;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Json;
public static class DBBuilderExtensions {
    public static IHostApplicationBuilder AddRelatudeDB(this IHostApplicationBuilder builder, Action<ServerOptions>? configureOptions = null) {
        var options = new ServerOptions();
        configureOptions?.Invoke(options);
        builder.Services.ConfigureHttpJsonOptions(o => RelatudeDBJsonOptions.ConfigureDefault(o.SerializerOptions));
        builder.Services.AddSingleton<RelatudeDBContext>();
        builder.Services.AddTransient<Database>();
        builder.Services.AddSingleton(options);
        return builder;
    }
}
public static class DBAppExtensions {
    /// <summary>
    /// Registers Relatude.DB endpoints and middleware with the application's endpoint routing.
    /// </summary>
    /// <remarks>Synchronous wrapper around UseRelatudeDBAsync that blocks the calling thread. Prefer
    /// UseRelatudeDBAsync to avoid thread-pool blocking or deadlocks when used in asynchronous contexts.</remarks>
    /// <param name="app">The WebApplication whose endpoint routing will be configured.</param>
    /// <param name="urlPath">Optional base path under which Relatude.DB endpoints are mounted; null to use the default path.</param>
    /// <returns>An IEndpointRouteBuilder for further endpoint configuration.</returns>
    public static IEndpointRouteBuilder UseRelatudeDB(this WebApplication app, string? urlPath = null) {
        return UseRelatudeDBAsync(app, urlPath).Result;
    }
    public static async Task<IEndpointRouteBuilder> UseRelatudeDBAsync(this WebApplication app, string? urlPath = null) {
        await app.StartRelatudeDBAsync(urlPath);
        app.MapRelatudeDBAdmin();
        return app;
    }
    public static IEndpointRouteBuilder StartRelatudeDB(this WebApplication app, string? urlPath = null) {
        return StartRelatudeDBAsync(app, urlPath).Result;
    }
    public static async Task<IEndpointRouteBuilder> StartRelatudeDBAsync(this WebApplication app, string? urlPath = null) {
        var server = new RelatudeDBServer(urlPath);
        RelatudeDBRuntime.Initialize(server);
        var options = app.Services.GetRequiredService<ServerOptions>();
        await server.StartAsync(app, options);
        app.Use(server.Info.Middleware); // the bot check under /-info/, before the progress page, so a page's report is taken while the database opens
        app.Use(server.Authentication.StartupProgressBarMiddleware); // middleware to show opening progress page
        app.Use(server.Authentication.AuthorizationMiddleware); // authentication middleware for server admin UI and API
        app.Use(server.GraphQL.Middleware); // the GraphQL endpoints defined per database in the admin UI
        return app;
    }
    public static IEndpointRouteBuilder MapRelatudeDBAdmin(this WebApplication app, string? urlPath = null) {
        var server = RelatudeDBRuntime.Server;
        server.MapAdminAPI(app);
        return app;
    }
    public static IEndpointRouteBuilder MapRelatudeDBClient(this WebApplication app) {
        var options = app.Services.GetRequiredService<ServerOptions>();
        //var urlPathFiles = options.FileHandlerRootUrl == null ? ServerOptions.DefaultFileRootUrl : options.FileHandlerRootUrl;
        //if (string.IsNullOrWhiteSpace(urlPathFiles)) throw new ArgumentException("URL path cannot be null or whitespace.", nameof(urlPathFiles));
        //if (!urlPathFiles.StartsWith("/")) urlPathFiles = "/" + urlPathFiles;
        //if (urlPathFiles.EndsWith("/")) urlPathFiles = urlPathFiles.TrimEnd('/');
        //app.MapGet(urlPathFiles + "/{query}", FileHandler.HandleFileAsync);
        return app;
    }
}
public class Database : NodeStore {
    public Database() : base(RelatudeDBRuntime.Database.Datastore, settings: RelatudeDBRuntime.Server.CustomSettings.SourceFor(RelatudeDBRuntime.Server.Settings.DefaultStoreId)) { }
    public override void Dispose() {
        // do nothing!
    }
}
