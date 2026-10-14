using Relatude.DB.NodeServer.Settings;
using System.Text.Json.Nodes;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// The warning at the top of both settings pages: the secrets relatude.db.json holds (see
/// <see cref="SettingsSecrets"/>). Each page lists its own - the server's on the server page, one
/// database's on that database's page - and names the pages the file holds others for, since it is one
/// file and one fix.
///
/// <para>For each secret the page is told where it is shown, whether it is in force from somewhere
/// else already (then the file's copy only needs deleting), and the configuration key that would carry
/// it, with the ids that tie it to its list elements. Never its value.</para>
/// </summary>
sealed partial class UISettings {
    object? secretsInSettingsFile(Guid? storeId) {
        var file = _server.ReadSettingsFileJson();
        if (file == null) return null;
        var found = SettingsSecrets.In(file);
        if (found.Count == 0) return null;
        var section = _server.ConfigurationSectionName;
        return new {
            File = _server.SettingsFileDisplay,
            ConfigSection = section,
            Items = found.Where(f => f.ContainerId == storeId).Select(f => new {
                f.Path,
                f.Definition.Label,
                SectionId = f.Section.Id,
                GroupId = f.Group.Id,
                Where = f.ItemLabel != null ? f.Group.Title + " › " + f.ItemLabel : f.Section.Title + " › " + f.Group.Title,
                Unused = unused(f),
                ConfigKey = section == null ? null : section + ":" + f.ConfigPath,
                ConfigIds = section == null ? [] : f.ConfigIds.Select(id => new { Key = section + ":" + id.Path, id.Value }).ToArray(),
            }).ToArray(),
            Elsewhere = found.Where(f => f.ContainerId != storeId).GroupBy(f => f.ContainerId).Select(g => {
                var first = g.First();
                return new {
                    Scope = g.Key == null ? "server" : "database",
                    StoreId = g.Key,
                    Title = g.Key is Guid id ? databaseTitle(file, id) : "the server",
                    Count = g.Count(),
                    // where a link to that page lands: its first secret
                    first.Path,
                    SectionId = first.Section.Id,
                    GroupId = first.Group.Id,
                    // a database the file has and this server does not - removed on this server - has no page to open
                    CanOpen = g.Key is not Guid storeOf || _server.Containers.ContainsKey(storeOf),
                };
            }).ToArray(),
        };
    }

    /// <summary>Why the file's copy is not used, when it is not: the value in force comes from somewhere
    /// else, and deleting the copy is the whole fix. Null when the file's value is the one in force.</summary>
    string? unused(SettingsSecrets.Found found) {
        var path = SettingsOverlay.OverridePath(found.ContainerId, found.Path);
        var decidedBy = _server.DecidedOutsideTheSettingsFiles(path);
        if (decidedBy != null) return "the value in force comes from " + decidedBy;
        var overrides = _server.OverridesFile;
        if (overrides == null || overrides.Unavailable != null) return null;
        if (overrides.ValueEntryFor(path) != null) return "the value in force comes from this server's overrides";
        if (overrides.Entries.Any(e => e.Kind == SettingsPatch.EntryKind.Removed && SettingsPatch.IsAtOrBelow(path, e.Path))) {
            return "its " + (found.Group.List?.ItemName ?? "element") + " is removed on this server";
        }
        return null;
    }

    static string databaseTitle(JsonObject file, Guid id) {
        var name = SettingsPatch.Read(file, SettingsOverlay.OverridePath(id, "Name")) is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
        return "database \"" + (string.IsNullOrWhiteSpace(name) ? id.ToString() : name) + "\"";
    }
}
