using Relatude.DB.Nodes;
using Relatude.DB.NodeServer;
using System.Text.Json;
public static class GlobalExtensions {
    public static WebApplicationBuilder AddRelatudeDB(this WebApplicationBuilder builder) {
        builder.Services.ConfigureHttpJsonOptions(o => {
            o.SerializerOptions.Converters.Add(new RelationJsonConverter());
            o.SerializerOptions.TypeInfoResolver = IRelationPropertyJsonTypeInfoResolver.Create();
        }
        );
        return builder;
    }
    public static IEndpointRouteBuilder UseRelatudeDB(this WebApplication app, string? urlPath = "/relatude.db",
        string? dataFolderPath = null, string? tempFolderPath = null, ISettingsLoader? settingsIO = null) {
        return RelatudeDBServer.UseWAFDB(app, urlPath, dataFolderPath, tempFolderPath, settingsIO);
    }
    public static Task<IEndpointRouteBuilder> UseRelatudeDBAsync(this WebApplication app, string? urlPath = "/relatude.db",
        string? dataFolderPath = null, string? tempFolderPath = null, ISettingsLoader? settingsIO = null) {
        return RelatudeDBServer.UseWAFDBAsync(app, urlPath, dataFolderPath, tempFolderPath, settingsIO);
    }
}
