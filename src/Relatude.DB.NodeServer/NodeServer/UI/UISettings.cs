using Relatude.DB.DataStores;
using Relatude.DB.NodeServer.Settings;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// The settings sections of the admin UI, both of them: the server's own settings and the settings
/// of one database. The two differ only in which catalog they run over and which object the paths
/// are resolved against, so they share everything below.
///
/// Three things are reported for every setting beyond its value, because all three change what a
/// reader should do with it:
///   - what it does, from <see cref="SettingsCatalog"/>;
///   - whether it still holds its default, from the settings class itself;
///   - whether configuration (appsettings.json, environment variables, user secrets) decides it, in
///     which case it cannot be edited here at all - the overlay would win at the next start and the
///     file would keep its own value regardless. Such a setting is shown, marked, and locked.
///
/// A group may edit a collection instead of a fixed set of settings (storage providers, file stores).
/// Its elements are addressed by Id, so their fields are ordinary settings on longer paths and carry
/// all three marks too; only adding and removing need commands of their own.
/// </summary>
sealed class UISettings {
    readonly RelatudeDBServer _server;
    readonly object _saveLock = new();
    internal UISettings(RelatudeDBServer server) => _server = server;

    internal void Register(UICommands commands) {
        commands.Register("settings-server-get", ctx => buildServer());
        commands.Register("settings-server-save", ctx => saveServer(ctx.Payload<SaveServerSettingsPayload>()));
        commands.Register("settings-db-get", ctx => buildDatabase(ctx.Payload<DatabaseSettingsPayload>().StoreId));
        commands.Register("settings-db-save", ctx => saveDatabase(ctx.Payload<SaveDatabaseSettingsPayload>()));
        commands.Register("settings-db-list-add", ctx => listAdd(ctx.Payload<ListAddPayload>()));
        commands.Register("settings-db-list-remove", ctx => listRemove(ctx.Payload<ListItemPayload>()));
    }

    // ---- reading ----

    object buildServer() {
        var settings = _server.Settings;
        return new {
            Scope = "server",
            Title = string.IsNullOrEmpty(settings.Name) ? "Server" : settings.Name,
            SettingsFile = settings.DBSettingsFilePath ?? Defaults.SettingsFileName,
            ConfigSection = _server.ConfigurationOverlay?.SectionName,
            Sections = buildSections(SettingsCatalog.Server, settings, containerId: null),
            Pickers = new {
                Databases = _server.GetContainers().Select(c => new {
                    Value = c.Settings.Id.ToString(),
                    Label = string.IsNullOrEmpty(c.Settings.Name) ? c.Settings.Id.ToString() : c.Settings.Name,
                }),
            },
        };
    }

    object buildDatabase(Guid storeId) {
        var container = getContainer(storeId);
        var settings = container.Settings;
        var ioById = (settings.IOSettings ?? []).ToDictionary(io => io.Id);
        return new {
            Scope = "database",
            StoreId = settings.Id,
            Title = string.IsNullOrEmpty(settings.Name) ? settings.Id.ToString() : settings.Name,
            State = container.HasFailed ? "Error" : container.Store?.State.ToString() ?? "Closed",
            IsOpen = container.IsOpenOrOpening(),
            SettingsFile = _server.Settings.DBSettingsFilePath ?? Defaults.SettingsFileName,
            ConfigSection = _server.ConfigurationOverlay?.SectionName,
            Sections = buildSections(SettingsCatalog.Database, settings, storeId),
            Pickers = new {
                IoProviders = (settings.IOSettings ?? []).Select(io => new {
                    Value = io.Id.ToString(),
                    Label = string.IsNullOrEmpty(io.Name) ? io.IOType.ToString() : io.Name,
                    Hint = io.IOType == IOTypes.LocalDisk ? io.Path : io.IOType.ToString(),
                }),
                FileStores = (settings.FileStoreSettings ?? []).Select(fs => new {
                    Value = fs.Id.ToString(),
                    Label = ioById.TryGetValue(fs.IoProviderId, out var io) && !string.IsNullOrEmpty(io.Name)
                        ? io.Name + " · " + fs.StoreType
                        : fs.StoreType + " · " + fs.Id,
                    Hint = (string?)fs.StoreType.ToString(),
                }),
                Cultures = cultures(),
                ValueIndexes = indexEnginePicker(settings.LocalSettings?.ValueIndexes),
                TextIndexes = indexEnginePicker(settings.LocalSettings?.TextIndexes),
                VectorIndexes = indexEnginePicker(settings.LocalSettings?.VectorIndexes),
            },
        };
    }

