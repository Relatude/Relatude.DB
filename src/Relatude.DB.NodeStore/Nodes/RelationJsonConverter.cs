using Relatude.DB.Demo.Models;
using Relatude.DB.Nodes;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Relatude.DB.Nodes;
public class RelationJsonConverter : JsonConverterFactory {
    public override bool CanConvert(Type typeToConvert) {
        return typeof(IRelationProperty).IsAssignableFrom(typeToConvert);
    }
    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options) {
        return new IRelationJsonConverter();
    }
}
public class IRelationJsonConverter : JsonConverter<IRelationProperty> {
    public override IRelationProperty? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        throw new NotImplementedException();
    }
    public override void Write(Utf8JsonWriter writer, IRelationProperty value, JsonSerializerOptions options) {
        if (value is IOneProperty one) {
            var node = one.GetIncludedData();
            if (node == null) writer.WriteNullValue();
            else JsonSerializer.Serialize(writer, node, options);
        } else if (value is IManyProperty many) {
            JsonSerializer.Serialize(writer, many.GetIncludedData(), options);
        } else throw new NotImplementedException("Unknown IRelationProperty type: " + value.GetType().FullName);
    }
}

public static class IRelationPropertyJsonTypeInfoResolver {
    public static DefaultJsonTypeInfoResolver Create() {
        return new DefaultJsonTypeInfoResolver {
            Modifiers = { static typeInfo =>
            {
                foreach (var prop in typeInfo.Properties)
                {
                    if (typeof(IRelationProperty).IsAssignableFrom(prop.PropertyType))
                    {
                        // Skip the property entirely if HasIncludedData() == false
                        prop.ShouldSerialize = static (obj, value) =>
                        {
                            if (value is IRelationProperty rp) return rp.HasIncludedData();
                            return true;
                        };
                    }
                }
            } }
        };
    }
}