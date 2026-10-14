using System.Text.Json.Nodes;

namespace Relatude.DB.NodeServer.Settings;

/// <summary>
/// The secrets relatude.db.json holds: every setting the catalog marks <see cref="SettingDefinition.Secret"/>
/// or <see cref="SettingDefinition.Sensitive"/> that has a value in the file itself.
///
/// <para>That file is the application's own description of itself - written by a developer, usually
/// committed to source control and deployed with the application - so a secret in it is readable by
/// whoever can read the repository. Configuration is where secrets belong: user secrets on a developer's
/// machine, environment variables or a vault on a server. Values from there are never written to either
/// settings file.</para>
///
/// <para>Only the file is looked at, never the settings in force: a secret that configuration or this
/// server's overrides already supply is still exposed while the file keeps its own copy.</para>
/// </summary>
public static class SettingsSecrets {
    /// <summary>
    /// One secret the file holds - never its value.
    /// </summary>
    /// <param name="ContainerId">The database it belongs to; null for a setting of the server.</param>
    /// <param name="Path">The setting's path as its settings page names it, relative to the server or the
    /// database: <c>ApiKey</c>, <c>IOSettings[9d8c...].BlobConnectionString</c>.</param>
    /// <param name="Definition">The setting's catalog entry.</param>
    /// <param name="Section">The section of the settings page it is shown in.</param>
    /// <param name="Group">The group it is shown in: for a field of a list element, the list's group.</param>
    /// <param name="ItemLabel">The name of the list element it is a field of, when it is one and the file names it.</param>
    /// <param name="ConfigPath">The configuration key it would have below the section name, with the
    /// positions of the lists it is in as they are in the file: <c>ContainerSettings:0:IOSettings:1:BlobConnectionString</c>.</param>
    /// <param name="ConfigIds">The ids to give beside it, so that configuration matches each list element by
    /// id rather than by position and a reordered file never sends a key to the wrong element.</param>
    public sealed record Found(Guid? ContainerId, string Path, SettingDefinition Definition, SettingSectionDefinition Section,
        SettingGroupDefinition Group, string? ItemLabel, string ConfigPath, ConfigId[] ConfigIds);

    /// <summary>A configuration key below the section name and the value it must have.</summary>
    public sealed record ConfigId(string Path, string Value);

    /// <summary>Every secret <paramref name="file"/> holds, server settings first and then each database's,
    /// in catalog order. <paramref name="file"/> is relatude.db.json in the form the settings serialize to.</summary>
    public static List<Found> In(JsonObject file) {
        var found = new List<Found>();
        look(SettingsCatalog.Server, file, null, "", [], found);
        if (file["ContainerSettings"] is JsonArray containers) {
            for (var i = 0; i < containers.Count; i++) {
                if (containers[i] is not JsonObject container || idOf(container) is not Guid id) continue;
                var at = "ContainerSettings:" + i + ":";
                look(SettingsCatalog.Database, container, id, at, [new ConfigId(at + "Id", id.ToString())], found);
            }
        }
        return found;
    }

    /// <summary>True for a setting the settings pages warn about while relatude.db.json holds it.</summary>
    public static bool IsSecret(SettingDefinition definition) => definition.Secret || definition.Sensitive;

    static void look(SettingSectionDefinition[] catalog, JsonObject root, Guid? containerId, string configAt, ConfigId[] ids, List<Found> found) {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in catalog) {
            foreach (var group in section.Groups) {
                foreach (var definition in group.Settings) {
                    if (!IsSecret(definition) || !holdsValue(SettingsPatch.Read(root, definition.Path)) || !seen.Add(definition.Path)) continue;
                    found.Add(new Found(containerId, definition.Path, definition, section, group, null, configAt + configKey(definition.Path), ids));
                }
                var list = group.List;
                if (list == null) continue;
                // a field may be listed twice for different kinds of element; it is one value all the same
                var fields = list.Fields.Where(IsSecret).DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
                if (fields.Length == 0 || SettingsPatch.Read(root, list.Path) is not JsonArray items) continue;
                for (var j = 0; j < items.Count; j++) {
                    if (items[j] is not JsonObject item || idOf(item) is not Guid itemId) continue;
                    var itemAt = configAt + configKey(list.Path) + ":" + j + ":";
                    ConfigId[] itemIds = [.. ids, new ConfigId(itemAt + "Id", itemId.ToString())];
                    var label = SettingsPatch.Read(item, list.LabelField) is JsonValue named && named.TryGetValue<string>(out var name) && !string.IsNullOrWhiteSpace(name) ? name : null;
                    foreach (var field in fields) {
                        if (!holdsValue(SettingsPatch.Read(item, field.Path))) continue;
                        found.Add(new Found(containerId, list.Path + "[" + itemId + "]." + field.Path, field, section, group, label, itemAt + configKey(field.Path), itemIds));
                    }
                }
            }
        }
    }

    // an empty string is no secret, and neither is the empty guid an unset key serializes to
    static bool holdsValue(JsonNode? value) {
        if (value == null) return false;
        if (value is not JsonValue v || !v.TryGetValue<string>(out var text)) return true;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return !(Guid.TryParse(text, out var guid) && guid == Guid.Empty);
    }

    static string configKey(string propertyPath) => propertyPath.Replace('.', ':');

    static Guid? idOf(JsonObject element)
        => element["Id"] is JsonValue v && v.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id) ? id : null;
}
