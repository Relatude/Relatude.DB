using Relatude.DB.NodeServer.Settings;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// The tabs of the custom settings the application registers (see <see cref="ICustomSettingsDefinition"/>):
/// one per set, beside the server settings for a set of the server's and beside a database's settings for
/// a set per database. A tab is the same page the server and database settings are - the same sections,
/// groups, editors and marks - read from the definition's groups and the set's own layers, so a setting
/// there says where its value comes from the same way: SHARED (<c>{FileKey}.json</c>), THIS SERVER
/// (<c>{FileKey}.overrides.json</c>), APPSETTINGS or CODE, and is locked when one of the last two decides it.
/// The set's THIS SERVER file is its own, so the tab has its own list of what it holds, to move into
/// SHARED or discard.
/// </summary>
sealed partial class UISettings {

    void registerCustom(UICommands commands) {
        commands.Register("settings-custom-get", ctx => buildCustom(customSet(ctx.Payload<CustomSettingsPayload>())));
        commands.Register("settings-custom-save", ctx => saveCustom(ctx.Payload<SaveCustomSettingsPayload>()));
        commands.Register("settings-custom-list-add", ctx => customListAdd(ctx.Payload<CustomListAddPayload>()));
        commands.Register("settings-custom-list-remove", ctx => customListRemove(ctx.Payload<CustomListItemPayload>()));
        commands.Register("settings-custom-overrides-get", ctx => buildCustomOverrides(customSet(ctx.Payload<CustomSettingsPayload>())));
        commands.Register("settings-custom-overrides-move", ctx => moveCustomOverrides(ctx.Payload<CustomOverridesPathsPayload>()));
        commands.Register("settings-custom-overrides-discard", ctx => discardCustomOverrides(ctx.Payload<CustomOverridesPathsPayload>()));
    }

    /// <summary>The tabs to add to the navigation, for the server info the admin UI starts with.</summary>
    internal static object[] CustomSettingsTabs(RelatudeDBServer server) => [.. server.CustomSettings.Definitions.Select(d => (object)new {
        Key = d.FileKey,
        d.Title,
        Scope = d.Scope == CustomSettingsScope.Server ? "server" : "database",
        d.Icon,
    })];

    CustomSettingsSet customSet(CustomSettingsPayload payload) {
        var definition = _server.CustomSettings.Find(payload.Key) ?? throw new Exception("There are no custom settings \"" + payload.Key + "\". ");
        return _server.CustomSettings.SetFor(definition, payload.StoreId);
    }

    // ---- the page ----

    object buildCustom(CustomSettingsSet set) {
        var definition = set.Definition;
        if (set.Error != null) throw new Exception(set.Error);
        var root = set.Value;
        var type = definition.SettingsType;
        var groups = definition.Groups;
        var database = set.StoreId is Guid id ? getContainer(id).Settings : null;
        return new {
            Scope = "custom",
            StoreId = set.StoreId,
            Title = database == null ? definition.Title : definition.Title + " · " + (string.IsNullOrEmpty(database.Name) ? database.Id.ToString() : database.Name),
            CustomKey = definition.FileKey,
            CustomScope = definition.Scope == CustomSettingsScope.Server ? "server" : "database",
            definition.Description,
            SettingsFile = set.SettingsFileDisplay,
            ConfigSection = customConfigSection(set),
            Overrides = set.HasDataFile ? new { File = set.DataFileDisplay, Count = set.Entries.Count, Error = set.DataUnavailable } : null,
            SecretsInFile = customSecretsInFile(set),
            Sections = new[] {
                new {
                    Id = definition.FileKey,
                    definition.Title,
                    definition.Icon,
                    Groups = groups.Select(group => new {
                        group.Id,
                        group.Title,
                        group.Help,
                        Settings = group.Settings.Select(s => customSetting(s, set, type, root, "")),
                        List = group.List == null ? null : buildCustomList(group.List, set, type, root),
                    }),
                },
            },
            Pickers = new {
                Databases = _server.GetContainers().Select(c => new {
                    Value = c.Settings.Id.ToString(),
                    Label = string.IsNullOrEmpty(c.Settings.Name) ? c.Settings.Id.ToString() : c.Settings.Name,
                }),
                Cultures = cultures(),
            },
        };
    }

