using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relatude.DB.Datamodels.Properties;

/// <summary>
/// Polymorphic System.Text.Json converter for the abstract PropertyModel. The already-serialized
/// PropertyType value doubles as the type discriminator, read position-independently by buffering
/// the object. Only the exact base type is handled, so the concrete types keep the default
/// (de)serialization logic without recursion.
/// </summary>
public sealed class PropertyModelJsonConverter : JsonConverter<PropertyModel> {
    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(PropertyModel);
    public override PropertyModel? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType == JsonTokenType.Null) return null;
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (!root.TryGetProperty("PropertyType", out var discriminator) && !root.TryGetProperty("propertyType", out discriminator))
            throw new JsonException("The property model is missing its PropertyType discriminator. ");
        PropertyType propertyType;
        if (discriminator.ValueKind == JsonValueKind.String) {
            if (!Enum.TryParse(discriminator.GetString(), ignoreCase: true, out propertyType))
                throw new JsonException("Unknown PropertyType \"" + discriminator.GetString() + "\". ");
        } else if (discriminator.ValueKind == JsonValueKind.Number) {
            propertyType = (PropertyType)discriminator.GetInt32();
        } else {
            throw new JsonException("The PropertyType discriminator must be a string or a number. ");
        }
        var concreteType = GetModelType(propertyType);
        return (PropertyModel?)root.Deserialize(concreteType, options);
    }
    public override void Write(Utf8JsonWriter writer, PropertyModel value, JsonSerializerOptions options) {
        // serializing as the runtime type includes all derived members and the PropertyType discriminator:
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
    public static Type GetModelType(PropertyType propertyType) => propertyType switch {
        PropertyType.Boolean => typeof(BooleanPropertyModel),
        PropertyType.Integer => typeof(IntegerPropertyModel),
        PropertyType.String => typeof(StringPropertyModel),
        PropertyType.StringArray => typeof(StringArrayPropertyModel),
        PropertyType.Double => typeof(DoublePropertyModel),
        PropertyType.Float => typeof(FloatPropertyModel),
        PropertyType.Decimal => typeof(DecimalPropertyModel),
        PropertyType.DateTime => typeof(DateTimePropertyModel),
        PropertyType.TimeSpan => typeof(TimeSpanPropertyModel),
        PropertyType.Guid => typeof(GuidPropertyModel),
        PropertyType.Long => typeof(LongPropertyModel),
        PropertyType.ByteArray => typeof(ByteArrayPropertyModel),
        PropertyType.File => typeof(FilePropertyModel),
        PropertyType.FloatArray => typeof(FloatArrayPropertyModel),
        PropertyType.DateTimeOffset => typeof(DateTimeOffsetPropertyModel),
        PropertyType.GuidArray => typeof(GuidArrayPropertyModel),
        PropertyType.EnumArray => typeof(EnumArrayPropertyModel),
        PropertyType.GeoCoordinate => typeof(GeoCoordinatePropertyModel),
        PropertyType.Embedded => typeof(EmbeddedPropertyModel),
        PropertyType.Reference => typeof(ReferencePropertyModel),
        PropertyType.References => typeof(ReferencesPropertyModel), // derives from GuidArrayPropertyModel, kept apart by the discriminator
        PropertyType.Relation => typeof(RelationPropertyModel),
        _ => throw new JsonException("The PropertyType " + propertyType + " has no property model type. "),
    };
}
