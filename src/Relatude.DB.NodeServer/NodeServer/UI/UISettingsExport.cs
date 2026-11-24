using Microsoft.AspNetCore.Http;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.GraphQL.Endpoints;
using Relatude.DB.IO;
using Relatude.DB.Logging;
using Relatude.DB.NodeServer.Settings;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// The overrides download: everything this installation changed in the admin UI - settings, custom
/// settings, datamodel overrides, log and GraphQL definitions, runtime types - listed in one shape, and a
/// zip of the application's own files as moving the entries chosen into them would write them, each at
/// its path relative to the application's folder, with relatude-changes.md saying what is in it. Nothing
/// is written on the server: the zip is for an installation whose files the next deployment replaces, to
/// be put into the project by hand. What the configuration section decides is never exported.
///
/// The same list is the admin UI's one dialog of what THIS SERVER holds: besides the download, each entry
/// says whether it can be moved into SHARED or discarded where it is (MoveBlocked, DiscardBlocked: null when
/// it can), and each group where it is kept and where a move writes it, so the dialog asks the command of
/// the entry's kind - settings-overrides-move, datamodel-overrides-move, custom-logs-data-move... - itself.
/// </summary>
sealed partial class UISettings {
    // for people: ø and • as they are, not escaped
    static readonly JsonSerializerOptions _indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    void registerExport(UICommands commands) {
        commands.Register("overrides-export-list", ctx => exportList());
    }

    /// <summary>One entry of the list. Before and after are what the application's file has and would have,
    /// as text, secrets blanked out; Blocked says why an entry cannot be exported, MoveBlocked why it cannot be
    /// moved into SHARED and DiscardBlocked why it cannot be discarded, each null when it can. Section is what
    /// the dialog's tree lists it under within its group: a settings section, a type, a source.</summary>
    sealed record ExportEntry(string Id, string Label, string? Where, string? Section, string File, string Change, string? Before, string? After, bool Secret,
        string? Blocked, string? MoveBlocked, string? DiscardBlocked);
    /// <summary>Entries of one kind in one place. Area is the kind as the tree names it ("Settings", a custom set's
    /// title), Database the database's name (null for the server's own), Key a custom set's file key; From is
    /// where THIS SERVER keeps them and Into where a move writes them, for the dialog's questions.</summary>
    sealed record ExportGroup(string Kind, string Title, string Area, Guid? StoreId, string? Database, string? Key, string? From, string? Into, ExportEntry[] Entries);
    /// <summary>Development: relatude.settings is most likely the one in source control, so what is moved into
    /// it is kept; elsewhere it is the deployed copy the next deployment replaces.</summary>
    sealed record ExportList(string Server, bool Development, string Environment, string? ConfigSection, List<ExportGroup> Groups, List<string> Problems);

    ExportList exportList() {
        var groups = new List<ExportGroup>();
        var problems = new List<string>();
        void kind(string what, Action list) {
            try {
                list();
            } catch (Exception error) {
                problems.Add(what + ": " + error.Message);
            }
        }
        kind("Settings", () => groups.AddRange(exportSettings()));
        foreach (var definition in _server.CustomSettings.Definitions) {
            var stores = definition.Scope == CustomSettingsScope.Server ? [(Guid?)null] : _server.GetContainers().Select(c => (Guid?)c.Settings.Id);
            foreach (var storeId in stores) kind(definition.Title, () => { if (exportCustom(definition, storeId) is { } group) groups.Add(group); });
        }
        foreach (var container in _server.GetContainers().OrderBy(c => c.Settings.Name, StringComparer.OrdinalIgnoreCase)) {
            var name = databaseName(container.Settings);
            kind("Datamodel overrides of " + name, () => {
                var file = container.OverridesFile;
                add(groups, "datamodel", "Datamodel overrides", container, file.DataLocation, file.SettingsLocation, exportDatamodel(container));
            });
            foreach (var definitions in new[] { "logs", "graphql" }) {
                kind((definitions == "logs" ? "Logs of " : "GraphQL endpoints of ") + name, () => {
                    var found = layers(container, definitions);
                    add(groups, definitions, definitions == "logs" ? "Logs" : "GraphQL endpoints", container, found.Folders?.Data.Display, found.Folders?.Settings?.Display,
                        exportDefinitions(container, definitions, found));
                });
            }
            kind("Runtime types of " + name, () => add(groups, "types", "Runtime types", container, null, null, exportTypes(container)));
        }
        return new ExportList(_server.Settings.Name, _server.IsDevelopment, _server.EnvironmentName, _server.ConfigurationOverlay?.SectionName, groups, problems);
    }