    // the memory index comes first, under the id that means it, so a default of Guid.Empty is a named
    // choice rather than an unknown value; the short id tells two engines of the same kind apart
    static object[] indexEnginePicker(IndexEngineSettings[]? engines) {
        return [
            new { Value = Guid.Empty.ToString(), Label = "Memory", Hint = "in RAM, rebuilt from the log at every open" },
            .. (engines ?? []).Select(e => new {
                Value = e.Id.ToString(),
                Label = (e.TypeName ?? "?") + " · " + e.MaxMemoryUsageInMb + " MB",
                Hint = e.Id.ToString("N")[..8],
            }),
        ];
    }

    object[] buildSections(SettingSectionDefinition[] catalog, object root, Guid? containerId) {
        var rootType = root.GetType();
        return [.. catalog.Select(section => (object)new {
            section.Id,
            section.Title,
            section.Icon,
            Groups = section.Groups.Select(group => new {
                group.Id,
                group.Title,
                group.Help,
                Settings = group.Settings.Select(definition => buildSetting(definition, rootType, root, containerId, "")),
                List = group.List == null ? null : buildList(group.List, rootType, root, containerId),
            }),
        })];
    }

    object buildList(SettingListDefinition list, Type rootType, object root, Guid? containerId) {
        var overlay = _server.ConfigurationOverlay;
        // configuration can append to an array but never remove from one, so a list the overlay has
        // touched is not one this page can be trusted to edit
        var locked = overlay != null && overlay.IsOverridden(SettingsOverlay.OverridePath(containerId, list.Path), out _);
        return new {
            list.Path,
            list.ItemName,
            list.LabelField,
            list.EmptyHelp,
            Locked = locked,
            Items = elements(root, list.Path).Select(item => {
                var id = idOf(item);
                var prefix = list.Path + "[" + id + "].";
                var usage = describeUsage(list.Path, id, containerId);
                return new {
                    Id = id,
                    Settings = list.Fields.Select(field => buildSetting(field, rootType, root, containerId, prefix)),
                    usage.UsedBy,
                    Removable = !locked && usage.Blocking.Length == 0,
                    usage.Blocking,
                    usage.RemoveWarning,
                };
            }),
        };
    }

    object buildSetting(SettingDefinition definition, Type rootType, object root, Guid? containerId, string prefix) {
        var path = prefix + definition.Path;
        var description = SettingsAccessor.Describe(rootType, path);
        var value = SettingsAccessor.Read(root, path);
        JsonNode? configured = null;
        var overlay = _server.ConfigurationOverlay;
        var overridden = overlay != null && overlay.IsOverridden(SettingsOverlay.OverridePath(containerId, path), out configured);
        var isSecret = definition.Secret;
        return new {
            Path = path,
            definition.Label,
            definition.Help,
            definition.Unit,
            definition.Placeholder,
            definition.Picker,
            definition.Generate,
            // the sibling is named relative to the element, so it needs the same prefix to be found
            VisibleWhen = definition.VisibleWhen == null ? null
                : new { Path = prefix + definition.VisibleWhen.Path, definition.VisibleWhen.Values },
            Secret = isSecret,
            ReadOnly = definition.ReadOnly || description.Property.SetMethod?.IsPublic != true,
            Applies = definition.Applies.ToString().ToLowerInvariant(),
            Editor = description.Editor.ToString().ToLowerInvariant(),
            description.Optional,
            Choices = choices(description, definition, value),
            // the choices of a suggested-values setting are a starting point, not the set of legal
            // values, so the field stays a text field with the list beside it
            AllowCustom = description.EnumNames == null && definition.Suggestions != null,
            // a secret is never handed back to the browser: the field shows whether one is set, and
            // a save only carries it when someone actually typed a new one
            Value = isSecret ? null : value,
            Default = isSecret ? null : description.DefaultValue,
            HasValue = hasValue(value),
            IsDefault = !definition.ReadOnly && sameValue(value, description.DefaultValue),
            Overridden = overridden,
            ConfiguredValue = overridden && !isSecret ? configured : null,
        };
    }

