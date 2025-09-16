using Relatude.DB.Nodes;
using System.Text.Json;

namespace Relatude.DB.NodeServer.Json;
public static class RelatudeDBJsonOptions {
    static JsonSerializerOptions? _defaultJsonHttpOptions;
    public static JsonSerializerOptions Default {
        get {
            if (_defaultJsonHttpOptions == null) {
                _defaultJsonHttpOptions = new JsonSerializerOptions();
                ConfigureDefault(_defaultJsonHttpOptions);
            }
            return _defaultJsonHttpOptions;
        }
    }
    public static void ConfigureDefault(JsonSerializerOptions options) {
        options.Converters.Add(new RelationJsonConverter());
        options.TypeInfoResolver = RelationJsonConverter.CreateResolver();
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    }
}
