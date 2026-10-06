using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Relatude.DB.IO;

namespace Relatude.DB.GraphQL.Endpoints;

/// <summary>One endpoint file as found: its key, the definition, or why it could not be read, and which folder it is in.</summary>
public sealed class GraphQLEndpointFile {
    public required string[] Key { get; init; }
    public GraphQLEndpointDefinition? Definition { get; init; }
    public string? Error { get; init; }
    public string FileName => Key[^1];
    /// <summary>The folder the file is in: SETTINGS (deployed with the application) or DATA (this installation's).</summary>
    public DefinitionSource Source { get; init; }
    /// <summary>Where the file is, for people.</summary>
    public string Display { get; init; } = "";
    /// <summary>What both folders have for the endpoint; null for a file that could not be read.</summary>
    public LayeredDefinition? Layer { get; init; }
}

/// <summary>
/// The endpoint definitions of one database, one json file per endpoint, in two folders (see
/// <see cref="LayeredDefinitionFolders"/>): SETTINGS - graphql/ in the database's folder in
/// relatude.settings, deployed and kept in source control with the application - and DATA - overrides/graphql/
/// on the database's storage, where everything saved from the admin UI goes. An endpoint in DATA replaces
/// the one in SETTINGS with the same id, and a marker there takes one away. Any json file is an endpoint,
/// so definitions can be written by hand; a file without an id gets one derived from its name, the same in
/// either folder.
/// </summary>
public sealed class GraphQLEndpointStore(LayeredDefinitionFolders folders) {
    /// <summary>A store with only a DATA folder: graphql/ on <paramref name="io"/>.</summary>
    public GraphQLEndpointStore(IIOProvider io) : this(new LayeredDefinitionFolders(null, new DefinitionFolder(io, [FileKeyUtility.GraphQLFolderName], FileKeyUtility.GraphQLFolderName))) { }

    /// <summary>The member of a definition file holding its id.</summary>
    public const string IdProperty = "id";
    public LayeredDefinitionFolders Folders => folders;

    /// <summary>The endpoints in force, and the files that could not be read.</summary>
    public List<GraphQLEndpointFile> Load() => Load(out _);
    /// <summary>The endpoints in force and the unreadable files, with what both folders have for every endpoint.</summary>
    public List<GraphQLEndpointFile> Load(out List<LayeredDefinition> layers) {
        var result = new List<GraphQLEndpointFile>();
        List<LayeredDefinition> found;
        List<DefinitionReadError> errors;
        try {
            (found, errors) = LayeredDefinitions.Read(folders, idOf, StringComparer.OrdinalIgnoreCase);
        } catch {
            layers = [];
            return result;
        }
        layers = found;
        foreach (var error in errors) result.Add(new GraphQLEndpointFile { Key = error.Key, Error = error.Message, Source = error.Source, Display = error.Display });
        foreach (var layer in found.Where(l => l.InForce).OrderBy(l => (l.DataKey ?? l.SettingsKey)!.FileName(), StringComparer.OrdinalIgnoreCase)) {
            var (folder, key, source) = layer.DataKey != null ? (folders.Data, layer.DataKey, DefinitionSource.Data) : (folders.Settings!, layer.SettingsKey!, DefinitionSource.Settings);
            try {
                var definition = GraphQLEndpointDefinition.FromJson(layer.Text!);
                if (definition.Id == Guid.Empty) definition.Id = Guid.Parse(layer.Id);
                if (string.IsNullOrWhiteSpace(definition.Name)) definition.Name = Path.GetFileNameWithoutExtension(key[^1]);
                result.Add(new GraphQLEndpointFile { Key = key, Definition = definition, Source = source, Display = folder.DisplayOf(key), Layer = layer });
            } catch (Exception ex) {
                result.Add(new GraphQLEndpointFile { Key = key, Error = ex.Message, Source = source, Display = folder.DisplayOf(key), Layer = layer });
            }
        }
        return result;
    }

