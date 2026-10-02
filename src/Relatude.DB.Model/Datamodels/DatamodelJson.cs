using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Relatude.DB.Common;
using Relatude.DB.Datamodels.Properties;

namespace Relatude.DB.Datamodels;

/// <summary>
/// The canonical JSON (de)serialization of a Datamodel, used by JsonFile datamodel sources
/// and anywhere a full round-trip of the model is needed.
/// </summary>
public static class DatamodelJson {
    /// <summary>Options for reading and writing datamodel JSON files.</summary>
    public static readonly JsonSerializerOptions Options = create(stripProvenance: false);
    /// <summary>
    /// Options for checksums of the datamodel (deciding when indexes must be rebuilt): identical
    /// to Options except that provenance (Sources, DatamodelSourceId, DatamodelSourceFilename) is
    /// stripped, so moving a type between sources or renaming a source does not trigger a rebuild.
    /// </summary>
    public static readonly JsonSerializerOptions ChecksumOptions = create(stripProvenance: true);
    /// <summary>
    /// Options for deciding whether two models say the same thing: <see cref="ChecksumOptions"/> with
    /// the derived name fields left out as well (NodeTypesNames, InnerNodeTypesNames, KeyPropertyName).
    /// Those are resolved from the ids they stand next to and differ between a model read from
    /// attributes and one read from JSON or generated code, without meaning anything different.
    /// </summary>
    public static readonly JsonSerializerOptions CompareOptions = create(stripProvenance: true, stripDerived: true);
    static JsonSerializerOptions create(bool stripProvenance, bool stripDerived = false) {
        var options = new JsonSerializerOptions {
            PropertyNameCaseInsensitive = true, // accepts files saved with camelCase policies
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new PropertyModelJsonConverter());
        options.Converters.Add(new BrowserSafeInt64Converter());
        options.Converters.Add(new BrowserSafeDecimalConverter());
        if (stripProvenance || stripDerived) {
            var resolver = new DefaultJsonTypeInfoResolver();
            if (stripProvenance) resolver.Modifiers.Add(stripProvenanceProperties);
            if (stripDerived) resolver.Modifiers.Add(stripDerivedProperties);
            options.TypeInfoResolver = resolver;
        }
        return options;
    }
    // The model travels through the admin UI, whose JavaScript numbers are doubles: long.MinValue (the
    // default MinValue of every long property) comes back as -9223372036854776000 and decimal.MaxValue as
    // 7.922816251426434E+28, each just outside the type's range, and a draft holding a long or a decimal
    // property could not be read back at all. A number past the range is read as the limit it stands for;
    // anything else reads as before. Written values are untouched.
    sealed class BrowserSafeInt64Converter : JsonConverter<long> {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
            if (reader.TokenType == JsonTokenType.String) return long.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            if (reader.TryGetInt64(out var value)) return value;
            var d = reader.GetDouble();
            if (d <= long.MinValue) return long.MinValue;
            if (d >= long.MaxValue) return long.MaxValue;
            return (long)Math.Round(d);
        }
        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }
    sealed class BrowserSafeDecimalConverter : JsonConverter<decimal> {
        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
            if (reader.TokenType == JsonTokenType.String) return decimal.Parse(reader.GetString()!, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
            if (reader.TryGetDecimal(out var value)) return value;
            var d = reader.GetDouble();
            if (d <= (double)decimal.MinValue) return decimal.MinValue;
            if (d >= (double)decimal.MaxValue) return decimal.MaxValue;
            return (decimal)d;
        }
        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }
    static void stripDerivedProperties(JsonTypeInfo typeInfo) {
        if (typeInfo.Type == typeof(ReferencePropertyModel) || typeInfo.Type == typeof(ReferencesPropertyModel)) {
            removeProperty(typeInfo, "NodeTypesNames");
        } else if (typeInfo.Type == typeof(EmbeddedPropertyModel)) {
            removeProperty(typeInfo, nameof(EmbeddedPropertyModel.InnerNodeTypesNames));
            removeProperty(typeInfo, nameof(EmbeddedPropertyModel.KeyPropertyName));
        } else if (typeInfo.Type == typeof(NodeTypeOverride) || typeInfo.Type == typeof(PropertyOverride)) {
            // the names next to the ids are for people reading the file, and follow the model when written
            removeProperty(typeInfo, nameof(NodeTypeOverride.Name));
        }
    }
    static void stripProvenanceProperties(JsonTypeInfo typeInfo) {
        if (typeInfo.Type == typeof(Datamodel)) {
            removeProperty(typeInfo, nameof(Datamodel.Sources));
        } else if (typeInfo.Type == typeof(NodeTypeModel)) {
            removeProperty(typeInfo, nameof(NodeTypeModel.DatamodelSourceId));
            removeProperty(typeInfo, nameof(NodeTypeModel.DatamodelSourceFilename));
        } else if (typeInfo.Type == typeof(RelationModel)) {
            removeProperty(typeInfo, nameof(RelationModel.DatamodelSourceId));
            removeProperty(typeInfo, nameof(RelationModel.DatamodelSourceFilename));
        }
    }
    static void removeProperty(JsonTypeInfo typeInfo, string name) {
        for (var i = typeInfo.Properties.Count - 1; i >= 0; i--)
            if (typeInfo.Properties[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                typeInfo.Properties.RemoveAt(i);
    }
    public static string Serialize(Datamodel datamodel) => JsonSerializer.Serialize(datamodel, Options);
    public static string SerializeForChecksum(Datamodel datamodel) => JsonSerializer.Serialize(datamodel, ChecksumOptions);
    /// <summary>
    /// A stable identity of what the model says, ignoring where it came from and the derived name
    /// fields (<see cref="CompareOptions"/>). Two models with the same checksum define the same types,
    /// relations and properties with the same settings, whatever order they were added in: the JSON
    /// is put in canonical form first (<see cref="CanonicalJson"/>). This is the datamodel editor's
    /// checksum; the store's state file uses <see cref="SerializeForChecksum"/> directly.
    /// </summary>
    public static Guid Checksum(Datamodel datamodel) => CanonicalJson(datamodel, CompareOptions).GenerateHashGuid();
    /// <summary>
    /// The value serialized with the given options and rewritten so that two values that say the same
    /// give the same text: the members of every object are sorted by name, lists of type ids are
    /// sorted, and there is no whitespace. Needed because the model's dictionaries (NodeTypes,
    /// Relations, Properties) serialize in insertion order, and that order depends on who built the
    /// model: the editor appends a new type at the end of the list, the loader places it after the
    /// other types of its source. The same model must still compare equal, or a draft written into
    /// compiled source code is never recognized as active once the application is rebuilt.
    /// </summary>
    public static string CanonicalJson<T>(T value, JsonSerializerOptions options) {
        var node = JsonSerializer.SerializeToNode(value, options);
        return canonical(node, null, false)?.ToJsonString() ?? "null";
    }
    /// <summary>
    /// The members holding overrides (<see cref="Datamodel.Overrides"/>, <see cref="NodeTypeModel.PropertyOverrides"/>).
    /// Inside them an object that is empty overrides nothing - what is left of an override that was reset -
    /// so it is left out of the canonical form, and no overrides, null and empty ones all read the same.
    /// </summary>
    static readonly HashSet<string> overrideMembers = new(StringComparer.OrdinalIgnoreCase) { nameof(Datamodel.Overrides), nameof(NodeTypeModel.PropertyOverrides) };
    /// <summary>
    /// The lists of type ids in the model classes. They are sets - the types a type inherits, the
    /// types a relation or a reference property points to - so their order carries no meaning, and
    /// the loaders do not agree on it (reflection lists the base class first, the editor keeps the
    /// order the user picked). Only lists are sorted: <c>NodeTypes</c> is also the name of the model's
    /// dictionary of types, which is an object and is left alone.
    /// </summary>
    static readonly HashSet<string> idSetLists = new(StringComparer.OrdinalIgnoreCase) {
        nameof(NodeTypeModel.Parents), nameof(RelationModel.SourceTypes), nameof(RelationModel.TargetTypes),
        nameof(ReferencePropertyModel.NodeTypes), nameof(EmbeddedPropertyModel.InnerNodeTypes),
    };
    static JsonNode? canonical(JsonNode? node, string? name, bool inOverrides) {
        switch (node) {
            case JsonObject obj: {
                    var sorted = new JsonObject();
                    foreach (var member in obj.OrderBy(m => m.Key, StringComparer.Ordinal)) {
                        var overrides = inOverrides || overrideMembers.Contains(member.Key);
                        var value = canonical(member.Value, member.Key, overrides);
                        if (overrides && value is null or JsonObject { Count: 0 }) continue;
                        sorted[member.Key] = value;
                    }
                    return sorted;
                }
            case JsonArray array: {
                    var items = array.Select(item => canonical(item, null, inOverrides)).ToList();
                    if (name != null && idSetLists.Contains(name) && items.All(i => i is JsonValue v && v.GetValueKind() == JsonValueKind.String))
                        items.Sort((a, b) => string.CompareOrdinal(a!.GetValue<string>(), b!.GetValue<string>()));
                    return new JsonArray(items.ToArray());
                }
            default:
                return node?.DeepClone(); // a value; nodes cannot be shared between parents, hence the copy
        }
    }
    /// <summary>
    /// The content of one JSON datamodel source file holding the given node types and relations:
    /// only those, without the built in base type, without the source list and without provenance
    /// (the loader stamps the source and file name back on when it reads the file). This is what the
    /// datamodel editor writes when it saves a model back into a JsonFile source.
    /// </summary>
    public static string SerializeForSourceFile(Datamodel datamodel, IEnumerable<Guid> nodeTypeIds, IEnumerable<Guid> relationIds) {
        var part = new Datamodel();
        part.NodeTypes.Clear(); // the base type is implied
        foreach (var id in nodeTypeIds) {
            if (id == NodeConstants.BaseNodeTypeId) continue;
            part.NodeTypes.Add(id, datamodel.NodeTypes[id]);
        }
        foreach (var id in relationIds) part.Relations.Add(id, datamodel.Relations[id]);
        return JsonSerializer.Serialize(part, ChecksumOptions);
    }
    public static Datamodel Deserialize(string json) {
        var datamodel = JsonSerializer.Deserialize<Datamodel>(json, Options);
        if (datamodel == null) throw new Exception("The datamodel JSON is empty or contains only null. ");
        normalizeIds(datamodel);
        return datamodel;
    }
    // Node types, relations and properties sit in dictionaries keyed by their id, so hand-written
    // files do not have to repeat the id inside each object: an omitted Id adopts the key, and a
    // disagreeing Id is an error.
    static void normalizeIds(Datamodel datamodel) {
        foreach (var (key, nodeType) in datamodel.NodeTypes) {
            if (nodeType.Id == Guid.Empty) nodeType.Id = key;
            else if (nodeType.Id != key) throw new Exception("The node type " + nodeType.FullName
                + " is keyed as " + key + " but declares the id " + nodeType.Id + ". Remove the Id or make it match the key. ");
            foreach (var (propertyKey, property) in nodeType.Properties) {
                if (property.Id == Guid.Empty) property.Id = propertyKey;
                else if (property.Id != propertyKey) throw new Exception("The property " + nodeType.CodeName + "." + property.CodeName
                    + " is keyed as " + propertyKey + " but declares the id " + property.Id + ". Remove the Id or make it match the key. ");
            }
        }
        foreach (var (key, relation) in datamodel.Relations) {
            if (relation.Id == Guid.Empty) relation.Id = key;
            else if (relation.Id != key) throw new Exception("The relation " + relation.CodeName
                + " is keyed as " + key + " but declares the id " + relation.Id + ". Remove the Id or make it match the key. ");
        }
    }
}
