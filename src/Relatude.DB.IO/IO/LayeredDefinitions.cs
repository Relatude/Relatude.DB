using System.Text.Json;
using System.Text.Json.Nodes;

namespace Relatude.DB.IO;

/// <summary>One folder of JSON definition files on a storage provider: one definition per file.</summary>
public sealed class DefinitionFolder(IIOProvider io, string[] folder, string display) {
    /// <summary>The provider the folder is in.</summary>
    public IIOProvider IO { get; } = io;
    /// <summary>The folder below the provider's root, empty for the root itself.</summary>
    public string[] Folder { get; } = folder;
    /// <summary>Where the folder is, for people: relatude.settings/logs, say.</summary>
    public string Display { get; } = display;
    public string[] FileKey(string fileName) => [.. Folder, fileName];
    /// <summary>A file in this folder, for people.</summary>
    public string DisplayOf(string[] fileKey) {
        var name = fileKey[Folder.Length..].AsKeyString();
        return Display.Length == 0 ? name : Display + "/" + name;
    }
    /// <summary>Every json file in the folder, by name.</summary>
    public string[][] FileKeys() => [.. IO.Search([.. Folder, "*.json"]).OrderBy(k => k.AsKeyString(), StringComparer.OrdinalIgnoreCase)];
}

/// <summary>
/// The two folders one kind of definition is kept in. <see cref="Settings"/> is part of the application:
/// a folder in relatude.settings, kept in source control and deployed to every installation.
/// <see cref="Data"/> is this installation's, with the database: what the admin UI saves goes there. A
/// definition in <see cref="Data"/> replaces the one in <see cref="Settings"/> with the same id, and a
/// marker there takes one away (see <see cref="LayeredDefinitions"/>). Without a settings folder, the data
/// folder is all there is.
/// </summary>
public sealed class LayeredDefinitionFolders(DefinitionFolder? settings, DefinitionFolder data) {
    public DefinitionFolder? Settings { get; } = settings;
    public DefinitionFolder Data { get; } = data;
}

/// <summary>Which folder a definition in force comes from.</summary>
public enum DefinitionSource { Settings, Data }

/// <summary>What a definition in the data folder does to the settings folder's.</summary>
public enum DataChange {
    /// <summary>A definition the settings folder does not have.</summary>
    Added,
    /// <summary>Replaces the settings folder's definition with the same id.</summary>
    Changed,
    /// <summary>Takes the settings folder's definition away on this installation.</summary>
    Removed,
    /// <summary>Says what the settings folder's says: it comes from there anyway.</summary>
    SameAsSettings,
}

/// <summary>One definition as the two folders have it.</summary>
public sealed class LayeredDefinition {
    public required string Id { get; init; }
    public string[]? SettingsKey { get; init; }
    public string? SettingsText { get; init; }
    public string[]? DataKey { get; init; }
    /// <summary>The data folder's text; null with a <see cref="DataKey"/> for a marker taking the definition away.</summary>
    public string? DataText { get; init; }
    public bool DataRemoves => DataKey != null && DataText == null;
    /// <summary>Whether the definition is in force: in the data folder, or in the settings folder and not taken away.</summary>
    public bool InForce => DataKey != null ? !DataRemoves : SettingsKey != null;
    /// <summary>The text in force, or null when the definition is taken away.</summary>
    public string? Text => DataKey != null ? DataText : SettingsText;
    /// <summary>Where the definition in force comes from, or null when it is taken away.</summary>
    public DefinitionSource? Source => !InForce ? null : DataKey != null ? DefinitionSource.Data : DefinitionSource.Settings;
    /// <summary>What the data folder does, or null when it has nothing for this definition.</summary>
    public DataChange? Change => DataKey == null ? null
        : DataRemoves ? DataChange.Removed
        : SettingsKey == null ? DataChange.Added
        : LayeredDefinitions.SameJson(SettingsText, DataText) ? DataChange.SameAsSettings
        : DataChange.Changed;
}