    /// <summary>
    /// The choices of an enum setting, minus the members the catalog excludes - a value the settings
    /// file is not allowed to hold. The value the setting currently has is kept regardless, so an
    /// excluded one that is already stored is visible instead of silently reading as unset.
    ///
    /// A free text setting whose catalog entry carries <see cref="SettingDefinition.Suggestions"/>
    /// reports them the same way, together with <c>AllowCustom</c>; nothing is excluded there, since
    /// the list does not bound the value in the first place.
    /// </summary>
    static object[]? choices(SettingsAccessor.PropertyDescription description, SettingDefinition definition, JsonNode? value) {
        if (description.EnumNames == null) {
            if (definition.Suggestions == null) return null;
            return [.. definition.Suggestions.Select(s => (object)new { s.Value, Label = s.Value, s.Hint })];
        }
        var excluded = definition.ExcludedChoices;
        var current = value is JsonValue v && v.TryGetValue<string>(out var name) ? name : null;
        return [.. description.EnumNames
            .Where(n => excluded == null || !excluded.Contains(n, StringComparer.OrdinalIgnoreCase)
                     || string.Equals(n, current, StringComparison.OrdinalIgnoreCase))
            .Select(n => (object)new { Value = n, Label = n })];
    }

    // ---- what points at a collection element, and what that means for removing it ----

    sealed record Usage(string[] UsedBy, string[] Blocking, string? RemoveWarning);

    Usage describeUsage(string listPath, Guid id, Guid? containerId) => listPath switch {
        "IOSettings" => ioProviderUsage(id, containerId),
        "FileStoreSettings" => fileStoreUsage(id, containerId),
        "DatamodelSources" => datamodelSourceUsage(),
        "LocalSettings.ValueIndexes" => indexEngineUsage(id, containerId, "value", local => local.DefaultValueIndex),
        "LocalSettings.TextIndexes" => indexEngineUsage(id, containerId, "text", local => local.DefaultTextIndex),
        "LocalSettings.VectorIndexes" => indexEngineUsage(id, containerId, "vector", local => local.DefaultVectorIndex),
        _ => new Usage([], [], null),
    };

    /// <summary>
    /// An index engine is referred to by one thing: the default of its kind on the same database
    /// (properties cannot name an engine yet). That reference blocks removal, since a default naming
    /// an engine its list lacks stops the database from opening. Removing an engine nothing points at
    /// costs nothing at once - its files stay where they are - so that only gets a warning.
    /// </summary>
    Usage indexEngineUsage(Guid id, Guid? containerId, string kind, Func<SettingsLocal, Guid> defaultOf) {
        var used = new List<string>();
        var local = containerId == null ? null : getContainer(containerId.Value).Settings.LocalSettings;
        if (local != null && defaultOf(local) == id) used.Add("the default " + kind + " index engine");
        var warning = "The engine's folder below the index folder is left on disk; delete it by hand if the engine is not coming back."
            + " An index that lived in it is rebuilt in the engine the default names when the database is next opened.";
        return new Usage([.. used], [.. used], warning);
    }