    static void add(List<ExportGroup> groups, string kind, string area, NodeStoreContainer container, string? from, string? into, ExportEntry[] entries) {
        var database = databaseName(container.Settings);
        if (entries.Length > 0) groups.Add(new(kind, area + " · " + database, area, container.Settings.Id, database, null, from, into, entries));
    }

    static string databaseName(NodeStoreContainerSettingsBase settings) => string.IsNullOrEmpty(settings.Name) ? settings.Id.ToString() : settings.Name;
    static string? text(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value?.ToJsonString(_indented);
    static string id(params object?[] parts) => string.Join("|", parts.Select(p => p?.ToString() ?? "-"));

    // ---- each kind ----

    IEnumerable<ExportGroup> exportSettings() {
        var file = _server.OverridesFile;
        if (file == null) return [];
        if (file.Unavailable != null) throw new Exception(file.Unavailable);
        var overlay = _server.ConfigurationOverlay;
        var context = new LookupContext(SettingsOverridesFile.ToJson(_server.Settings), file);
        var target = _server.SettingsFileRelativePath;
        return file.Entries
            .Select(entry => (Entry: entry, Info: describePath(entry.Path, context)))
            .GroupBy(x => x.Info.ContainerId)
            .OrderBy(g => g.Key == null ? 0 : 1)
            .Select(g => {
                var database = g.Key == null ? null : containerTitle(g.Key.Value, context);
                return new ExportGroup("settings", "Settings · " + (database ?? "Server"), "Settings", g.Key, database, null, file.Display, _server.SettingsFileDisplay, [.. g.Select(x => {
                    var entry = x.Entry;
                    var fileValue = file.FileValue(entry.Path);
                    JsonNode? hide(JsonNode? v) => redact(v, entry.Path, context);
                    var configured = overlay != null && overlay.IsOverridden(entry.Path, out _);
                    var (moveBlocked, discardBlocked) = settingsBlocks(entry, x.Info);
                    return new ExportEntry(id("settings", entry.Path), x.Info.Label, x.Info.Where, x.Info.Where, target, change(entry.Kind),
                        entry.Kind == SettingsPatch.EntryKind.Added ? null : text(hide(fileValue)),
                        entry.Kind == SettingsPatch.EntryKind.Removed ? null : text(hide(entry.Value)),
                        x.Info.Secret || holdsSecret(entry.Value, hide) || holdsSecret(fileValue, hide),
                        configured ? "Set by the " + overlay!.SectionName + " configuration section, which is never exported." : null,
                        moveBlocked, discardBlocked);
                })]);
            });
    }

    ExportGroup? exportCustom(ICustomSettingsDefinition definition, Guid? storeId) {
        var set = _server.CustomSettings.SetFor(definition, storeId);
        if (!set.HasDataFile) return null;
        if (set.Error != null || set.DataUnavailable != null) throw new Exception(set.Error ?? set.DataUnavailable);
        var root = set.Value;
        var target = set.SettingsFilePath is { } path ? _server.RelativeToApplication(path) : null;
        var entries = set.Entries.Select(entry => {
            var (label, where, secret) = describeCustomPath(set, entry.Path, root);
            var fileValue = set.FileValue(entry.Path);
            JsonNode? hide(JsonNode? v) => redactCustom(set, v, entry.Path, root);
            var configured = set.IsConfigured(entry.Path, out _);
            var (moveBlocked, discardBlocked) = customBlocks(set, entry.Path);
            return new ExportEntry(id("custom", definition.FileKey, storeId, entry.Path), label, where, where, target ?? set.SettingsFileDisplay, change(entry.Kind),
                entry.Kind == SettingsPatch.EntryKind.Added ? null : text(hide(fileValue)),
                entry.Kind == SettingsPatch.EntryKind.Removed ? null : text(hide(entry.Value)),
                secret || holdsSecret(entry.Value, hide) || holdsSecret(fileValue, hide),
                configured ? "Set by " + set.DecidedOutsideTheFiles(entry.Path) + ", which is never exported."
                    : target == null ? set.SettingsFileDisplay + " is outside the application's folder." : null,
                moveBlocked, discardBlocked);
        }).ToArray();
        if (entries.Length == 0) return null;
        var database = storeId is Guid sid && _server.Containers.TryGetValue(sid, out var c) ? databaseName(c.Settings) : null;
        return new ExportGroup("custom", definition.Title + (database == null ? "" : " · " + database), definition.Title, storeId, database, definition.FileKey,
            set.DataFileDisplay, set.SettingsFileDisplay, entries);
    }

    ExportEntry[] exportDatamodel(NodeStoreContainer container) {
        var file = container.OverridesFile;
        var settingsLayer = file.ReadSettingsLayer();
        var target = _server.RelativeToApplication(file.SettingsPath) ?? file.SettingsLocation;
        return [.. DatamodelOverridesLayers.Entries(file.ReadDataLayer(_server)).Select(e => {
            var shared = DatamodelOverridesLayers.Get(settingsLayer, new(e.TypeId, e.PropertyId, e.Attribute), out var inSettings);
            var label = shortName(e.TypeName) + (e.PropertyId == null ? "" : "." + (e.PropertyName ?? e.PropertyId.ToString())) + ": " + e.Attribute;
            var change = !inSettings ? e.Value == null ? "same" : "added"
                : JsonNode.DeepEquals(shared, e.Value) ? "same" : e.Value == null ? "removed" : "changed";
            // discarding is a change of the draft, which the data model editor makes itself: the dialog asks it
            return new ExportEntry(id("datamodel", container.Settings.Id, e.TypeId, e.PropertyId, e.Attribute), label, null, e.TypeName ?? e.TypeId.ToString(), target, change,
                inSettings ? text(shared) : null, text(e.Value), false, null,
                file.CanWrite ? null : "The database has no storage provider to keep THIS SERVER's overrides in.", null);
        })];
        static string shortName(string? fullName) => fullName == null ? "" : fullName[(fullName.LastIndexOf('.') + 1)..];
    }

    static ExportEntry[] exportDefinitions(NodeStoreContainer container, string kind, (LayeredDefinitionFolders? Folders, List<LayeredDefinition> Definitions) found) {
        var (folders, definitions) = found;
        if (folders?.Settings == null) return [];
        return [.. definitions.Where(d => d.DataKey != null).Select(d => {
            var after = LayeredDefinitions.SettingsAfterMove(folders, d)!.Value;
            return new ExportEntry(id(kind, container.Settings.Id, d.Id), definitionName(d), null, null, folders.Settings.DisplayOf(after.Key), change(d.Change), d.SettingsText, d.DataText, false,
                null, null, null);
        })];
    }

    // what both folders have for a database's logs or endpoints; the database's own logs aside, whose
    // settings are not definitions to move
    (LayeredDefinitionFolders? Folders, List<LayeredDefinition> Definitions) layers(NodeStoreContainer container, string kind) {
        if (kind == "graphql") return (_server.GraphQLDefinitionsFor(container.Settings), _server.GraphQL.Layers(container.Settings.Id));
        var folders = _server.LogDefinitionsFor(container.Settings);
        if (folders == null) return (null, []);
        // the system keys as such: asking a logger for them fails while a store is being built
        var reserved = new HashSet<string>(StoreLogger.SystemLogKeys, StringComparer.OrdinalIgnoreCase);
        var (found, _) = LayeredDefinitions.Read(folders, CustomLogs.IdOf, StringComparer.OrdinalIgnoreCase);
        return (folders, [.. found.Where(d => !reserved.Contains(d.Id))]);
    }

    static string definitionName(LayeredDefinition d) {
        try {
            if (JsonNode.Parse(d.Text ?? d.SettingsText ?? "{}", null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject json) {
                foreach (var (key, value) in json) {
                    if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase) && value is JsonValue v && v.TryGetValue<string>(out var name) && name.Length > 0) return name;
                }
            }
        } catch (JsonException) {
            // named by its id
        }
        return d.Id;
    }

