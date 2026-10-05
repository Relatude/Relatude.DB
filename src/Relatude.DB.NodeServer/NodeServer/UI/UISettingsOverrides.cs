using Relatude.DB.NodeServer.Settings;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// What relatude.db.overrides.json holds, as the settings pages show it, and the two things that can
/// be done with it besides editing: moving entries into relatude.db.json, which makes them part of the
/// application's own settings, and discarding them, which puts back the value relatude.db.json gives.
///
/// The file covers the whole server, so the list does too - the server settings and every database -
/// whichever settings page it is opened from. Neither action touches a setting that the configuration
/// section or the application's code decides: those are listed, marked, and left alone. Configuration
/// above all must never reach relatude.db.json, which is usually in source control.
/// </summary>
sealed partial class UISettings {

    void registerOverrides(UICommands commands) {
        commands.Register("settings-overrides-get", ctx => buildOverrides());
        commands.Register("settings-overrides-move", ctx => moveOverrides(ctx.Payload<OverridesPathsPayload>()));
        commands.Register("settings-overrides-discard", ctx => discardOverrides(ctx.Payload<OverridesPathsPayload>()));
    }

    /// <summary>The line the settings pages show beside the file name, or null when the overrides file
    /// is turned off and saves go to relatude.db.json.</summary>
    object? overridesSummary() {
        var file = _server.OverridesFile;
        if (file == null) return null;
        // Error: the file could not be read at start, so nothing in it is in force and nothing can be saved
        return new { File = file.Display, Count = file.Entries.Count, Error = file.Unavailable };
    }

    // ---- the list ----

    object buildOverrides() {
        var file = _server.OverridesFile;
        var overlay = _server.ConfigurationOverlay;
        if (file == null) {
            return new { Enabled = false, File = (string?)null, Error = (string?)null, SettingsFile = _server.SettingsFileDisplay, ConfigSection = overlay?.SectionName, Count = 0, Groups = Array.Empty<object>() };
        }
        var context = new LookupContext(SettingsOverridesFile.ToJson(_server.Settings), file);
        var groups = file.Entries
            .Select(entry => (Entry: entry, Info: describePath(entry.Path, context)))
            .GroupBy(x => x.Info.ContainerId)
            .Select(g => (Group: g, Title: g.Key == null ? "Server" : containerTitle(g.Key.Value, context)))
            .OrderBy(x => x.Group.Key == null ? 0 : 1).ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .Select(x => new {
                Scope = x.Group.Key == null ? "server" : "database",
                StoreId = x.Group.Key,
                x.Title,
                Entries = x.Group.Select(e => describeEntry(e.Entry, e.Info, context)).ToArray(),
            })
            .ToArray();
        return new {
            Enabled = true,
            File = file.Display,
            Error = file.Unavailable,
            SettingsFile = _server.SettingsFileDisplay,
            ConfigSection = overlay?.SectionName,
            Count = file.Entries.Count,
            Groups = groups,
        };
    }

    object describeEntry(SettingsPatch.Entry entry, PathInfo info, LookupContext context) {
        var file = context.File!;
        var overlay = _server.ConfigurationOverlay;
        var configured = overlay != null && overlay.IsOverridden(entry.Path, out _);
        var decidedBy = _server.DecidedOutsideTheSettingsFiles(entry.Path);
        var wholeDatabase = (info.ContainerId == null && SettingsPatch.IsAtOrBelow(info.Relative, nameof(RelatudeDBServerSettings.ContainerSettings)))
            || (info.ContainerId != null && info.Relative.Length == 0);
        var discardBlocked = decidedBy != null
            ? "Set by " + decidedBy + ", which decides it whichever file holds it."
            : wholeDatabase
                ? "A whole database cannot be taken back here. Move it into " + Defaults.SettingsFileName + ", or edit the two files by hand."
                : null;
        var fileValue = file.FileValue(entry.Path);
        object? value = null, before = null;
        string? summary = null;
        switch (entry.Kind) {
            case SettingsPatch.EntryKind.Value:
                value = info.Secret ? null : redact(entry.Value, entry.Path, context);
                before = info.Secret ? null : redact(fileValue, entry.Path, context);
                break;
            case SettingsPatch.EntryKind.Added:
                summary = "Added here";
                break;
            case SettingsPatch.EntryKind.Removed:
                summary = "Removed here, still in " + Defaults.SettingsFileName;
                break;
        }
        return new {
            entry.Path,
            Kind = entry.Kind.ToString().ToLowerInvariant(),
            info.Label,
            info.Where,
            info.Secret,
            Value = value,
            HasValue = hasValue(entry.Value),
            FileValue = before,
            FileHasValue = hasValue(fileValue),
            Summary = summary,
            CanMove = !configured,
            MoveBlocked = configured ? "Set by the " + overlay!.SectionName + " configuration section, which is never copied into " + Defaults.SettingsFileName + "." : null,
            CanDiscard = discardBlocked == null,
            DiscardBlocked = discardBlocked,
        };
    }

