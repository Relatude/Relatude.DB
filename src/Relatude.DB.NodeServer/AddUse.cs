using Relatude.DB;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Json;

// NO NAMASPACE ON PURPOSE
public static class AddUse {
    public static WebApplicationBuilder AddRelatudeDB(this WebApplicationBuilder builder) {
        builder.Services.ConfigureHttpJsonOptions(o => RelatudeDBJsonOptions.ConfigureDefault(o.SerializerOptions));
        builder.Services.AddSingleton<RelatudeDBContext>();
        return builder;
    }
    public static IEndpointRouteBuilder UseRelatudeDB(this WebApplication app, string? urlPath = Defaults.AdminUrlRoot,
        string? dataFolderPath = null, string? tempFolderPath = null, ISettingsLoader? settingsIO = null) {
        return UseRelatudeDBAsync(app, urlPath, dataFolderPath, tempFolderPath, settingsIO).Result;
    }
    public static async Task<IEndpointRouteBuilder> UseRelatudeDBAsync(this WebApplication app, string? urlPath = Defaults.AdminUrlRoot,
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