    /// <summary>
    /// Everything pointing at a storage provider, across every database - not only the one being
    /// edited. Providers are declared per database but <see cref="RelatudeDBServer.TryGetIO"/> resolves
    /// an id across all of them, so a provider another database leans on is one this page must not
    /// remove. Everything that points at a provider breaks without it, so every use blocks.
    /// </summary>
    Usage ioProviderUsage(Guid id, Guid? containerId) {
        var used = new List<string>();
        foreach (var container in _server.GetContainers()) {
            var s = container.Settings;
            // the database being edited is the one the reader is looking at, so naming it in every
            // line would only crowd out the ones that matter: the uses somewhere else
            var of = s.Id == containerId ? "" : " of \"" + (string.IsNullOrEmpty(s.Name) ? s.Id.ToString() : s.Name) + "\"";
            void note(Guid? assigned, string role) {
                if (assigned == id) used.Add(role + of);
            }
            note(s.IoDatabase, "the database files");
            note(s.IoDatabaseSecondary, "the secondary log");
            note(s.IoIndexes, "the index files");
            note(s.IoBackup, "the backups");
            note(s.IoLog, "the activity log");
            foreach (var store in s.FileStoreSettings ?? []) {
                if (store.IoProviderId == id) used.Add("a file store" + of);
            }
            foreach (var source in s.DatamodelSources ?? []) {
                if (source.FileIO == id) used.Add("the datamodel source \"" + (source.Name ?? source.Id.ToString()) + "\"" + of);
            }
        }
        return new Usage([.. used], [.. used], null);
    }

    /// <summary>
    /// A file store is different: an assignment pointing at it blocks removal, but the real cost is
    /// invisible from here. Every uploaded file records the id of the store it went into, so removing
    /// a store that holds files leaves those file values pointing at nothing. Finding out whether it
    /// holds any means walking every node, which is what the Files section is for - so this warns
    /// rather than blocks.
    /// </summary>
    Usage fileStoreUsage(Guid id, Guid? containerId) {
        var used = new List<string>();
        foreach (var container in _server.GetContainers()) {
            var s = container.Settings;
            if (s.LocalSettings?.DefaultFileStore != id) continue;
            used.Add("the default file store" + (s.Id == containerId ? "" : " of \"" + (string.IsNullOrEmpty(s.Name) ? s.Id.ToString() : s.Name) + "\""));
        }
        var warning = "Files already uploaded into this store record its id and stop resolving once it is gone."
            + " The missing-file scan under Files is what tells you whether it holds any.";
        return new Usage([.. used], [.. used], warning);
    }

    /// <summary>
    /// Nothing in the settings points at a model source, so nothing blocks removing one - but what it
    /// costs is not visible from the settings file either. The types it defines leave the model, and
    /// the nodes already stored under them are read back as bare nodes, since a node whose type the
    /// model no longer has falls back to the base type and loses the properties that type declared.
    /// Turning the source off does exactly the same thing while keeping what it says, so the warning
    /// names that as the way back.
    /// </summary>
    static Usage datamodelSourceUsage() => new([], [],
        "The node types and relations it defines leave the model when the database is next opened, and nodes already"
        + " stored under them are read back without the properties those types declared. Turning it off instead has the"
        + " same effect on the model and keeps the definition here.");

    static IEnumerable<object> elements(object root, string path) {
        var (owner, property) = arrayProperty(root, path, create: false);
        if (owner == null || property.GetValue(owner) is not System.Collections.IEnumerable items) yield break;
        foreach (var item in items) if (item != null) yield return item;
    }

