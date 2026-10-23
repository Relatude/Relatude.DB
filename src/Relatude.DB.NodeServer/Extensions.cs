using Relatude.DB.Nodes;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Json;
public static class DBBuilderExtensions {
    /// <summary>
    /// Adds Relatude.DB to the application. Every call configures the same <see cref="ServerOptions"/>, so it can
    /// be called any number of times and in any order, also by libraries that build on it.
    /// </summary>
    public static IHostApplicationBuilder AddRelatudeDB(this IHostApplicationBuilder builder, Action<ServerOptions>? configureOptions = null) {
        var options = builder.Services.Where(d => !d.IsKeyedService).Select(d => d.ImplementationInstance).OfType<ServerOptions>().FirstOrDefault();
        if (options == null) {
            options = new ServerOptions();
            builder.Services.ConfigureHttpJsonOptions(o => RelatudeDBJsonOptions.ConfigureDefault(o.SerializerOptions));
            builder.Services.AddSingleton<RelatudeDBContext>();
            builder.Services.AddSingleton(options);
        }
        configureOptions?.Invoke(options);
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
    const string usedKey = "Relatude.DB.Used";
    /// <summary>
    /// Starts the server and adds its middleware and endpoints. A second call on the same application does nothing.
    /// There is one server per process, held by <see cref="RelatudeDBRuntime"/>, so a second application throws.
    /// </summary>
    public static async Task<IEndpointRouteBuilder> UseRelatudeDBAsync(this WebApplication app, string? urlPath = null) {
        var properties = ((IApplicationBuilder)app).Properties;
        if (properties.ContainsKey(usedKey)) return app;
        properties[usedKey] = true;
        var server = new RelatudeDBServer(urlPath);
        RelatudeDBRuntime.Initialize(server);
        var options = app.Services.GetRequiredService<ServerOptions>();
        await server.StartAsync(app, options);
        app.Use(server.Info.Middleware); // the bot check under /-info/, before the progress page, so a page's report is taken while the database opens
        app.Use(server.Authentication.StartupProgressBarMiddleware); // middleware to show opening progress page
        app.Use(server.Authentication.AuthorizationMiddleware); // authentication middleware for server admin UI and API
        app.Use(server.GraphQL.Middleware); // the GraphQL endpoints defined per database in the admin UI
        server.MapAdminAPI(app);
        return app;
    }
}