/// <summary>A definition file that could not be read: bad JSON, no id, or an id another file has.</summary>
public sealed record DefinitionReadError(DefinitionSource Source, string[] Key, string Display, string Message);

/// <summary>
/// Reading and writing definitions kept in a <see cref="LayeredDefinitionFolders"/>, a whole definition at
/// a time: what the admin UI saves goes to the data folder, and is moved into the settings folder - or
/// discarded - from there. A data folder file holding <c>{ "$removed": true }</c> with the definition's id
/// takes the settings folder's definition away on this installation. Saving a definition that says what
/// the settings folder's says removes the data folder's copy, so nothing is kept twice.
/// </summary>
public static class LayeredDefinitions {
    /// <summary>The member of a data folder file that marks a definition taken away.</summary>
    public const string RemovedMarker = "$removed";
    static readonly JsonDocumentOptions readOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    static readonly JsonSerializerOptions writeOptions = new() { WriteIndented = true };

    /// <summary>
    /// Reads both folders. <paramref name="idOf"/> gives the id of a definition from its file and its JSON
    /// (a marker's JSON too), and throws when it has none; ids are compared with <paramref name="ids"/>.
    /// </summary>
    public static (List<LayeredDefinition> Definitions, List<DefinitionReadError> Errors) Read(LayeredDefinitionFolders folders,
        Func<string[], JsonObject, string> idOf, StringComparer ids) {
        var errors = new List<DefinitionReadError>();
        var settings = folders.Settings == null ? new Dictionary<string, (string[] Key, string Text)>(ids) : readFolder(folders.Settings, DefinitionSource.Settings, idOf, ids, errors, out _);
        var data = readFolder(folders.Data, DefinitionSource.Data, idOf, ids, errors, out var markers);
        var result = new List<LayeredDefinition>();
        foreach (var (id, s) in settings) {
            data.TryGetValue(id, out var d);
            result.Add(new LayeredDefinition {
                Id = id, SettingsKey = s.Key, SettingsText = s.Text,
                DataKey = d.Key, DataText = d.Key == null || markers.Contains(id) ? null : d.Text,
            });
        }
        foreach (var (id, d) in data) {
            if (settings.ContainsKey(id)) continue;
            // a marker taking away what the settings folder no longer has takes nothing away: listed, so it can be cleared
            result.Add(new LayeredDefinition { Id = id, DataKey = d.Key, DataText = markers.Contains(id) ? null : d.Text });
        }
        return (result, errors);
    }
    static Dictionary<string, (string[] Key, string Text)> readFolder(DefinitionFolder folder, DefinitionSource source, Func<string[], JsonObject, string> idOf,
        StringComparer ids, List<DefinitionReadError> errors, out HashSet<string> markers) {
        var found = new Dictionary<string, (string[] Key, string Text)>(ids);
        markers = new HashSet<string>(ids);
        foreach (var key in folder.FileKeys()) {
            try {
                var text = folder.IO.ReadAllTextUTF8(key);
                if (string.IsNullOrWhiteSpace(text)) throw new Exception("The file is empty.");
                var json = JsonNode.Parse(text, null, readOptions) as JsonObject ?? throw new Exception("The file does not hold a JSON object.");
                var id = idOf(key, json);
                if (found.ContainsKey(id)) throw new Exception("Another file in " + folder.Display + " has the same id, " + id + ".");
                found[id] = (key, text);
                if (IsMarker(json)) {
                    if (source == DefinitionSource.Settings) throw new Exception("Only a file of this installation can take a definition away.");
                    markers.Add(id);
                }
            } catch (Exception error) {
                errors.Add(new DefinitionReadError(source, key, folder.DisplayOf(key), error.Message));
            }
        }
        return found;
    }

    public static bool IsMarker(JsonObject json) => json[RemovedMarker] is JsonValue v && v.TryGetValue<bool>(out var removed) && removed;
    /// <summary>The text of a marker taking the definition with this id away.</summary>
    public static string MarkerText(string idProperty, string id) => new JsonObject { [idProperty] = id, [RemovedMarker] = true }.ToJsonString(writeOptions);