    static Guid idOf(object item) =>
        item.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)?.GetValue(item) is Guid id ? id : Guid.Empty;

    static object[] cultures() {
        return [.. CultureInfo.GetCultures(CultureTypes.SpecificCultures)
            .Where(c => !string.IsNullOrEmpty(c.Name))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => (object)new { Value = c.Name, Label = c.Name, Hint = c.EnglishName })];
    }

    static bool hasValue(JsonNode? value) {
        if (value == null) return false;
        if (value is JsonValue v && v.TryGetValue<string>(out var text)) return !string.IsNullOrEmpty(text);
        return true;
    }

    // an unset optional string and an empty one are the same setting to whoever reads the page, so
    // they must not show up as a difference from the default
    static bool sameValue(JsonNode? a, JsonNode? b) {
        if (!hasValue(a) && !hasValue(b)) return true;
        return JsonNode.DeepEquals(a, b);
    }

    // ---- writing ----

    object saveServer(SaveServerSettingsPayload payload) {
        lock (_saveLock) {
            var settings = _server.Settings;
            var result = apply(SettingsCatalog.Server, settings, payload.Values, containerId: null);
            if (result.Changed.Count > 0) _server.UpdateWAFServerSettingsFile();
            return new { result.Changed, result.Rejected, Reopened = false, Settings = buildServer() };
        }
    }

    object saveDatabase(SaveDatabaseSettingsPayload payload) {
        lock (_saveLock) {
            var container = getContainer(payload.StoreId);
            var settings = container.Settings;
            var result = apply(SettingsCatalog.Database, settings, payload.Values, payload.StoreId);
            var reopened = false;
            if (result.Changed.Count > 0) {
                forgetChangedIOProviders(result.Changed);
                _server.UpdateWAFServerSettingsFile();
                // the settings object the container already holds is the one that was just edited, so
                // re-applying it is only a close and open - which is what makes open-time settings take
                if (payload.Reopen && container.IsOpenOrOpening()) {
                    container.ApplyNewSettings(settings, reopenIfOpen: true);
                    reopened = true;
                }
            }
            return new { result.Changed, result.Rejected, Reopened = reopened, Settings = buildDatabase(payload.StoreId) };
        }
    }

    /// <summary>
    /// Live IO providers are built once and cached by id, so a provider whose folder or container has
    /// just changed would keep serving the old location even after the database is reopened. Dropping
    /// the cached instance is what makes the edit real; anything still holding the old one keeps
    /// working off it until it lets go.
    /// </summary>
    void forgetChangedIOProviders(IEnumerable<string> changedPaths) {
        const string prefix = "IOSettings[";
        foreach (var path in changedPaths) {
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var close = path.IndexOf(']');
            if (close > prefix.Length && Guid.TryParse(path[prefix.Length..close], out var id)) _server.ForgetIOProvider(id);
        }
    }

    object listAdd(ListAddPayload payload) {
        lock (_saveLock) {
            var settings = getContainer(payload.StoreId).Settings;
            var list = findList(payload.Path);
            requireUnlockedList(list, payload.StoreId);
            var (owner, property) = arrayProperty(settings, list.Path, create: true);
            var elementType = property.PropertyType.GetElementType()!;
            var item = Activator.CreateInstance(elementType) ?? throw new Exception("Could not create a " + list.ItemName + ".");
            elementType.GetProperty("Id")!.SetValue(item, SecureGuid.New());
            append(owner!, property, item);
            var id = idOf(item);
            var prefix = list.Path + "[" + id + "].";
            var rejected = new List<RejectedSetting>();
            foreach (var (field, value) in list.NewItem ?? []) {
                SettingsAccessor.Write(settings, prefix + field, JsonSerializer.SerializeToElement(value));
            }
            foreach (var (field, value) in payload.Values ?? []) {
                var definition = list.Fields.FirstOrDefault(f => string.Equals(f.Path, field, StringComparison.OrdinalIgnoreCase));
                if (definition == null || definition.ReadOnly) {
                    rejected.Add(new RejectedSetting(field, "Not a field of a " + list.ItemName + "."));
                    continue;
                }
                try {
                    SettingsAccessor.Write(settings, prefix + field, value);
                } catch (Exception error) {
                    rejected.Add(new RejectedSetting(field, error.Message));
                }
            }
            // a new element that has to name a storage provider starts on the one holding the database,
            // so it is usable as added rather than pointing at nothing. Only a field that cannot be left
            // empty: an optional one - a model source's file provider - means something by being unset,
            // and filling it in would pick a behaviour nobody asked for
            foreach (var field in list.Fields.Where(f => f.Picker == "ioProviders")) {
                if (SettingsAccessor.Describe(elementType, field.Path).Optional) continue;
                var current = SettingsAccessor.Read(settings, prefix + field.Path)?.GetValue<string>();
                if (!string.IsNullOrEmpty(current) && current != Guid.Empty.ToString()) continue;
                if (settings.IoDatabase is Guid database && database != Guid.Empty) {
                    SettingsAccessor.Write(settings, prefix + field.Path, JsonSerializer.SerializeToElement(database.ToString()));
                }
            }
            _server.UpdateWAFServerSettingsFile();
            return new { Added = id, Rejected = rejected, Settings = buildDatabase(payload.StoreId) };
        }
    }

    object listRemove(ListItemPayload payload) {
        lock (_saveLock) {
            var settings = getContainer(payload.StoreId).Settings;
            var list = findList(payload.Path);
            requireUnlockedList(list, payload.StoreId);
            var blocking = describeUsage(list.Path, payload.Id, payload.StoreId).Blocking;
            if (blocking.Length > 0) {
                throw new Exception("This " + list.ItemName + " is still used as " + string.Join(", ", blocking)
                    + ". Point those somewhere else first. ");
            }
            var (owner, property) = arrayProperty(settings, list.Path, create: false);
            var before = owner == null ? [] : existing(owner, property);
            var after = before.Where(item => idOf(item) != payload.Id).ToArray();
            if (after.Length == before.Length) throw new Exception("That " + list.ItemName + " is already gone. ");
            property.SetValue(owner, toArray(property.PropertyType.GetElementType()!, after));
            if (string.Equals(list.Path, "IOSettings", StringComparison.OrdinalIgnoreCase)) _server.ForgetIOProvider(payload.Id);
            _server.UpdateWAFServerSettingsFile();
            return new { Removed = payload.Id, Settings = buildDatabase(payload.StoreId) };
        }
    }

    /// <summary>
    /// The object holding a list and the array property on it. A list path may reach into a nested
    /// settings object ("LocalSettings.ValueIndexes"); with <paramref name="create"/> the objects on
    /// the way are made when missing - adding the first engine to a database whose LocalSettings is
    /// null - and without it a missing one comes back as a null owner, meaning an empty list.
    /// </summary>
    static (object? Owner, PropertyInfo Property) arrayProperty(object root, string path, bool create) {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
        var segments = path.Split('.');
        object? owner = root;
        var type = root.GetType();
        for (var i = 0; i < segments.Length - 1; i++) {
            var step = type.GetProperty(segments[i], flags) ?? throw new Exception("\"" + path + "\" is not an editable list.");
            var next = owner == null ? null : step.GetValue(owner);
            if (next == null && create && owner != null) {
                next = Activator.CreateInstance(step.PropertyType) ?? throw new Exception("Could not create " + step.Name + ".");
                step.SetValue(owner, next);
            }
            owner = next;
            type = step.PropertyType;
        }
        var property = type.GetProperty(segments[^1], flags);
        if (property?.PropertyType.IsArray != true) throw new Exception("\"" + path + "\" is not an editable list.");
        return (owner, property);
    }
    static object[] existing(object owner, PropertyInfo property) =>
        ((System.Collections.IEnumerable?)property.GetValue(owner))?.Cast<object>().ToArray() ?? [];
    static Array toArray(Type elementType, object[] items) {
        var array = Array.CreateInstance(elementType, items.Length);
        for (var i = 0; i < items.Length; i++) array.SetValue(items[i], i);
        return array;
    }
    static void append(object owner, PropertyInfo property, object item) {
        property.SetValue(owner, toArray(property.PropertyType.GetElementType()!, [.. existing(owner, property), item]));
    }

    static SettingListDefinition findList(string path) {
        var list = SettingsCatalog.Database.SelectMany(s => s.Groups).Select(g => g.List)
            .FirstOrDefault(l => l != null && string.Equals(l.Path, path, StringComparison.OrdinalIgnoreCase));
        return list ?? throw new Exception("\"" + path + "\" is not an editable list. ");
    }

    void requireUnlockedList(SettingListDefinition list, Guid containerId) {
        var overlay = _server.ConfigurationOverlay;
        if (overlay != null && overlay.IsOverridden(SettingsOverlay.OverridePath(containerId, list.Path), out _)) {
            throw new Exception("The " + list.ItemName + " list is set by the " + overlay.SectionName
                + " configuration section and cannot be changed here. ");
        }
    }

    sealed record ApplyResult(List<string> Changed, List<RejectedSetting> Rejected);

    ApplyResult apply(SettingSectionDefinition[] catalog, object root, Dictionary<string, JsonElement>? values, Guid? containerId) {
        var changed = new List<string>();
        var rejected = new List<RejectedSetting>();
        if (values == null || values.Count == 0) return new ApplyResult(changed, rejected);
        var groups = catalog.SelectMany(s => s.Groups).ToArray();
        var byPath = groups.SelectMany(g => g.Settings).ToDictionary(s => s.Path, StringComparer.OrdinalIgnoreCase);
        var lists = groups.Select(g => g.List).OfType<SettingListDefinition>().ToArray();
        var overlay = _server.ConfigurationOverlay;
        foreach (var (path, value) in values) {
            var definition = byPath.GetValueOrDefault(path) ?? listField(lists, path);
            if (definition == null) {
                rejected.Add(new RejectedSetting(path, "Not an editable setting."));
                continue;
            }
            if (definition.ReadOnly) {
                rejected.Add(new RejectedSetting(path, definition.Label + " cannot be changed here."));
                continue;
            }
            // writing an overridden setting would be silently undone: the value on disk is restored
            // before saving, and configuration is merged back over it at the next start
            if (overlay != null && overlay.IsOverridden(SettingsOverlay.OverridePath(containerId, path), out _)) {
                rejected.Add(new RejectedSetting(path, definition.Label + " is set by the " + overlay.SectionName + " configuration section and cannot be changed here."));
                continue;
            }
            try {
                if (SettingsAccessor.Write(root, path, value)) changed.Add(path);
            } catch (Exception error) {
                rejected.Add(new RejectedSetting(path, error.Message));
            }
        }
        return new ApplyResult(changed, rejected);
    }

    /// <summary>Matches "IOSettings[8f1c...].Path" back to the field it came from, so an element's
    /// fields are validated against the catalog exactly like a top level setting.</summary>
    static SettingDefinition? listField(SettingListDefinition[] lists, string path) {
        var open = path.IndexOf('[');
        var close = path.IndexOf("].", StringComparison.Ordinal);
        if (open <= 0 || close < open) return null;
        var listPath = path[..open];
        var field = path[(close + 2)..];
        var list = lists.FirstOrDefault(l => string.Equals(l.Path, listPath, StringComparison.OrdinalIgnoreCase));
        return list?.Fields.FirstOrDefault(f => string.Equals(f.Path, field, StringComparison.OrdinalIgnoreCase));
    }

    NodeStoreContainer getContainer(Guid storeId) {
        if (!_server.Containers.TryGetValue(storeId, out var container)) throw new Exception("Database not found. ");
        return container;
    }
}

sealed record RejectedSetting(string Path, string Reason);
sealed record DatabaseSettingsPayload(Guid StoreId);
sealed record SaveServerSettingsPayload(Dictionary<string, JsonElement>? Values);
sealed record SaveDatabaseSettingsPayload(Guid StoreId, Dictionary<string, JsonElement>? Values, bool Reopen);
sealed record ListAddPayload(Guid StoreId, string Path, Dictionary<string, JsonElement>? Values);
sealed record ListItemPayload(Guid StoreId, string Path, Guid Id);