    object customSetting(SettingDefinition definition, CustomSettingsSet set, Type type, object root, string prefix) {
        var path = prefix + definition.Path;
        var overridden = set.IsConfigured(path, out var configured);
        var codeSet = !overridden && set.IsSetByCode(path);
        var inOverrides = set.HasDataFile && set.ValueEntryFor(path) != null;
        var fileValue = inOverrides ? set.FileValue(path) : null;
        return settingView(definition, type, root, prefix, new SettingMarks(overridden, configured, codeSet, inOverrides, fileValue));
    }

    object buildCustomList(SettingListDefinition list, CustomSettingsSet set, Type type, object root) {
        var locked = set.DecidedOutsideTheFiles(list.Path) != null;
        return new {
            list.Path,
            list.ItemName,
            list.LabelField,
            list.EmptyHelp,
            Locked = locked,
            RemovedHere = set.HasDataFile
                ? set.RemovedFrom(list.Path).Select(e => elementLabel(list, set.FileValue(e.Path) as JsonObject, e.Path)).ToArray()
                : [],
            Items = elements(root, list.Path).Select(item => {
                var id = idOf(item);
                var prefix = list.Path + "[" + id + "].";
                return new {
                    Id = id,
                    Settings = list.Fields.Select(field => customSetting(field, set, type, root, prefix)),
                    UsedBy = Array.Empty<string>(),
                    Removable = !locked,
                    Blocking = Array.Empty<string>(),
                    RemoveWarning = (string?)null,
                    AddedHere = set.HasDataFile && (set.IsAdded(list.Path + "[" + id + "]") || set.ValueEntryFor(list.Path) != null),
                };
            }),
        };
    }

    /// <summary>The configuration section a value of this set would be put in: the one for every database,
    /// or for a set per database on a server with more than one, the one for this database.</summary>
    string? customConfigSection(CustomSettingsSet set) {
        var section = _server.ConfigurationSectionName;
        if (section == null) return null;
        var root = section + ":" + CustomSettingsCatalog.ConfigurationKey + ":";
        if (set.StoreId is not Guid id || _server.Containers.Count < 2) return root + set.Definition.FileKey;
        var database = getContainer(id).Settings;
        return root + CustomSettingsCatalog.DatabasesConfigurationKey + ":" + (DatabaseShortName.Of(database) ?? database.Id.ToString()) + ":" + set.Definition.FileKey;
    }

    /// <summary>The secrets SHARED holds for this set, the way <see cref="secretsInSettingsFile"/> lists those of relatude.db.json.</summary>
    object? customSecretsInFile(CustomSettingsSet set) {
        var file = set.ReadSettingsFileJson();
        if (file == null) return null;
        var section = new SettingSectionDefinition { Id = set.Definition.FileKey, Title = set.Definition.Title, Icon = set.Definition.Icon, Groups = set.Definition.Groups };
        var found = new List<SettingsSecrets.Found>();
        var configSection = customConfigSection(set);
        // the key below the section name, "CustomSettings:webshop:", that the secret's own key is put after
        var at = configSection == null ? "" : configSection[(configSection.IndexOf(':') + 1)..] + ":";
        SettingsSecrets.Look([section], file, set.StoreId, at, [], found);
        if (found.Count == 0) return null;
        var sectionName = _server.ConfigurationSectionName;
        return new {
            File = set.SettingsFileDisplay,
            ConfigSection = sectionName,
            Items = found.Select(f => new {
                f.Path,
                f.Definition.Label,
                SectionId = f.Section.Id,
                GroupId = f.Group.Id,
                Where = f.ItemLabel != null ? f.Group.Title + " › " + f.ItemLabel : f.Section.Title + " › " + f.Group.Title,
                Unused = set.DecidedOutsideTheFiles(f.Path) is { } decidedBy ? "the value in force comes from " + decidedBy
                    : set.HasDataFile && set.ValueEntryFor(f.Path) != null ? "the value in force comes from this server's overrides" : null,
                ConfigKey = sectionName == null ? null : sectionName + ":" + f.ConfigPath,
                ConfigIds = sectionName == null ? [] : f.ConfigIds.Select(c => new { Key = sectionName + ":" + c.Path, c.Value }).ToArray(),
            }).ToArray(),
            Elsewhere = Array.Empty<object>(),
        };
    }

    // ---- saving ----

