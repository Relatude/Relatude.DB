using Relatude.DB.Nodes;
using Relatude.DB.NodeServer;

// NO NAMASPACE ON PURPOSE - this is a global extension class
public class RelatudeDBContext {

}
public static class GlobalExtensions {
    public static WebApplicationBuilder AddRelatudeDB(this WebApplicationBuilder builder) {
        builder.Services.ConfigureHttpJsonOptions(o => {
            o.SerializerOptions.Converters.Add(new RelationJsonConverter());
            o.SerializerOptions.TypeInfoResolver = RelationJsonConverter.CreateResolver();
        });
        builder.Services.AddTransient((service) => RelatudeDBServer.Default);
        return builder;
    }
    public static IEndpointRouteBuilder UseRelatudeDB(this WebApplication app, string? urlPath = "/relatude.db",
        string? dataFolderPath = null, string? tempFolderPath = null, ISettingsLoader? settingsIO = null) {
        return RelatudeDBServer.UseRelatudeDB(app, urlPath, dataFolderPath, tempFolderPath, settingsIO);
    }
    public static Task<IEndpointRouteBuilder> UseRelatudeDBAsync(this WebApplication app, string? urlPath = "/relatude.db",
        string? dataFolderPath = null, string? tempFolderPath = null, ISettingsLoader? settingsIO = null) {
        return RelatudeDBServer.UseWAFDBAsync(app, urlPath, dataFolderPath, tempFolderPath, settingsIO);
    }
}
