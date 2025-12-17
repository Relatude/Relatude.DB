using Relatude.DB.Nodes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relatude.DB.NodeServer.Json;
public static class RelatudeDBJsonOptions {
    static JsonSerializerOptions? _defaultJsonSEEOptions;
    public static JsonSerializerOptions SSE {
        get {
            if (_defaultJsonSEEOptions == null) {
                _defaultJsonSEEOptions = new JsonSerializerOptions();
                ConfigureDefault(_defaultJsonSEEOptions);
                _defaultJsonSEEOptions.Converters.Add(new JsonStringEnumConverter());

            }
            return _defaultJsonSEEOptions;
        }
    }
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
