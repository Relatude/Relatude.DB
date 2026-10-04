using Relatude.DB.Common;
using Relatude.DB.IO;
using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Relatude.DB.NodeServer.Settings;

/// <summary>
/// relatude.db.overrides.json: the settings changed in the admin UI, kept apart from relatude.db.json.
///
/// <para>relatude.db.json is the application's own description of itself - it is usually written by a
/// developer and deployed with the application - while what an administrator changes on the settings
/// pages belongs to the installation. So the admin UI never writes relatude.db.json: every save
/// compares the settings in force with relatude.db.json as the server read it, and writes only the
/// difference here (see <see cref="SettingsPatch"/> for the shape). At the next start the file is merged
/// over relatude.db.json, and the RelatudeDB configuration section over both. A deployment that
/// replaces relatude.db.json keeps what was changed here, and relatude.db.json keeps its comments.</para>
///
/// <para>The file is kept with the default database, in the overrides folder of its storage, unless
/// <see cref="ServerOptions.SettingsOverridesFilePath"/> names a file; see
/// <see cref="SettingsOverridesLocation"/> for how that is found.</para>
///
/// <para>Values supplied by configuration or set by the application's code are taken out before the
/// difference is made, so neither ever reaches this file. Anything in it can be moved into
/// relatude.db.json from the settings page.</para>
/// </summary>
public sealed class SettingsOverridesFile {
    public const string FileName = "relatude.db.overrides.json";
    /// <summary>Where the file is kept when there is no default database to keep it with: relative to
    /// the root data folder, in the overrides folder of where a new installation's database folder would be.</summary>
    public static readonly string FallbackRelativePath = Path.Combine(Defaults.DataFolderPath, SettingsOverridesLocation.FolderName, FileName);
    /// <summary>The same, as older versions had it: in that folder itself.</summary>
    public static readonly string LegacyFallbackRelativePath = Path.Combine(Defaults.DataFolderPath, FileName);

    static readonly TimeSpan _writeTimeout = TimeSpan.FromSeconds(10);
    const string _header =
        "// Settings changed in the Relatude.DB admin UI. They are merged over relatude.db.json when the server\n"
        + "// starts, and the RelatudeDB configuration section is merged over both. The server rewrites this file\n"
        + "// on every save from the settings pages; use \"Move into relatude.db.json\" there to make a change permanent.\n";

    SettingsOverridesLocation _location;
    // where the file was read from when that was the place older versions kept it: taken away once
    // the file has been written where it belongs
    SettingsOverridesLocation? _readFromLegacy;
    readonly string? _unavailable;
    JsonObject _base;
    JsonObject _patch;
    List<SettingsPatch.Entry>? _entries;

    SettingsOverridesFile(SettingsOverridesLocation location, JsonObject baseJson, JsonObject patch, string? unavailable = null) {
        _location = location;
        _base = baseJson;
        _patch = patch;
        _unavailable = unavailable;
    }

    /// <summary>Where the file is.</summary>
    public SettingsOverridesLocation Location => _location;
    /// <summary>Where the file is, for people.</summary>
    public string Display => (_readFromLegacy ?? _location).Display;
    /// <summary>Why the file could not be read at start, when it could not. The server then runs without
    /// it and refuses to save, since a save would write over what the file holds.</summary>
    public string? Unavailable => _unavailable;
    /// <summary>relatude.db.json as the server read it, in the form the settings serialize to.</summary>
    public JsonObject Base => _base;
    /// <summary>What the file changes, compared with <see cref="Base"/>.</summary>
    public JsonObject Patch => _patch;
    /// <summary>Everything the file changes, one entry per setting, added element or removed element.</summary>
    public IReadOnlyList<SettingsPatch.Entry> Entries => _entries ??= SettingsPatch.Entries(_patch, _base);

    /// <summary>A file on disk at <paramref name="path"/>, for code and tests that name one.</summary>
    public static SettingsOverridesFile Open(string path, RelatudeDBServerSettings fileSettings, Action<string> info, Action<string> warn,
        out RelatudeDBServerSettings effective)
        => Open(SettingsOverridesLocation.OnDisk(path, Path.GetDirectoryName(Path.GetFullPath(path)) ?? ""), fileSettings, info, warn, out effective);