    object saveCustom(SaveCustomSettingsPayload payload) {
        lock (_saveLock) {
            var set = customSet(new(payload.Key, payload.StoreId));
            var copy = set.Copy();
            var result = applyCustom(set, copy, payload.Values);
            if (result.Changed.Count > 0) set.Save(copy);
            return new { result.Changed, result.Rejected, Reopened = false, Settings = buildCustom(set) };
        }
    }

    /// <summary>The values posted, written to <paramref name="copy"/> with the checks the other settings
    /// pages make: a path the definition shows, not read only, and not decided by configuration or code.</summary>
    ApplyResult applyCustom(CustomSettingsSet set, object copy, Dictionary<string, JsonElement>? values) {
        var changed = new List<string>();
        var rejected = new List<RejectedSetting>();
        if (values == null || values.Count == 0) return new ApplyResult(changed, rejected);
        var groups = set.Definition.Groups;
        var byPath = groups.SelectMany(g => g.Settings).DistinctBy(s => s.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(s => s.Path, StringComparer.OrdinalIgnoreCase);
        var lists = groups.Select(g => g.List).OfType<SettingListDefinition>().ToArray();
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
            var decidedBy = set.DecidedOutsideTheFiles(path);
            if (decidedBy != null) {
                rejected.Add(new RejectedSetting(path, definition.Label + " is set by " + decidedBy + " and cannot be changed here."));
                continue;
            }
            try {
                if (SettingsAccessor.Write(copy, path, value)) changed.Add(path);
            } catch (Exception error) {
                rejected.Add(new RejectedSetting(path, error.Message));
            }
        }
        return new ApplyResult(changed, rejected);
    }

    // ---- lists ----

    object customListAdd(CustomListAddPayload payload) {
        lock (_saveLock) {
            var set = customSet(new(payload.Key, payload.StoreId));
            var list = customList(set, payload.Path);
            var copy = set.Copy();
            var (owner, property) = arrayProperty(copy, list.Path, create: true);
            var elementType = property.PropertyType.GetElementType()!;
            var item = Activator.CreateInstance(elementType) ?? throw new Exception("Could not create a " + list.ItemName + ".");
            elementType.GetProperty("Id")!.SetValue(item, SecureGuid.New());
            append(owner!, property, item);
            var prefix = list.Path + "[" + idOf(item) + "].";
            var rejected = new List<RejectedSetting>();
            foreach (var (field, value) in list.NewItem ?? []) SettingsAccessor.Write(copy, prefix + field, JsonSerializer.SerializeToElement(value));
            foreach (var (field, value) in payload.Values ?? []) {
                var definition = list.Fields.FirstOrDefault(f => string.Equals(f.Path, field, StringComparison.OrdinalIgnoreCase));
                if (definition == null || definition.ReadOnly) {
                    rejected.Add(new RejectedSetting(field, "Not a field of a " + list.ItemName + "."));
                    continue;
                }
                try {
                    SettingsAccessor.Write(copy, prefix + field, value);
                } catch (Exception error) {
                    rejected.Add(new RejectedSetting(field, error.Message));
                }
            }
            set.Save(copy);
            return new { Added = idOf(item), Rejected = rejected, Settings = buildCustom(set) };
        }
    }

    object customListRemove(CustomListItemPayload payload) {
        lock (_saveLock) {
            var set = customSet(new(payload.Key, payload.StoreId));
            var list = customList(set, payload.Path);
            var copy = set.Copy();
            var (owner, property) = arrayProperty(copy, list.Path, create: false);
            var before = owner == null ? [] : existing(owner, property);
            var after = before.Where(item => idOf(item) != payload.Id).ToArray();
            if (after.Length == before.Length) throw new Exception("That " + list.ItemName + " is already gone. ");
            // the last element gone from a list SHARED does not have at all: back to no list, which is what it says
            var none = after.Length == 0 && set.FileValue(list.Path) == null;
            property.SetValue(owner, none ? null : toArray(property.PropertyType.GetElementType()!, after));
            set.Save(copy);
            return new { Removed = payload.Id, Settings = buildCustom(set) };
        }
    }

    static SettingListDefinition customList(CustomSettingsSet set, string path) {
        var list = set.Definition.Groups.Select(g => g.List).FirstOrDefault(l => l != null && string.Equals(l.Path, path, StringComparison.OrdinalIgnoreCase))
            ?? throw new Exception("\"" + path + "\" is not an editable list. ");
        var decidedBy = set.DecidedOutsideTheFiles(list.Path);
        if (decidedBy != null) throw new Exception("The " + list.ItemName + " list is set by " + decidedBy + " and cannot be changed here. ");
        return list;
    }

