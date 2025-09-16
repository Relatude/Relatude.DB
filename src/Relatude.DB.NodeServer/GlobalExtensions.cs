using Relatude.DB.Nodes;
using Relatude.DB.NodeServer;
using System.Text.Json;

// NO NAMASPACE ON PURPOSE
public static class GlobalExtensions {
    static JsonSerializerOptions? _defaultJsonHttpOptions;
    public static JsonSerializerOptions DefaultJsonHttpOptions {
        get {
            if (_defaultJsonHttpOptions == null) {
                _defaultJsonHttpOptions = new JsonSerializerOptions();
                ConfigureDefaultJsonHttpOptions(_defaultJsonHttpOptions);
            }
            return _defaultJsonHttpOptions;
        }
    }
    public static void ConfigureDefaultJsonHttpOptions(JsonSerializerOptions options) {
        options.Converters.Add(new RelationJsonConverter());
        options.TypeInfoResolver = RelationJsonConverter.CreateResolver();
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    }
    public static WebApplicationBuilder AddRelatudeDB(this WebApplicationBuilder builder) {
        builder.Services.ConfigureHttpJsonOptions(o => ConfigureDefaultJsonHttpOptions(o.SerializerOptions));
        builder.Services.AddSingleton<RelatudeDBContext>();
        return builder;
    }
    public static IEndpointRouteBuilder UseRelatudeDB(this WebApplication app, string? urlPath = "/relatude.db",
        string? dataFolderPath = null, string? tempFolderPath = null, ISettingsLoader? settingsIO = null) {
        return UseRelatudeDBAsync(app, urlPath, dataFolderPath, tempFolderPath, settingsIO).Result;
    }
    public static async Task<IEndpointRouteBuilder> UseRelatudeDBAsync(this WebApplication app, string? urlPath = "/relatude.db",
        string? dataFolderPath = null, string? tempFolderPath = null, ISettingsLoader? settingsIO = null) {
        var server = new RelatudeDBServer(urlPath);
        await server.StartAsync(app, dataFolderPath, tempFolderPath, settingsIO);
        app.Use(server.StartupProgressBarMiddleware); // middleware to show opening progress page
        app.Use(server.Authentication.AuthorizationMiddleware); // authentication middleware for server admin UI and API
        server.MapSimpleAPI(app);
        RelatudeDBRuntime.Initialize(server);
        return app;
    }
}