    // an endpoint is known by its id; a file without one by an id derived from its name
    static string idOf(string[] key, JsonObject json) {
        foreach (var (name, value) in json) {
            if (string.Equals(name, IdProperty, StringComparison.OrdinalIgnoreCase) && value is JsonValue v && v.TryGetValue<string>(out var text)
                && Guid.TryParse(text, out var id) && id != Guid.Empty) return id.ToString();
        }
        return IdFromFileName(key[^1]).ToString();
    }

    /// <summary>
    /// Saves a definition to DATA: over what DATA has for it, else in a file named as its SETTINGS file is,
    /// else in a new file named after the endpoint. One that says what SETTINGS says is not kept twice.
    /// </summary>
    public GraphQLEndpointFile Save(GraphQLEndpointDefinition definition, LayeredDefinition? existing) {
        if (definition.Id == Guid.Empty) definition.Id = Guid.NewGuid();
        var id = definition.Id;
        // a file written by hand may leave out the id and the name, which it then gets from its file
        var name = existing?.SettingsKey != null ? Path.GetFileNameWithoutExtension(existing.SettingsKey[^1]) : definition.Name;
        var key = LayeredDefinitions.SaveData(folders, existing, definition.ToJson(), newFileName(definition), text => {
            var d = GraphQLEndpointDefinition.FromJson(text);
            if (d.Id == Guid.Empty) d.Id = id;
            if (string.IsNullOrWhiteSpace(d.Name)) d.Name = name;
            return d.ToJson();
        });
        var inForce = key ?? existing!.SettingsKey!;
        var source = key != null ? DefinitionSource.Data : DefinitionSource.Settings;
        return new GraphQLEndpointFile { Key = inForce, Definition = definition, Source = source, Display = (source == DefinitionSource.Data ? folders.Data : folders.Settings!).DisplayOf(inForce) };
    }

    /// <summary>Takes an endpoint away: a marker in DATA for one SETTINGS has, else its DATA file deleted.</summary>
    public void Delete(LayeredDefinition existing) => LayeredDefinitions.Remove(folders, existing, IdProperty);
    /// <summary>Deletes a file that could not be read, in whichever folder it is.</summary>
    public void DeleteFile(DefinitionSource source, string[] key) => (source == DefinitionSource.Settings ? folders.Settings! : folders.Data).IO.DeleteFileIfItExists(key);

    /// <summary>Moves what DATA has for an endpoint into SETTINGS; nothing that is served changes.</summary>
    public bool MoveToSettings(LayeredDefinition existing) => LayeredDefinitions.MoveToSettings(folders, existing);
    /// <summary>Drops what DATA has for an endpoint, which puts back the SETTINGS definition, or nothing.</summary>
    public bool DiscardData(LayeredDefinition existing) => LayeredDefinitions.DiscardData(folders, existing);

    string newFileName(GraphQLEndpointDefinition definition) {
        var slug = FileKeyUtility.FilterLegalCharInFileKey(definition.Name).Replace(' ', '-');
        if (slug.Length > 40) slug = slug[..40].Trim('-');
        if (slug.Length == 0 || slug == "unnamed") slug = definition.Id.ToString("N");
        var name = slug + ".json";
        // a name neither folder has: a file named as another endpoint's would be taken for it
        for (var i = 2; taken(name); i++) name = slug + "-" + i + ".json";
        return name;
    }
    bool taken(string fileName) => folders.Data.IO.Exists(folders.Data.FileKey(fileName)) || (folders.Settings != null && folders.Settings.IO.Exists(folders.Settings.FileKey(fileName)));

    /// <summary>A stable id for a hand-written file that has none: from its name, so the same file name means
    /// the same endpoint in SETTINGS and in DATA.</summary>
    public static Guid IdFromFileName(string fileName) {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(fileName.ToLowerInvariant()));
        return new Guid(hash.AsSpan(0, 16));
    }
}