    // a value shown in the list, with every secret inside an object or list set whole blanked out:
    // a database's AI settings added here carry its API key
    JsonNode? redact(JsonNode? value, string path, LookupContext context) {
        switch (value) {
            case JsonObject obj: {
                var result = new JsonObject();
                foreach (var (key, inner) in obj) result[key] = redact(inner, path + "." + key, context);
                return result;
            }
            case JsonArray array: {
                var result = new JsonArray();
                foreach (var inner in array) {
                    var id = SettingsPatch.idOf(inner);
                    result.Add(redact(inner, id == null ? path : path + "[" + id + "]", context));
                }
                return result;
            }
            default:
                if (!describePath(path, context).Secret) return value?.DeepClone();
                return hasValue(value) ? JsonValue.Create("••••••") : null;
        }
    }

    // ---- moving into relatude.db.json ----

    object moveOverrides(OverridesPathsPayload payload) {
        lock (_saveLock) {
            var moved = _server.MoveOverridesIntoSettingsFile(payload.Paths);
            return new { Moved = moved, Overrides = buildOverrides() };
        }
    }

    // ---- discarding: the value from relatude.db.json back ----

    /// <summary>
    /// Takes entries out of the overrides file by putting back what relatude.db.json says: the value of
    /// a setting, a removed element restored, an added element removed (unless something still points
    /// at it, the same rule the list editors follow). The change is made to the settings in force and
    /// saved like any other edit, so the file follows from it. Settings that only apply when a database
    /// opens take effect at the next open, as with a save.
    /// </summary>
    object discardOverrides(OverridesPathsPayload payload) {
        lock (_saveLock) {
            var file = _server.OverridesFile ?? throw new Exception("The settings overrides file is turned off, so there is nothing to discard. ");
            var wanted = new HashSet<string>(payload.Paths ?? [], StringComparer.OrdinalIgnoreCase);
            var discarded = new List<string>();
            var rejected = new List<RejectedSetting>();
            var changedIn = new Dictionary<Guid, List<string>>();
            foreach (var entry in file.Entries.Where(e => wanted.Contains(e.Path)).ToArray()) {
                try {
                    discard(entry, file);
                    discarded.Add(entry.Path);
                    var (containerId, relative) = splitContainer(entry.Path);
                    if (containerId != null) {
                        if (!changedIn.TryGetValue(containerId.Value, out var paths)) changedIn[containerId.Value] = paths = [];
                        paths.Add(relative);
                    }
                } catch (Exception error) {
                    rejected.Add(new RejectedSetting(entry.Path, error.Message));
                }
            }
            foreach (var (_, paths) in changedIn) forgetChangedIOProviders(paths);
            if (discarded.Count > 0) _server.UpdateWAFServerSettingsFile();
            return new { Discarded = discarded, Rejected = rejected, Overrides = buildOverrides() };
        }
    }

