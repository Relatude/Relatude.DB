using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Relatude.DB.Datamodels;

/// <summary>
/// Reads and writes a <see cref="DatamodelSource"/> whole, so the kinds that were folded together in
/// September 2026 keep loading from older settings and model files: the old type names
/// (<c>AssemblyNameReference</c>, <c>JsonFile</c>, <c>CSharpCodeFile</c>) and their numbers still read,
/// and the two file kinds set <see cref="DatamodelSource.FileFormat"/> unless the file names one
/// itself. The removed single-type kind (<c>TypeNameReference</c>, 1) is refused with the fix in the
/// message. Enums are always written by name, whatever the options say, and properties the class no
/// longer has (the per-source <c>AutoDeduceRelations</c>) are ignored on read. Property names are
/// matched case insensitively, since settings files have been written with and without camelCase.
/// </summary>
public sealed class DatamodelSourceJsonConverter : JsonConverter<DatamodelSource> {
    public override DatamodelSource? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType == JsonTokenType.Null) return null;
        var node = JsonNode.Parse(ref reader) as JsonObject ?? throw new JsonException("A datamodel source must be a JSON object. ");
        var source = new DatamodelSource();
        DatamodelSourceFileFormat? impliedFormat = null;
        foreach (var (name, value) in node) {
            switch (name.ToLowerInvariant()) {
                case "id": source.Id = guid(value) ?? Guid.Empty; break;
                case "name": source.Name = text(value); break;
                case "namespace": source.Namespace = text(value); break;
                case "filepath": source.Filepath = text(value); break;
                case "reference": source.Reference = text(value); break;
                case "sourcecodepath": source.SourceCodePath = text(value); break;
                case "fileio": source.FileIO = guid(value); break;
                case "generatemodelfile": source.GenerateModelFile = flag(value) ?? false; break;
                case "enabled": source.Enabled = flag(value) ?? true; break;
                case "color": source.Color = text(value); break;
                case "type": source.Type = readType(value, out impliedFormat); break;
                case "fileformat": source.FileFormat = readFormat(value); break;
                default: break; // unknown, or removed: ignored
            }
        }
        // the old file kinds carried their format in the name; an explicit format wins over that
        if (impliedFormat != null && !hasProperty(node, "FileFormat")) source.FileFormat = impliedFormat.Value;
        return source;
    }
    public override void Write(Utf8JsonWriter writer, DatamodelSource value, JsonSerializerOptions options) {
        string n(string name) => options.PropertyNamingPolicy?.ConvertName(name) ?? name;
        writer.WriteStartObject();
        writer.WriteString(n("Id"), value.Id);
        writer.WriteString(n("Name"), value.Name);
        writer.WriteString(n("Namespace"), value.Namespace);
        writer.WriteString(n("Type"), value.Type.ToString());
        writer.WriteString(n("FileFormat"), value.FileFormat.ToString());
        writer.WriteString(n("Filepath"), value.Filepath);
        writer.WriteString(n("Reference"), value.Reference);
        if (value.FileIO == null) writer.WriteNull(n("FileIO")); else writer.WriteString(n("FileIO"), value.FileIO.Value);
        writer.WriteString(n("SourceCodePath"), value.SourceCodePath);
        writer.WriteBoolean(n("GenerateModelFile"), value.GenerateModelFile);
        writer.WriteBoolean(n("Enabled"), value.Enabled);
        writer.WriteString(n("Color"), value.Color);
        writer.WriteEndObject();
    }

    static bool hasProperty(JsonObject node, string name) => node.Any(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase));
    static string? text(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value?.ToString();
    static Guid? guid(JsonNode? value) {
        var s = text(value);
        if (string.IsNullOrEmpty(s)) return null;
        return Guid.TryParse(s, out var g) ? g : throw new JsonException("\"" + s + "\" is not a guid. ");
    }
    static bool? flag(JsonNode? value) {
        if (value is not JsonValue v) return null;
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<string>(out var s) && bool.TryParse(s, out b)) return b;
        throw new JsonException("\"" + value + "\" is not a boolean. ");
    }
    static DatamodelSourceType readType(JsonNode? value, out DatamodelSourceFileFormat? impliedFormat) {
        impliedFormat = null;
        if (value is JsonValue v && v.TryGetValue<int>(out var number)) {
            switch (number) {
                case 0: return DatamodelSourceType.TypeReference;
                case 1: throw removed();
                case 2: impliedFormat = DatamodelSourceFileFormat.Json; return DatamodelSourceType.TextFiles;
                case 3: impliedFormat = DatamodelSourceFileFormat.CSharpCode; return DatamodelSourceType.TextFiles;
                case 4: return DatamodelSourceType.Code;
                default: throw new JsonException(number + " is not a datamodel source type. ");
            }
        }
        var name = text(value) ?? "";
        switch (name.ToLowerInvariant()) {
            case "typereference":
            case "assemblynamereference": return DatamodelSourceType.TypeReference;
            case "typenamereference": throw removed();
            case "textfiles": return DatamodelSourceType.TextFiles;
            case "jsonfile": impliedFormat = DatamodelSourceFileFormat.Json; return DatamodelSourceType.TextFiles;
            case "csharpcodefile": impliedFormat = DatamodelSourceFileFormat.CSharpCode; return DatamodelSourceType.TextFiles;
            case "code": return DatamodelSourceType.Code;
            default: throw new JsonException("\"" + name + "\" is not a datamodel source type. Use TypeReference, TextFiles or Code. ");
        }
    }
    static JsonException removed() => new("The datamodel source type TypeNameReference (one type by its assembly qualified name) was removed in September 2026. "
        + "Use TypeReference with the assembly in Reference and the type's namespace in Namespace instead. ");
    static DatamodelSourceFileFormat readFormat(JsonNode? value) {
        if (value is JsonValue v && v.TryGetValue<int>(out var number)) {
            return Enum.IsDefined(typeof(DatamodelSourceFileFormat), number) ? (DatamodelSourceFileFormat)number : throw new JsonException(number + " is not a datamodel source file format. ");
        }
        var name = text(value) ?? "";
        return Enum.TryParse<DatamodelSourceFileFormat>(name, ignoreCase: true, out var format) ? format
            : throw new JsonException("\"" + name + "\" is not a datamodel source file format. Use Json or CSharpCode. ");
    }
}
