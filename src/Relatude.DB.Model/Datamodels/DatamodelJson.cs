using System.Text.Json;
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
        if (stripProvenance || stripDerived) {
            var resolver = new DefaultJsonTypeInfoResolver();
            if (stripProvenance) resolver.Modifiers.Add(stripProvenanceProperties);
            if (stripDerived) resolver.Modifiers.Add(stripDerivedProperties);
            options.TypeInfoResolver = resolver;
        }
        return options;
    }
    static void stripDerivedProperties(JsonTypeInfo typeInfo) {
        if (typeInfo.Type == typeof(ReferencePropertyModel) || typeInfo.Type == typeof(ReferencesPropertyModel)) {
            removeProperty(typeInfo, "NodeTypesNames");
        } else if (typeInfo.Type == typeof(EmbeddedPropertyModel)) {
            removeProperty(typeInfo, nameof(EmbeddedPropertyModel.InnerNodeTypesNames));
            removeProperty(typeInfo, nameof(EmbeddedPropertyModel.KeyPropertyName));
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
    /// relations and properties with the same settings. This is the datamodel editor's checksum; the
    /// store's state file uses <see cref="SerializeForChecksum"/> directly.
    /// </summary>
    public static Guid Checksum(Datamodel datamodel) => JsonSerializer.Serialize(datamodel, CompareOptions).GenerateHashGuid();
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
