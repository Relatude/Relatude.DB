using Relatude.DB.Nodes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relatude.DB.NodeServer.Json;
public static class RelatudeDBJsonOptions {
    // Made once, when the class is first used, and read-only before anyone gets them: requests come in
    // together, and options handed out half made could be used (which makes them read-only) by one request
    // while another was still configuring them.
    public static JsonSerializerOptions SSE { get; } = create(enumsAsNames: true);
    public static JsonSerializerOptions Default { get; } = create(enumsAsNames: false);
    static JsonSerializerOptions create(bool enumsAsNames) {
        var options = new JsonSerializerOptions();
        ConfigureDefault(options);
        if (enumsAsNames) options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly();
        return options;
    }
    public static void ConfigureDefault(JsonSerializerOptions options) {
        options.Converters.Add(new RelationJsonConverter());
        options.TypeInfoResolver = RelationJsonConverter.CreateResolver();
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    }
}
