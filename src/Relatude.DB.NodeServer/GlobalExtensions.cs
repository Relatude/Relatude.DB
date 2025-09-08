using Relatude.DB.Nodes;
using Relatude.DB.NodeServer;
using System.Text.Json;

// NO NAMASPACE ON PURPOSE
public class RelatudeDBContext {

}
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
