using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
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
    static JsonSerializerOptions create(bool stripProvenance) {
        var options = new JsonSerializerOptions {
            PropertyNameCaseInsensitive = true, // accepts files saved with camelCase policies
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new PropertyModelJsonConverter());
        if (stripProvenance) {
            options.TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { stripProvenanceProperties } };
        }
        return options;
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
