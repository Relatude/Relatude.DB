using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Relatude.DB.IO;

/// <summary>
/// The two folders a library of text files is kept in, any kind of file in nested folders: <see cref="Settings"/>, part
/// of the application, kept in source control and deployed to every installation, and <see cref="Data"/>, this
/// installation's, where what is edited on it is saved. A file in <see cref="Data"/> replaces the file with the same
/// path in <see cref="Settings"/>, and the data folder's <see cref="LayeredFiles.StateFileName"/> can take one away (see
/// <see cref="LayeredFiles"/>). Without a settings folder, the data folder is all there is.
/// </summary>
public sealed class LayeredFileFolders(DefinitionFolder? settings, DefinitionFolder data) {
    public DefinitionFolder? Settings { get; } = settings;
    public DefinitionFolder Data { get; } = data;
}

/// <summary>One file of a <see cref="LayeredFileFolders"/>, as the two folders have it.</summary>
public sealed class LayeredFile {
    /// <summary>Its path below the folders, '/'-separated: pages/article.sbn-html. Compared without case.</summary>
    public required string Path { get; init; }
    public string[]? SettingsKey { get; init; }
    public string[]? DataKey { get; init; }
    /// <summary>The settings folder's text; null when it has none, or when the file was listed without its texts.</summary>
    public string? SettingsText { get; init; }
    /// <summary>The data folder's text; null when it has none, or when the file was listed without its texts.</summary>
    public string? DataText { get; init; }
    public DateTime? SettingsChangedUtc { get; init; }
    public DateTime? DataChangedUtc { get; init; }
    /// <summary>The data folder takes the settings folder's file away on this installation.</summary>
    public bool Removed { get; init; }
    /// <summary>The hash (<see cref="LayeredFiles.Hash"/>) of the settings folder's text the data folder's file was saved over.</summary>
    public string? BasedOn { get; init; }
    /// <summary>
    /// The data folder's file was saved over another text than the settings folder has now: a deployment changed the
    /// settings folder's file since, and the change is hidden by this installation's.
    /// </summary>
    public bool Stale { get; init; }
    /// <summary>Whether the file is in force: in the data folder, or in the settings folder and not taken away.</summary>
    public bool InForce => DataKey != null || (SettingsKey != null && !Removed);
    /// <summary>Where the file in force comes from, or null when it is taken away.</summary>
    public DefinitionSource? Source => DataKey != null ? DefinitionSource.Data : SettingsKey != null && !Removed ? DefinitionSource.Settings : null;
    /// <summary>The text in force, when it was read.</summary>
    public string? Text => DataKey != null ? DataText : Removed ? null : SettingsText;
}

/// <summary>
/// Reading and writing a library of text files kept in a <see cref="LayeredFileFolders"/>, a whole file at a time: what
/// is edited on an installation goes to the data folder, and is moved into the settings folder, or discarded, from
/// there. The data folder's <see cref="StateFileName"/> holds what plain text files cannot say themselves: the settings
/// folder's files taken away on this installation, and for each of its own files the hash of the settings folder's
/// text it was saved over, so a deployment that changes that text is seen (<see cref="LayeredFile.Stale"/>). Saving a
/// text that says what the settings folder's says removes the data folder's copy, so nothing is kept twice. Files and
/// folders whose names start with '.' are not part of the library.
/// </summary>
public static partial class LayeredFiles {
    /// <summary>The file in the data folder holding the removals and the texts each file was saved over.</summary>
    public const string StateFileName = ".layers.json";
    /// <summary>The longest path a file may have.</summary>
    public const int MaxPathLength = 200;
    /// <summary>The deepest a file may be, in folders.</summary>
    public const int MaxDepth = 8;