    /// <summary>
    /// Reads the file and merges it over <paramref name="fileSettings"/>. A file that is missing or empty
    /// changes nothing. Keys that match no setting, and values a setting cannot hold, are reported
    /// through <paramref name="warn"/> and skipped, as are changes to list elements relatude.db.json no
    /// longer has. A file that is not JSON at all stops the start
    /// (<see cref="SettingsOverridesFileInvalidException"/>): carrying on without it would have the next
    /// save from the admin UI write over everything it holds. Any other failure - the storage cannot be
    /// reached - is thrown as it is, for the caller to decide.
    /// </summary>
    public static SettingsOverridesFile Open(SettingsOverridesLocation location, RelatudeDBServerSettings fileSettings, Action<string> info, Action<string> warn,
        out RelatudeDBServerSettings effective) {
        var baseJson = ToJson(fileSettings);
        var read = readFile(location);
        // not where it belongs, but where an older version put it: read from there, and moved by the
        // next save (the server moves it at start already, see MoveFromLegacyPlace)
        SettingsOverridesLocation? legacy = null;
        if (read == null && location.Legacy is { } old && exists(old)) {
            read = readFile(old);
            legacy = old;
        }
        effective = fileSettings;
        if (read == null) return new SettingsOverridesFile(location, baseJson, new JsonObject());
        var patch = normalizeObject(read, typeof(RelatudeDBServerSettings), "", warn);
        var merged = (JsonObject)baseJson.DeepClone();
        SettingsPatch.Merge(merged, patch, msg => warn(FileName + ": " + msg));
        RelatudeDBServerSettings applied;
        try {
            applied = FromJson(merged);
        } catch (Exception err) {
            throw new SettingsOverridesFileInvalidException("The settings in " + location.Display + " could not be applied over "
                + Defaults.SettingsFileName + ": " + err.Message, err);
        }
        // what the file says once read: values written the way the settings write them, and nothing
        // that changes nothing, so the page and the next save see the same difference
        var normalized = SettingsPatch.Diff(baseJson, ToJson(applied));
        var file = new SettingsOverridesFile(location, baseJson, normalized) { _readFromLegacy = legacy };
        if (file.Entries.Count == 0) return file;
        effective = applied;
        info("Settings from " + location.Display + " applied: " + string.Join(", ", file.Entries.Select(e => SettingsPatch.Display(e.Path)
            + (e.Kind == SettingsPatch.EntryKind.Added ? " (added)" : e.Kind == SettingsPatch.EntryKind.Removed ? " (removed)" : ""))));
        return file;
    }

    /// <summary>The file as it is when it could not be read at start: it changes nothing, and every save
    /// and move is refused with <paramref name="reason"/>.</summary>
    public static SettingsOverridesFile ForUnavailable(SettingsOverridesLocation location, RelatudeDBServerSettings fileSettings, string reason)
        => new(location, ToJson(fileSettings), new JsonObject(), reason);

    /// <summary>
    /// Writes what <paramref name="fileLayer"/> - the settings in force, with the configuration section
    /// and the application's code taken out again - changes compared with relatude.db.json. Nothing is
    /// written when that is what the file already says; the file is deleted when it would be empty.
    /// Returns true when the file changed.
    /// </summary>
    public bool Save(JsonObject fileLayer) {
        requireAvailable();
        var next = SettingsPatch.Diff(_base, fileLayer);
        if (JsonNode.DeepEquals(next, _patch) && (next.Count > 0) == exists(_location) && _readFromLegacy == null) return false;
        write(_location, next);
        _patch = next;
        _entries = null;
        leaveLegacyPlace();
        return true;
    }

    /// <summary>
    /// Moves a file older versions kept at the storage root into the overrides folder, where
    /// <paramref name="location"/> says it belongs: written there first, then taken away from the root.
    /// The text goes across as it is, comments and all. Does nothing when there is no such file, or when
    /// there already is one in the overrides folder. Returns true when it moved the file.
    /// </summary>
    public static bool MoveFromLegacyPlace(SettingsOverridesLocation location) {
        if (location.Legacy is not { } legacy || exists(location) || !exists(legacy)) return false;
        var text = readText(legacy);
        if (string.IsNullOrWhiteSpace(text)) return false;
        writeText(location, text);
        writeText(legacy, null);
        return true;
    }
    void leaveLegacyPlace() {
        if (_readFromLegacy == null) return;
        try {
            writeText(_readFromLegacy, null);
        } catch {
            // the file is where it belongs now; the copy left behind is read no more
        }
        _readFromLegacy = null;
    }