    void discard(SettingsPatch.Entry entry, SettingsOverridesFile file) {
        var decidedBy = _server.DecidedOutsideTheSettingsFiles(entry.Path);
        if (decidedBy != null) throw new Exception("Set by " + decidedBy + ", which decides it whichever file holds it.");
        var (containerId, relative) = splitContainer(entry.Path);
        if (containerId == null && SettingsPatch.IsAtOrBelow(relative, nameof(RelatudeDBServerSettings.ContainerSettings))
            || containerId != null && relative.Length == 0) {
            throw new Exception("A whole database cannot be taken back here.");
        }
        object root = containerId == null ? _server.Settings : getContainer(containerId.Value).Settings;
        switch (entry.Kind) {
            case SettingsPatch.EntryKind.Value:
                SettingsAccessor.Assign(root, relative, file.FileValue(entry.Path));
                break;
            case SettingsPatch.EntryKind.Added: {
                var (listPath, id) = splitElement(relative);
                var blocking = describeUsage(listPath, id, containerId).Blocking;
                if (blocking.Length > 0) throw new Exception("Still used as " + string.Join(", ", blocking) + ". Point those somewhere else first.");
                removeElement(root, listPath, id);
                if (string.Equals(listPath, "IOSettings", StringComparison.OrdinalIgnoreCase)) _server.ForgetIOProvider(id);
                break;
            }
            case SettingsPatch.EntryKind.Removed: {
                var (listPath, _) = splitElement(relative);
                var element = file.FileValue(entry.Path) as JsonObject ?? throw new Exception(Defaults.SettingsFileName + " no longer has that element.");
                var baseList = SettingsPatch.Read(file.Base, SettingsOverlay.OverridePath(containerId, listPath)) as JsonArray;
                insertElement(root, listPath, element, baseList);
                break;
            }
        }
    }

    static void removeElement(object root, string listPath, Guid id) {
        var (owner, property) = arrayProperty(root, listPath, create: false);
        if (owner == null) return;
        var remaining = existing(owner, property).Where(item => idOf(item) != id).ToArray();
        property.SetValue(owner, toArray(property.PropertyType.GetElementType()!, remaining));
    }

    // back where relatude.db.json has it: after the nearest element before it there that is still in the list
    static void insertElement(object root, string listPath, JsonObject element, JsonArray? baseList) {
        var (owner, property) = arrayProperty(root, listPath, create: true);
        var elementType = property.PropertyType.GetElementType()!;
        var item = element.Deserialize(elementType, LocalSettingsLoaderFile.JsonOptions) ?? throw new Exception("The element could not be read back.");
        var id = idOf(item);
        var items = existing(owner!, property).ToList();
        if (items.Any(i => idOf(i) == id)) return; // already back
        var position = baseList == null ? items.Count : 0;
        foreach (var before in baseList ?? []) {
            var beforeId = SettingsPatch.idOf(before);
            if (beforeId == id) break;
            var at = items.FindIndex(i => idOf(i) == beforeId);
            if (at >= 0) position = at + 1;
        }
        items.Insert(position, item);
        property.SetValue(owner, toArray(elementType, [.. items]));
    }

    // ---- what a path is called ----

    /// <summary>What the list says about one entry's path, read off the catalog.</summary>
    sealed record PathInfo(Guid? ContainerId, string Relative, string Label, string? Where, bool Secret);

    /// <summary>The settings in force and the overrides file, read once per request: labels look into
    /// both to name a list element by its own name field.</summary>
    sealed record LookupContext(JsonObject Live, SettingsOverridesFile? File);

