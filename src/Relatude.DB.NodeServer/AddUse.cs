using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Json;
using Relatude.DB.Web;

// NO NAMESPACE ON PURPOSE
public static class AddUse {
    public static WebApplicationBuilder AddRelatudeDB(this WebApplicationBuilder builder, Action<ServerOptions>? configureOptions = null) {
        var options = new ServerOptions();
        configureOptions?.Invoke(options);
        builder.Services.ConfigureHttpJsonOptions(o => RelatudeDBJsonOptions.ConfigureDefault(o.SerializerOptions));
        builder.Services.AddSingleton<RelatudeDBContext>();
        builder.Services.AddSingleton(options);
        return builder;
    }
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
        app.Use(server.Authentication.StartupProgressBarMiddleware); // middleware to show opening progress page
        app.Use(server.Authentication.AuthorizationMiddleware); // authentication middleware for server admin UI and API
        return app;
    }
    public static IEndpointRouteBuilder MapRelatudeDBAdmin(this WebApplication app, string? urlPath = null) {
        var server = RelatudeDBRuntime.Server;
        server.MapAdminAPI(app);
        return app;
    }
    public static IEndpointRouteBuilder MapRelatudeDBClient(this WebApplication app) {
        var options = app.Services.GetRequiredService<ServerOptions>();
        var urlPathFiles = options.FileHandlerRootUrl == null ? ServerOptions.DefaultFileRootUrl : options.FileHandlerRootUrl;
        if (string.IsNullOrWhiteSpace(urlPathFiles)) throw new ArgumentException("URL path cannot be null or whitespace.", nameof(urlPathFiles));
        if (!urlPathFiles.StartsWith("/")) urlPathFiles = "/" + urlPathFiles;
        if (urlPathFiles.EndsWith("/")) urlPathFiles = urlPathFiles.TrimEnd('/');
        app.MapGet(urlPathFiles + "/{propPathAndAdj}", FileHandler.HandleFileAsync);
        return app;
    }
}