    // ---- THIS SERVER: the set's overrides file ----

    object buildCustomOverrides(CustomSettingsSet set) {
        var definition = set.Definition;
        if (!set.HasDataFile) {
            return new { Enabled = false, File = (string?)null, Error = (string?)null, SettingsFile = set.SettingsFileDisplay, ConfigSection = customConfigSection(set), Count = 0, Groups = Array.Empty<object>() };
        }
        var root = set.Error == null ? set.Value : null;
        var entries = set.Entries.Select(entry => describeCustomEntry(set, entry, root)).ToArray();
        var database = set.StoreId is Guid id && _server.Containers.TryGetValue(id, out var container) ? container.Settings : null;
        return new {
            Enabled = true,
            File = set.DataFileDisplay,
            Error = set.Error ?? set.DataUnavailable,
            SettingsFile = set.SettingsFileDisplay,
            ConfigSection = customConfigSection(set),
            Count = entries.Length,
            Groups = entries.Length == 0 ? [] : new object[] {
                new {
                    Scope = "custom",
                    set.StoreId,
                    Title = database == null ? definition.Title : definition.Title + " · " + (string.IsNullOrEmpty(database.Name) ? database.Id.ToString() : database.Name),
                    Entries = entries,
                },
            },
        };
    }

    object describeCustomEntry(CustomSettingsSet set, SettingsPatch.Entry entry, object? root) {
        var (label, where, secret) = describeCustomPath(set, entry.Path, root);
        var configured = set.IsConfigured(entry.Path, out _);
        var decidedBy = set.DecidedOutsideTheFiles(entry.Path);
        var fileValue = set.FileValue(entry.Path);
        object? value = null, before = null;
        string? summary = null;
        switch (entry.Kind) {
            case SettingsPatch.EntryKind.Value:
                value = secret ? null : redactCustom(set, entry.Value, entry.Path, root);
                before = secret ? null : redactCustom(set, fileValue, entry.Path, root);
                break;
            case SettingsPatch.EntryKind.Added:
                summary = "Added here";
                break;
            case SettingsPatch.EntryKind.Removed:
                summary = "Removed here, still in " + set.SettingsFileDisplay;
                break;
        }
        return new {
            entry.Path,
            Kind = entry.Kind.ToString().ToLowerInvariant(),
            Label = label,
            Where = where,
            Secret = secret,
            Value = value,
            HasValue = hasValue(entry.Value),
            FileValue = before,
            FileHasValue = hasValue(fileValue),
            Summary = summary,
            CanMove = !configured,
            MoveBlocked = configured ? "Set by " + decidedBy + ", which is never copied into " + set.SettingsFileDisplay + "." : null,
            CanDiscard = decidedBy == null,
            DiscardBlocked = decidedBy == null ? null : "Set by " + decidedBy + ", which decides it whichever file holds it.",
        };
    }

    // a value shown in the list, with every secret inside an object or list set whole blanked out: a nested
    // settings object SHARED did not have is saved whole, secrets and all
    JsonNode? redactCustom(CustomSettingsSet set, JsonNode? value, string path, object? root) {
        switch (value) {
            case JsonObject obj: {
                var result = new JsonObject();
                foreach (var (key, inner) in obj) result[key] = redactCustom(set, inner, path + "." + key, root);
                return result;
            }
            case JsonArray array: {
                var result = new JsonArray();
                foreach (var inner in array) {
                    var id = SettingsPatch.idOf(inner);
                    result.Add(redactCustom(set, inner, id == null ? path : path + "[" + id + "]", root));
                }
                return result;
            }
            default:
                if (!describeCustomPath(set, path, root).Secret) return value?.DeepClone();
                return hasValue(value) ? JsonValue.Create("••••••") : null;
        }
    }