    ExportEntry[] exportTypes(NodeStoreContainer container) {
        var root = _server.RootDataFolderPath;
        var result = new List<ExportEntry>();
        foreach (var source in container.Settings.DatamodelSources ?? []) {
            if (source.Type != DatamodelSourceType.RuntimeTypes || !source.Enabled || source.FileIO != null) continue;
            var (files, _, _) = DatamodelSourceLoader.ResolveFiles(DatamodelSourceLoader.ResolveFilePath(source, root), DatamodelSourceLoader.DefaultPath(source), "*.json");
            foreach (var path in files) {
                var relative = _server.RelativeToApplication(path);
                result.Add(new ExportEntry(id("types", container.Settings.Id, source.Id, relative ?? path), Path.GetFileNameWithoutExtension(path), source.Name, source.Name,
                    relative ?? path, "file", null, File.ReadAllText(path), false, relative == null ? "The file is outside the application's folder." : null,
                    "A runtime type is a file of its own, not an override: download it into the project, or point its source at a folder the project tracks.",
                    "Runtime types are changed and deleted in the data model editor."));
            }
        }
        return [.. result];
    }

    static string change(SettingsPatch.EntryKind kind) => kind.ToString().ToLowerInvariant() switch { "value" => "changed", var k => k };
    static string change(DataChange? change) => change switch {
        DataChange.Added => "added",
        DataChange.Removed => "removed",
        DataChange.SameAsSettings => "same",
        _ => "changed",
    };
    static bool holdsSecret(JsonNode? value, Func<JsonNode?, JsonNode?> hide) => value != null && !JsonNode.DeepEquals(hide(value), value);