    PathInfo describePath(string fullPath, LookupContext context) {
        var (containerId, relative) = splitContainer(fullPath);
        if (containerId != null && relative.Length == 0) return new(containerId, relative, "The database itself", null, false);
        var catalog = containerId == null ? SettingsCatalog.Server : SettingsCatalog.Database;
        foreach (var section in catalog) {
            foreach (var group in section.Groups) {
                var where = section.Title + " · " + group.Title;
                foreach (var definition in group.Settings) {
                    if (string.Equals(definition.Path, relative, StringComparison.OrdinalIgnoreCase)) return new(containerId, relative, definition.Label, where, definition.Secret);
                }
                if (group.List is not { } list || !relative.StartsWith(list.Path + "[", StringComparison.OrdinalIgnoreCase)) continue;
                var close = relative.IndexOf(']', list.Path.Length);
                if (close < 0) continue;
                var elementPath = SettingsOverlay.OverridePath(containerId, relative[..(close + 1)]);
                var item = list.ItemName + " \"" + elementLabel(list, elementJson(elementPath, context), elementPath) + "\"";
                if (close == relative.Length - 1) return new(containerId, relative, char.ToUpperInvariant(item[0]) + item[1..], where, false);
                var field = relative[(close + 2)..];
                var fieldDefinition = list.Fields.FirstOrDefault(f => string.Equals(f.Path, field, StringComparison.OrdinalIgnoreCase));
                return new(containerId, relative, (fieldDefinition?.Label ?? field) + " of " + item, where, fieldDefinition?.Secret == true);
            }
        }
        // an object set whole - the AI settings of a database that had none - or a setting the pages
        // do not show, such as what the logs record
        foreach (var section in catalog) {
            foreach (var group in section.Groups) {
                if (group.Settings.Any(d => d.Path.StartsWith(relative + ".", StringComparison.OrdinalIgnoreCase))) {
                    return new(containerId, relative, group.Title + " (all of it)", section.Title, false);
                }
            }
        }
        return new(containerId, relative, SettingsPatch.Display(relative), null, false);
    }

    static JsonObject? elementJson(string elementPath, LookupContext context)
        => (SettingsPatch.Read(context.Live, elementPath) ?? (context.File == null ? null : SettingsPatch.Read(context.File.Base, elementPath))) as JsonObject;

    /// <summary>The name a list element goes by: its label field, a storage provider's name where the
    /// field holds the provider's id (a file store), or the kind of element and the start of its id.</summary>
    string elementLabel(SettingListDefinition list, JsonObject? element, string elementPath) {
        string? text = null;
        if (element != null && element.TryGetPropertyValue(list.LabelField, out var value) && value is JsonValue v && v.TryGetValue<string>(out var s)) text = s;
        if (!string.IsNullOrWhiteSpace(text) && Guid.TryParse(text, out var referenced)) {
            var provider = _server.GetContainers().SelectMany(c => c.Settings.IOSettings ?? []).FirstOrDefault(io => io.Id == referenced);
            text = provider == null ? text : string.IsNullOrEmpty(provider.Name) ? provider.IOType.ToString() : provider.Name;
        }
        if (!string.IsNullOrWhiteSpace(text)) return text;
        var open = elementPath.LastIndexOf('[');
        var id = open >= 0 && elementPath.EndsWith(']') ? elementPath[(open + 1)..^1] : "";
        return list.ItemName + " " + (id.Length > 8 ? id[..8] : id);
    }

    string containerTitle(Guid containerId, LookupContext context) {
        if (_server.Containers.TryGetValue(containerId, out var container)) {
            return string.IsNullOrEmpty(container.Settings.Name) ? containerId.ToString() : container.Settings.Name;
        }
        var json = elementJson(SettingsOverlay.OverridePath(containerId, "").TrimEnd('.'), context);
        return json?["Name"] is JsonValue name && name.TryGetValue<string>(out var text) && text.Length > 0 ? text : containerId.ToString();
    }

    static (Guid? ContainerId, string Relative) splitContainer(string fullPath) {
        const string prefix = "ContainerSettings[";
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return (null, fullPath);
        var close = fullPath.IndexOf(']', prefix.Length);
        if (close < 0 || !Guid.TryParse(fullPath[prefix.Length..close], out var id)) return (null, fullPath);
        if (close == fullPath.Length - 1) return (id, "");
        return fullPath[close + 1] == '.' ? (id, fullPath[(close + 2)..]) : (null, fullPath);
    }

    static (string ListPath, Guid Id) splitElement(string relative) {
        var open = relative.LastIndexOf('[');
        if (open <= 0 || !relative.EndsWith(']') || !Guid.TryParse(relative[(open + 1)..^1], out var id)) {
            throw new Exception("\"" + relative + "\" is not a list element.");
        }
        return (relative[..open], id);
    }
}

sealed record OverridesPathsPayload(string[]? Paths);