    static readonly JsonSerializerOptions writeOptions = new() { WriteIndented = true };
    static readonly JsonDocumentOptions readOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    // the state file is read, changed and written as one step per data folder
    static readonly ConcurrentDictionary<(IIOProvider, string), object> locks = new();

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_.-]*$")]
    private static partial Regex SegmentPattern();

    /// <summary>A path as the library writes it: '/'-separated, without a leading or trailing '/'.</summary>
    public static string Normalize(string path) => string.Join('/', path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>
    /// Why a path cannot name a file of the library, or null when it can: folders and a file name of letters, digits,
    /// '_', '-' and '.', none starting with '.' or '-', at most <see cref="MaxDepth"/> folders deep and
    /// <see cref="MaxPathLength"/> characters long.
    /// </summary>
    public static string? PathProblem(string? path) {
        if (string.IsNullOrWhiteSpace(path)) return "A file needs a path, such as pages/article.sbn-html.";
        var normal = Normalize(path);
        if (normal.Length == 0) return "A file needs a path, such as pages/article.sbn-html.";
        if (normal.Length > MaxPathLength) return "A path is at most " + MaxPathLength + " characters long.";
        var segments = normal.Split('/');
        if (segments.Length > MaxDepth + 1) return "A file is at most " + MaxDepth + " folders deep.";
        foreach (var s in segments) {
            if (!SegmentPattern().IsMatch(s)) return "\"" + s + "\" cannot be part of a path: folders and file names are letters, digits, '_', '-' and '.', starting with a letter, a digit or '_'.";
        }
        if (!segments[^1].Contains('.')) return "A file's name needs an extension, which says what kind of file it is: " + normal + ".sbn-html, say.";
        return null;
    }

    static string checkedPath(string path) => PathProblem(path) is { } problem ? throw new ArgumentException(problem, nameof(path)) : Normalize(path);

    /// <summary>The hash of a text as the state file keeps it, its line endings aside, so a checkout that changes them changes nothing.</summary>
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sameLineEndings(text))))[..32];

    /// <summary>Whether two texts say the same, their line endings aside.</summary>
    public static bool SameText(string? a, string? b) => a == null || b == null ? a == b : sameLineEndings(a) == sameLineEndings(b);

    static string sameLineEndings(string text) => text.Replace("\r\n", "\n");

    // ------------------------------------------------------------------ reading

    /// <summary>
    /// Every file of the library, in force or taken away, by path, without their texts unless <paramref name="texts"/>;
    /// <see cref="LayeredFile.Stale"/> is found either way, reading the settings folder's text of the files the data
    /// folder replaces.
    /// </summary>
    public static async Task<List<LayeredFile>> ListAsync(LayeredFileFolders folders, bool texts = false) {
        var settings = folders.Settings == null ? [] : await filesOf(folders.Settings);
        var data = await filesOf(folders.Data);
        var state = readState(folders.Data);
        var paths = new SortedSet<string>(settings.Keys.Concat(data.Keys).Concat(state.Removed), StringComparer.OrdinalIgnoreCase);
        var result = new List<LayeredFile>(paths.Count);
        foreach (var path in paths) {
            settings.TryGetValue(path, out var s);
            data.TryGetValue(path, out var d);
            if (s.Key == null && d.Key == null) continue; // a removal of what the settings folder no longer has
            var settingsText = s.Key != null && (texts || d.Key != null) ? readText(folders.Settings!, s.Key) : null;
            var basedOn = state.BasedOn.GetValueOrDefault(path);
            result.Add(new LayeredFile {
                Path = s.Key != null ? s.Path : d.Path,
                SettingsKey = s.Key, DataKey = d.Key,
                SettingsText = s.Key != null && texts ? settingsText : null,
                DataText = d.Key != null && texts ? readText(folders.Data, d.Key) : null,
                SettingsChangedUtc = s.Changed, DataChangedUtc = d.Changed,
                Removed = d.Key == null && state.Removed.Contains(path),
                BasedOn = d.Key != null ? basedOn : null,
                Stale = d.Key != null && settingsText != null && basedOn != null && basedOn != Hash(settingsText),
            });
        }
        return result;
    }

    /// <summary>One file with its texts, or null when neither folder has it.</summary>
    public static async Task<LayeredFile?> ReadAsync(LayeredFileFolders folders, string path) {
        path = checkedPath(path);
        var s = folders.Settings == null ? null : await findAsync(folders.Settings, path);
        var d = await findAsync(folders.Data, path);
        if (s == null && d == null) return null;
        var state = readState(folders.Data);
        var settingsText = s == null ? null : readText(folders.Settings!, s.Value.Key);
        var basedOn = d == null ? null : state.BasedOn.GetValueOrDefault(path);
        return new LayeredFile {
            Path = s?.Path ?? d!.Value.Path,
            SettingsKey = s?.Key, DataKey = d?.Key,
            SettingsText = settingsText,
            DataText = d == null ? null : readText(folders.Data, d.Value.Key),
            SettingsChangedUtc = s?.Changed, DataChangedUtc = d?.Changed,
            Removed = d == null && state.Removed.Contains(path),
            BasedOn = basedOn,
            Stale = d != null && settingsText != null && basedOn != null && basedOn != Hash(settingsText),
        };
    }

    static string readText(DefinitionFolder folder, string[] key) => folder.IO.Exists(key) ? folder.IO.ReadAllTextUTF8(key) : "";

    // the files below a folder by path, leaving out what starts with '.'
    static async Task<Dictionary<string, (string Path, string[] Key, DateTime Changed)>> filesOf(DefinitionFolder folder) {
        var found = new Dictionary<string, (string, string[], DateTime)>(StringComparer.OrdinalIgnoreCase);
        FolderMeta meta;
        try {
            meta = await folder.IO.GetFolderAsync(folder.Folder, recursive: true, withFiles: true);
        } catch (DirectoryNotFoundException) {
            return found;
        }
        void walk(FolderMeta f) {
            foreach (var file in f.Files) {
                var key = file.Key.SplitKey();
                if (key.Length <= folder.Folder.Length) continue;
                var below = key[folder.Folder.Length..];
                if (below.Any(s => s.StartsWith('.'))) continue;
                var path = string.Join('/', below);
                if (PathProblem(path) != null) continue; // not a name the library gives: left alone
                found[path] = (path, key, file.LastModifiedUtc);
            }
            foreach (var sub in f.SubFolders) {
                if (!sub.Name.StartsWith('.')) walk(sub);
            }
        }
        walk(meta);
        return found;
    }

    // a file by path, its case aside, named as stored: looked for in its folder, then in the whole library, as a
    // provider whose keys tell case apart (blob storage, Linux) may have its folders in another case
    static async Task<(string Path, string[] Key, DateTime? Changed)?> findAsync(DefinitionFolder folder, string path) {
        var segments = path.Split('/');
        string[] parent = [.. folder.Folder, .. segments[..^1]];
        try {
            var meta = await folder.IO.GetFolderAsync(parent, recursive: false, withFiles: true);
            foreach (var file in meta.Files) {
                var key = file.Key.SplitKey();
                if (key.Length == parent.Length + 1 && key[^1].Equals(segments[^1], StringComparison.OrdinalIgnoreCase)) {
                    return (string.Join('/', [.. key[folder.Folder.Length..]]), key, file.LastModifiedUtc);
                }
            }
        } catch (DirectoryNotFoundException) {
        }
        var all = await filesOf(folder);
        return all.TryGetValue(path, out var f) ? (f.Path, f.Key, f.Changed) : null;
    }

    // ------------------------------------------------------------------ writing

    /// <summary>
    /// Saves a text to the data folder: over the data folder's file when there is one, else at the path. A text that
    /// says what the settings folder's says is not kept twice: the data folder's file goes instead. Returns the key
    /// written, or null when the data folder's file was removed.
    /// </summary>
    public static string[]? SaveData(LayeredFileFolders folders, LayeredFile? current, string path, string text) {
        path = checkedPath(current?.Path ?? path);
        lock (lockOf(folders.Data)) {
            var state = readState(folders.Data);
            state.Removed.Remove(path);
            if (current?.SettingsKey != null && current.SettingsText != null && SameText(current.SettingsText, text)) {
                if (current.DataKey != null) folders.Data.IO.DeleteFileIfItExists(current.DataKey);
                state.BasedOn.Remove(path);
                writeState(folders.Data, state);
                return null;
            }
            var key = current?.DataKey ?? keyOf(folders.Data, path);
            folders.Data.IO.WriteAllTextUTF8(key, text);
            if (current?.SettingsText != null) state.BasedOn[path] = Hash(current.SettingsText);
            else state.BasedOn.Remove(path);
            writeState(folders.Data, state);
            return key;
        }
    }

    /// <summary>
    /// Saves a text to the settings folder, where the application keeps it in source control, and lets the data folder's
    /// file go, which would hide it on this installation. Returns the key written.
    /// </summary>
    public static string[] SaveSettings(LayeredFileFolders folders, LayeredFile? current, string path, string text) {
        var settings = folders.Settings ?? throw new InvalidOperationException("There is no settings folder to save the file in.");
        path = checkedPath(current?.Path ?? path);
        var key = current?.SettingsKey ?? keyOf(settings, path);
        settings.IO.WriteAllTextUTF8(key, text);
        lock (lockOf(folders.Data)) {
            if (current?.DataKey != null) folders.Data.IO.DeleteFileIfItExists(current.DataKey);
            var state = readState(folders.Data);
            if (state.Removed.Remove(path) | state.BasedOn.Remove(path)) writeState(folders.Data, state);
        }
        return key;
    }

    /// <summary>Takes a file away on this installation: the data folder's file deleted, and the settings folder's, if any, marked removed.</summary>
    public static void Remove(LayeredFileFolders folders, LayeredFile current) {
        lock (lockOf(folders.Data)) {
            if (current.DataKey != null) folders.Data.IO.DeleteFileIfItExists(current.DataKey);
            var state = readState(folders.Data);
            state.BasedOn.Remove(current.Path);
            if (current.SettingsKey != null) state.Removed.Add(current.Path);
            else state.Removed.Remove(current.Path);
            writeState(folders.Data, state);
        }
    }

    /// <summary>
    /// Moves what the data folder has for a file into the settings folder: its text written there, or for a removal the
    /// settings folder's file deleted. Nothing in force changes. Returns false when the data folder has nothing for it.
    /// </summary>
    public static bool MoveToSettings(LayeredFileFolders folders, LayeredFile current) {
        if (current.DataKey == null && !current.Removed) return false;
        var settings = folders.Settings ?? throw new InvalidOperationException("There is no settings folder to move the file into.");
        if (current.Removed) {
            if (current.SettingsKey != null) settings.IO.DeleteFileIfItExists(current.SettingsKey);
        } else {
            var text = current.DataText ?? folders.Data.IO.ReadAllTextUTF8(current.DataKey!);
            settings.IO.WriteAllTextUTF8(current.SettingsKey ?? keyOf(settings, current.Path), text);
        }
        return DiscardData(folders, current);
    }

    /// <summary>Deletes what the data folder has for a file, its removal too, which puts back the settings folder's (or nothing).</summary>
    public static bool DiscardData(LayeredFileFolders folders, LayeredFile current) {
        if (current.DataKey == null && !current.Removed) return false;
        lock (lockOf(folders.Data)) {
            if (current.DataKey != null) folders.Data.IO.DeleteFileIfItExists(current.DataKey);
            var state = readState(folders.Data);
            if (state.Removed.Remove(current.Path) | state.BasedOn.Remove(current.Path)) writeState(folders.Data, state);
        }
        return true;
    }

    static string[] keyOf(DefinitionFolder folder, string path) => [.. folder.Folder, .. path.Split('/')];

    static object lockOf(DefinitionFolder folder) => locks.GetOrAdd((folder.IO, folder.Folder.AsKeyString()), _ => new object());

    // ------------------------------------------------------------------ the state file

    sealed class State {
        public HashSet<string> Removed { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> BasedOn { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    static State readState(DefinitionFolder data) {
        var state = new State();
        string[] key = [.. data.Folder, StateFileName];
        if (!data.IO.Exists(key)) return state;
        try {
            if (JsonNode.Parse(data.IO.ReadAllTextUTF8(key), null, readOptions) is not JsonObject json) return state;
            if (json["removed"] is JsonArray removed) {
                foreach (var r in removed) {
                    if (r?.GetValue<string>() is { } p && PathProblem(p) == null) state.Removed.Add(Normalize(p));
                }
            }
            if (json["basedOn"] is JsonObject basedOn) {
                foreach (var (p, h) in basedOn) {
                    if (PathProblem(p) == null && h is JsonValue v && v.TryGetValue<string>(out var hash)) state.BasedOn[Normalize(p)] = hash;
                }
            }
        } catch (JsonException) {
            // a state file that does not read takes nothing away and marks nothing stale
        }
        return state;
    }

    static void writeState(DefinitionFolder data, State state) {
        string[] key = [.. data.Folder, StateFileName];
        if (state.Removed.Count == 0 && state.BasedOn.Count == 0) {
            data.IO.DeleteFileIfItExists(key);
            return;
        }
        var json = new JsonObject {
            ["removed"] = new JsonArray([.. state.Removed.Order(StringComparer.OrdinalIgnoreCase).Select(p => (JsonNode)JsonValue.Create(p))]),
            ["basedOn"] = new JsonObject(state.BasedOn.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => KeyValuePair.Create(p.Key, (JsonNode?)JsonValue.Create(p.Value)))),
        };
        data.IO.WriteAllTextUTF8(key, json.ToJsonString(writeOptions));
    }
}