    /// <summary>relatude.db.json as it would be with the entries <paramref name="keep"/> accepts merged
    /// into it - what moving them out of this file writes there.</summary>
    public JsonObject BaseWith(Func<SettingsPatch.Entry, bool> keep) {
        requireAvailable();
        var result = (JsonObject)_base.DeepClone();
        SettingsPatch.Merge(result, SettingsPatch.Select(_patch, _base, keep));
        return result;
    }

    /// <summary>Takes the new content of relatude.db.json as the base to compare with. The difference is
    /// made again by the next <see cref="Save"/>.</summary>
    public void Rebase(RelatudeDBServerSettings fileSettings) {
        _base = ToJson(fileSettings);
        _entries = null;
    }

    /// <summary>Puts the file where <paramref name="location"/> says: written there first and only then
    /// taken away from where it was, so a failure between the two leaves a copy rather than nothing.
    /// Returns true when the file moved.</summary>
    public bool MoveTo(SettingsOverridesLocation location) {
        requireAvailable();
        if (location.SameAs(_location)) return false;
        if (_patch.Count > 0) write(location, _patch);
        write(_location, new JsonObject()); // an empty patch deletes the file
        leaveLegacyPlace();
        _location = location;
        return true;
    }

    void requireAvailable() {
        if (_unavailable != null) throw new InvalidOperationException("Settings cannot be saved: " + _unavailable);
    }

    /// <summary>The value relatude.db.json gives a setting, or null when it gives none.</summary>
    public JsonNode? FileValue(string path) => SettingsPatch.Read(_base, path);

    /// <summary>The entry that decides this setting: one for the setting itself, or for an object or a
    /// list it is part of that the file sets whole. Elements the file adds are not counted - their
    /// fields all come from here, which says nothing about any one of them.</summary>
    public SettingsPatch.Entry? ValueEntryFor(string path)
        => Entries.FirstOrDefault(e => e.Kind == SettingsPatch.EntryKind.Value && SettingsPatch.IsAtOrBelow(path, e.Path));