    // what the list calls a path, read off the definition's groups
    (string Label, string? Where, bool Secret) describeCustomPath(CustomSettingsSet set, string path, object? root) {
        var title = set.Definition.Title;
        foreach (var group in set.Definition.Groups) {
            var where = title + " · " + group.Title;
            foreach (var definition in group.Settings) {
                if (string.Equals(definition.Path, path, StringComparison.OrdinalIgnoreCase)) return (definition.Label, where, definition.Secret);
            }
            if (group.List is not { } list || !path.StartsWith(list.Path + "[", StringComparison.OrdinalIgnoreCase)) continue;
            var close = path.IndexOf(']', list.Path.Length);
            if (close < 0) continue;
            var elementPath = path[..(close + 1)];
            var element = (root == null ? null : readElement(root, elementPath)) ?? set.FileValue(elementPath) as JsonObject;
            var item = list.ItemName + " \"" + elementLabel(list, element, elementPath) + "\"";
            if (close == path.Length - 1) return (char.ToUpperInvariant(item[0]) + item[1..], where, false);
            var field = path[(close + 2)..];
            var fieldDefinition = list.Fields.FirstOrDefault(f => string.Equals(f.Path, field, StringComparison.OrdinalIgnoreCase));
            return ((fieldDefinition?.Label ?? field) + " of " + item, where, fieldDefinition?.Secret == true);
        }
        foreach (var group in set.Definition.Groups) {
            if (group.Settings.Any(d => d.Path.StartsWith(path + ".", StringComparison.OrdinalIgnoreCase))) return (group.Title + " (all of it)", title, false);
        }
        return (SettingsPatch.Display(path), title, false);

        static JsonObject? readElement(object root, string elementPath) {
            try {
                return SettingsAccessor.Read(root, elementPath) as JsonObject;
            } catch {
                return null; // an element no longer there is named from SHARED
            }
        }
    }

    object moveCustomOverrides(CustomOverridesPathsPayload payload) {
        lock (_saveLock) {
            var set = customSet(new(payload.Key, payload.StoreId));
            var moved = set.MoveIntoSettingsFile(payload.Paths);
            return new { Moved = moved, Overrides = buildCustomOverrides(set) };
        }
    }

    /// <summary>Takes entries out of THIS SERVER by putting back what SHARED says - a value, a removed
    /// element, an added one taken away - and saving, like the discard of relatude.db.overrides.json.</summary>
    object discardCustomOverrides(CustomOverridesPathsPayload payload) {
        lock (_saveLock) {
            var set = customSet(new(payload.Key, payload.StoreId));
            var wanted = new HashSet<string>(payload.Paths ?? [], StringComparer.OrdinalIgnoreCase);
            var discarded = new List<string>();
            var rejected = new List<RejectedSetting>();
            var copy = set.Copy();
            var baseJson = set.Base;
            foreach (var entry in set.Entries.Where(e => wanted.Contains(e.Path)).ToArray()) {
                try {
                    var decidedBy = set.DecidedOutsideTheFiles(entry.Path);
                    if (decidedBy != null) throw new Exception("Set by " + decidedBy + ", which decides it whichever file holds it.");
                    switch (entry.Kind) {
                        case SettingsPatch.EntryKind.Value:
                            SettingsAccessor.Assign(copy, entry.Path, set.FileValue(entry.Path));
                            break;
                        case SettingsPatch.EntryKind.Added: {
                            var (listPath, id) = splitElement(entry.Path);
                            removeElement(copy, listPath, id);
                            break;
                        }
                        case SettingsPatch.EntryKind.Removed: {
                            var (listPath, _) = splitElement(entry.Path);
                            var element = set.FileValue(entry.Path) as JsonObject ?? throw new Exception(set.SettingsFileDisplay + " no longer has that element.");
                            insertElement(copy, listPath, element, SettingsPatch.Read(baseJson, listPath) as JsonArray);
                            break;
                        }
                    }
                    discarded.Add(entry.Path);
                } catch (Exception error) {
                    rejected.Add(new RejectedSetting(entry.Path, error.Message));
                }
            }
            if (discarded.Count > 0) set.Save(copy);
            return new { Discarded = discarded, Rejected = rejected, Overrides = buildCustomOverrides(set) };
        }
    }
}

sealed record CustomSettingsPayload(string Key, Guid? StoreId);
sealed record SaveCustomSettingsPayload(string Key, Guid? StoreId, Dictionary<string, JsonElement>? Values);
sealed record CustomListAddPayload(string Key, Guid? StoreId, string Path, Dictionary<string, JsonElement>? Values);
sealed record CustomListItemPayload(string Key, Guid? StoreId, string Path, Guid Id);
sealed record CustomOverridesPathsPayload(string Key, Guid? StoreId, string[]? Paths);
