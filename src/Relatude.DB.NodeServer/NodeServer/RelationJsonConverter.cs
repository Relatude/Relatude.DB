using System.Text.Json;
using System.Text.Json.Serialization;
using Relatude.DB.Nodes;

namespace Relatude.DB.NodeServer; 
public class RelationJsonConverter : JsonConverter<IRelationProperty> {
    public override IRelationProperty? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        throw new NotImplementedException();
    }
    public override void Write(Utf8JsonWriter writer, IRelationProperty value, JsonSerializerOptions options) {
        // undefined in JSON if not included in query
        // null or empty array if included but no data
        if (!value.HasIncludedData) return; 
        if (value is IOneProperty one) JsonSerializer.Serialize(writer, one.IncludedData, options);
        else if (value is IManyProperty many) JsonSerializer.Serialize(writer, many.IncludedData, options);
        throw new NotImplementedException("Unknown IRelationProperty type: " + value.GetType().FullName);
    }
}