    /// <summary>True when the list element at this path is one the file adds.</summary>
    public bool IsAdded(string elementPath)
        => Entries.Any(e => e.Kind == SettingsPatch.EntryKind.Added && string.Equals(e.Path, elementPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>The elements of the list at this path that the file removes.</summary>
    public IEnumerable<SettingsPatch.Entry> RemovedFrom(string listPath)
        => Entries.Where(e => e.Kind == SettingsPatch.EntryKind.Removed && isElementOf(e.Path, listPath));

    /// <summary>True when the file changes anything at or below this path.</summary>
    public bool TouchesAtOrBelow(string path) => Entries.Any(e => SettingsPatch.IsAtOrBelow(e.Path, path));

    static bool isElementOf(string elementPath, string listPath) {
        if (!elementPath.StartsWith(listPath + "[", StringComparison.OrdinalIgnoreCase)) return false;
        return elementPath.IndexOf(']', listPath.Length) == elementPath.Length - 1;
    }

    public static JsonObject ToJson(RelatudeDBServerSettings settings)
        => JsonSerializer.SerializeToNode(settings, LocalSettingsLoaderFile.JsonOptions) as JsonObject
            ?? throw new Exception("Could not serialize the server settings.");

    public static RelatudeDBServerSettings FromJson(JsonObject json)
        => json.Deserialize<RelatudeDBServerSettings>(LocalSettingsLoaderFile.JsonOptions)
            ?? throw new Exception("Could not read the server settings.");

    // ---- the file ----

    static bool exists(SettingsOverridesLocation location)
        => location.DiskPath != null ? File.Exists(location.DiskPath) : location.Io!.Exists(location.Key);

    static string? readText(SettingsOverridesLocation location) {
        if (location.DiskPath != null) {
            var path = location.DiskPath;
            return File.Exists(path) ? FileOpenRetry.Open(path, () => File.ReadAllText(path)) : null;
        }
        var io = location.Io!;
        return io.ExistsAndIsNotEmpty(location.Key) ? io.ReadAllTextUTF8(location.Key) : null;
    }

    static JsonObject? readFile(SettingsOverridesLocation location) {
        var text = readText(location);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try {
            var node = JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return node as JsonObject ?? throw new JsonException("The file does not hold a JSON object.");
        } catch (JsonException err) {
            throw new SettingsOverridesFileInvalidException("The settings overrides file " + location.Display + " is not valid JSON: " + err.Message
                + " Correct it, or move it away to start with " + Defaults.SettingsFileName + " alone.", err);
        }
    }

    // an empty patch deletes the file
    static void write(SettingsOverridesLocation location, JsonObject patch)
        => writeText(location, patch.Count == 0 ? null : _header + patch.ToJsonString(LocalSettingsLoaderFile.JsonOptions) + Environment.NewLine);

    // null deletes the file
    static void writeText(SettingsOverridesLocation location, string? text) {
        if (location.DiskPath != null) {
            var path = location.DiskPath;
            if (text == null) {
                if (File.Exists(path)) FileOpenRetry.Open(path, () => File.Delete(path), _writeTimeout);
                return;
            }
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            var temp = path + ".tmp";
            File.WriteAllText(temp, text);
            // written whole and then put in place, so a crash mid-write cannot leave half a file - which
            // would stop the next start, see readFile
            FileOpenRetry.Replace(temp, path, _writeTimeout);
            return;
        }
        // blob storage and memory: a provider can only delete and write again
        var io = location.Io!;
        if (text == null) io.DeleteFileIfItExists(location.Key);
        else io.WriteAllTextUTF8(location.Key, text);
    }

    // ---- reading what was written by hand ----

    // Keys are matched to the settings case insensitively and written back under the property's own
    // name, so a patch merges onto the serialized settings key for key. A value is checked against the
    // setting it is for but kept as written; the merge and the round trip through the settings type
    // turn it into the form the server writes.
    static JsonObject normalizeObject(JsonObject node, Type type, string path, Action<string> warn) {
        var result = new JsonObject();
        foreach (var (key, value) in node) {
            if (key == SettingsPatch.ReplaceMarker) {
                result[key] = value?.DeepClone();
                continue;
            }
            var childPath = path.Length == 0 ? key : path + "." + key;
            var property = type.GetProperty(key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property == null || property.GetIndexParameters().Length > 0) {
                warn(FileName + ": \"" + childPath + "\" does not match any setting - ignored.");
                continue;
            }
            if (property.SetMethod?.IsPublic != true) {
                warn(FileName + ": \"" + childPath + "\" is a read-only setting - ignored.");
                continue;
            }
            if (normalizeValue(value, property.PropertyType, childPath, warn, out var normalized)) result[property.Name] = normalized;
        }
        return result;
    }

    static bool normalizeValue(JsonNode? value, Type type, string path, Action<string> warn, out JsonNode? result) {
        var valueType = Nullable.GetUnderlyingType(type) ?? type;
        if (value is JsonObject obj && isSettingsObject(valueType)) {
            result = normalizeObject(obj, valueType, path, warn);
            return true;
        }
        if (value is JsonArray array && valueType.IsArray && isSettingsObject(valueType.GetElementType()!)) {
            result = normalizeArray(array, valueType.GetElementType()!, path, warn);
            return true;
        }
        try {
            JsonSerializer.Deserialize(SettingsPatch.strip(value), type, LocalSettingsLoaderFile.JsonOptions);
            result = value?.DeepClone();
            return true;
        } catch (Exception) {
            warn(FileName + ": \"" + path + "\" does not hold a valid " + valueType.Name + " - ignored.");
            result = null;
            return false;
        }
    }

    static JsonArray normalizeArray(JsonArray array, Type elementType, string path, Action<string> warn) {
        var result = new JsonArray();
        for (var i = 0; i < array.Count; i++) {
            if (array[i] is not JsonObject element) {
                warn(FileName + ": \"" + path + "[" + i + "]\" is not an object - ignored.");
                continue;
            }
            var fields = new JsonObject();
            var markers = new List<(string, JsonNode?)>();
            foreach (var (key, value) in element) {
                if (key is SettingsPatch.AddedMarker or SettingsPatch.RemovedMarker) markers.Add((key, value?.DeepClone()));
                else fields[key] = value?.DeepClone();
            }
            var normalized = normalizeObject(fields, elementType, path + "[" + i + "]", warn);
            foreach (var (key, value) in markers) normalized[key] = value;
            result.Add(normalized);
        }
        return result;
    }

    static bool isSettingsObject(Type type)
        => type.IsClass && type != typeof(string) && !type.IsArray && !typeof(IEnumerable).IsAssignableFrom(type);
}

/// <summary>relatude.db.overrides.json is there but cannot be read as settings. The server does not start
/// on it: running without the file would have the next save from the admin UI write over it.</summary>
public sealed class SettingsOverridesFileInvalidException(string message, Exception? inner = null) : Exception(message, inner);