    // ---- the zip ----

    internal sealed record ExportPayload(string[]? Ids);

    /// <summary>A file of the zip: its text, or null for one to delete by hand; what it holds; the secrets in it.</summary>
    sealed class ExportFile(string path) {
        public string Path { get; } = path;
        public string? Text { get; set; }
        public List<string> Holds { get; } = [];
        public List<string> Secrets { get; } = [];
    }

    internal async Task<IResult> WriteExport(HttpContext ctx, ExportPayload payload) {
        var wanted = new HashSet<string>(payload.Ids ?? [], StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return Results.BadRequest(new { error = "Nothing was chosen. " });
        var listed = exportList().Groups;
        var chosen = listed.SelectMany(g => g.Entries.Select(e => (Group: g, Entry: e))).Where(x => wanted.Contains(x.Entry.Id) && x.Entry.Blocked == null).ToList();
        if (chosen.Count == 0) return Results.BadRequest(new { error = "None of the entries chosen can be exported. " });
        var files = new Dictionary<string, ExportFile>(StringComparer.OrdinalIgnoreCase);
        ExportFile fileAt(string path) => files.TryGetValue(path, out var f) ? f : files[path] = new ExportFile(path);
        foreach (var x in chosen) {
            var f = fileAt(x.Entry.File);
            f.Holds.Add(x.Group.Title + ": " + x.Entry.Label);
            if (x.Entry.Secret) f.Secrets.Add(x.Group.Title + ": " + x.Entry.Label);
        }
        // the text of each file, from the moves' own code
        var settingsPaths = chosen.Where(x => x.Group.Kind == "settings").Select(x => part(x.Entry.Id, 1)).ToArray();
        if (settingsPaths.Length > 0) fileAt(_server.SettingsFileRelativePath).Text = _server.SettingsFileTextWith(settingsPaths, out _);
        foreach (var set in chosen.Where(x => x.Group.Kind == "custom").GroupBy(x => x.Entry.File)) {
            var first = set.First().Entry.Id;
            var definition = _server.CustomSettings.Find(part(first, 1)) ?? throw new Exception("There are no custom settings \"" + part(first, 1) + "\". ");
            var storeId = Guid.TryParse(part(first, 2), out var sid) ? sid : (Guid?)null;
            fileAt(set.Key).Text = _server.CustomSettings.SetFor(definition, storeId).SettingsTextWith([.. set.Select(x => part(x.Entry.Id, 3))], out _);
        }
        foreach (var model in chosen.Where(x => x.Group.Kind == "datamodel").GroupBy(x => x.Group.StoreId!.Value)) {
            var container = _server.Containers[model.Key];
            var paths = model.Select(x => new DatamodelOverridesLayers.AttributePath(Guid.Parse(part(x.Entry.Id, 2)),
                Guid.TryParse(part(x.Entry.Id, 3), out var p) ? p : null, part(x.Entry.Id, 4))).ToArray();
            fileAt(model.First().Entry.File).Text = container.OverridesFile.SettingsTextWith(_server, paths, null);
        }
        foreach (var x in chosen.Where(x => x.Group.Kind is "logs" or "graphql")) {
            var (folders, definitions) = layers(_server.Containers[x.Group.StoreId!.Value], x.Group.Kind);
            var definition = definitions.First(d => string.Equals(d.Id, part(x.Entry.Id, 2), StringComparison.OrdinalIgnoreCase));
            fileAt(x.Entry.File).Text = LayeredDefinitions.SettingsAfterMove(folders!, definition)!.Value.Text;
            if (definition.DataRemoves && definition.SettingsKey == null) files.Remove(x.Entry.File); // takes away nothing the project has
        }
        foreach (var x in chosen.Where(x => x.Group.Kind == "types")) fileAt(x.Entry.File).Text = x.Entry.After;

        var bodyControl = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>();
        if (bodyControl != null) bodyControl.AllowSynchronousIO = true; // ZipArchive writes synchronously when entries close
        ctx.Response.ContentType = "application/zip";
        ctx.Response.Headers.ContentDisposition = "attachment; filename=\"relatude-changes-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmm") + ".zip\"";
        using var zip = new ZipArchive(ctx.Response.Body, ZipArchiveMode.Create, leaveOpen: true);
        await writeEntry(zip, "relatude-changes.md", changesText(files.Values));
        foreach (var f in files.Values.Where(f => f.Text != null).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)) await writeEntry(zip, f.Path, f.Text!);
        return Results.Empty;
    }

    static string part(string id, int index) => id.Split('|')[index];

    static async Task writeEntry(ZipArchive zip, string path, string content) {
        var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
        await using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(content);
        await stream.WriteAsync(bytes);
    }

    string changesText(IEnumerable<ExportFile> all) {
        var files = all.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("# Changes from " + _server.Settings.Name);
        sb.AppendLine();
        sb.AppendLine("Downloaded from the Relatude.DB admin UI on " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC (environment " + _server.EnvironmentName + ").");
        sb.AppendLine("Each file is the application's own file as it is on that installation, with the changes made there in the admin UI");
        sb.AppendLine("merged in. Nothing on the server was changed.");
        var written = files.Where(f => f.Text != null).ToList();
        if (written.Count > 0) {
            sb.AppendLine();
            sb.AppendLine("## Files");
            foreach (var f in written) {
                sb.AppendLine();
                sb.AppendLine("`" + f.Path + "`");
                foreach (var h in f.Holds) sb.AppendLine("- " + h);
            }
        }
        var deleted = files.Where(f => f.Text == null).ToList();
        if (deleted.Count > 0) {
            sb.AppendLine();
            sb.AppendLine("## Delete by hand");
            sb.AppendLine();
            sb.AppendLine("These were taken away on the installation. A zip cannot delete, so delete them from the project:");
            sb.AppendLine();
            foreach (var f in deleted) sb.AppendLine("- `" + f.Path + "` (" + string.Join("; ", f.Holds) + ")");
        }
        var secrets = files.Where(f => f.Secrets.Count > 0).ToList();
        if (secrets.Count > 0) {
            sb.AppendLine();
            sb.AppendLine("## Secrets");
            sb.AppendLine();
            sb.AppendLine("These files hold secrets. Keep them out of source control, or move the secrets into configuration");
            sb.AppendLine("(user secrets, environment variables or a key vault) before committing:");
            sb.AppendLine();
            foreach (var f in secrets) sb.AppendLine("- `" + f.Path + "`: " + string.Join("; ", f.Secrets));
        }
        sb.AppendLine();
        sb.AppendLine("## How to apply");
        sb.AppendLine();
        sb.AppendLine("1. Unzip over the project's folder, so each file lands at its path.");
        sb.AppendLine("2. Review the changes in your version control's diff before committing. A file changed in the project since that");
        sb.AppendLine("   installation was deployed shows those changes too. " + Defaults.SettingsFileName + " is written without comments.");
        sb.AppendLine("3. Deploy. When the installation starts on the new files, it takes out of its own overrides what they now say the same as,");
        sb.AppendLine("   and logs what it took out.");
        if (files.Any(f => f.Path.StartsWith(Defaults.DataFolderPath + "/", StringComparison.OrdinalIgnoreCase))) {
            sb.AppendLine();
            sb.AppendLine("Runtime types are kept in " + Defaults.DataFolderPath + " unless their source names a file or folder. To keep them in");
            sb.AppendLine("source control, put them in a folder the project tracks and point the source there.");
        }
        return sb.ToString();
    }
}