    /// <summary>Whether two texts hold the same JSON, layout and comments aside.</summary>
    public static bool SameJson(string? a, string? b) {
        if (a == null || b == null) return a == b;
        try {
            return JsonNode.DeepEquals(JsonNode.Parse(a, null, readOptions), JsonNode.Parse(b, null, readOptions));
        } catch (JsonException) {
            return a == b;
        }
    }

    /// <summary>
    /// Saves a definition to the data folder: over the data folder's file when there is one, else in a file
    /// named as the settings folder's is, else as <paramref name="fileName"/> says. A definition that says
    /// what the settings folder's says is not kept twice: the data folder's file goes instead.
    /// <paramref name="canonical"/> writes a definition's text the way a save would, so a settings file
    /// written by hand - fewer members, other formatting - is seen to say the same. Returns the key
    /// written, or null when the data folder's file was removed.
    /// </summary>
    public static string[]? SaveData(LayeredDefinitionFolders folders, LayeredDefinition? current, string text, string fileName, Func<string, string>? canonical = null) {
        if (current?.SettingsKey != null && sameAsSettings(current.SettingsText!, text, canonical)) {
            if (current.DataKey != null) folders.Data.IO.DeleteFileIfItExists(current.DataKey);
            return null;
        }
        var key = current?.DataKey ?? (current?.SettingsKey != null ? folders.Data.FileKey(current.SettingsKey.FileName()) : folders.Data.FileKey(fileName));
        folders.Data.IO.WriteAllTextUTF8(key, text);
        return key;
    }

    static bool sameAsSettings(string settingsText, string text, Func<string, string>? canonical) {
        if (SameJson(settingsText, text)) return true;
        if (canonical == null) return false;
        try {
            return SameJson(canonical(settingsText), canonical(text));
        } catch {
            return false; // a settings file that does not read says nothing the same
        }
    }

    /// <summary>
    /// Takes a definition away on this installation: a marker in the data folder when the settings folder
    /// has it, else the data folder's file deleted.
    /// </summary>
    public static void Remove(LayeredDefinitionFolders folders, LayeredDefinition current, string idProperty) {
        if (current.SettingsKey == null) {
            if (current.DataKey != null) folders.Data.IO.DeleteFileIfItExists(current.DataKey);
            return;
        }
        var key = current.DataKey ?? folders.Data.FileKey(current.SettingsKey.FileName());
        folders.Data.IO.WriteAllTextUTF8(key, MarkerText(idProperty, current.Id));
    }

    /// <summary>
    /// Moves what the data folder has for a definition into the settings folder: its text written there
    /// (in the settings folder's file, else one named as the data folder's is), or for a marker the
    /// settings folder's file deleted. The data folder's file goes once that is done, so nothing in force
    /// changes. Returns false when the data folder has nothing for it.
    /// </summary>
    public static bool MoveToSettings(LayeredDefinitionFolders folders, LayeredDefinition current) {
        if (current.DataKey == null) return false;
        var settings = folders.Settings ?? throw new InvalidOperationException("There is no settings folder to move the definition into.");
        if (current.DataRemoves) {
            if (current.SettingsKey != null) settings.IO.DeleteFileIfItExists(current.SettingsKey);
        } else {
            settings.IO.WriteAllTextUTF8(current.SettingsKey ?? settings.FileKey(current.DataKey.FileName()), current.DataText!);
        }
        folders.Data.IO.DeleteFileIfItExists(current.DataKey);
        return true;
    }

    /// <summary>Deletes what the data folder has for a definition, which puts back the settings folder's (or nothing).</summary>
    public static bool DiscardData(LayeredDefinitionFolders folders, LayeredDefinition current) {
        if (current.DataKey == null) return false;
        folders.Data.IO.DeleteFileIfItExists(current.DataKey);
        return true;
    }
}
