using Relatude.DB.IO;
using Relatude.DB.Logging;
using Relatude.DB.NodeServer.Settings;

namespace Relatude.DB.NodeServer;

/// <summary>
/// Where the definitions of each database's custom logs are kept: relatude.settings/logs/ for the
/// database without a short name, relatude.settings/{short name}/logs/ for the others
/// (<see cref="DatabaseShortName"/>), one {log key}.json per log, with the application's other settings
/// - so they are deployed, and kept in source control, with the application. What the logs record stays
/// in the database's log storage, one folder per log.
/// </summary>
public partial class RelatudeDBServer {
    // one folder per database id, built once: a disk provider registers itself for good when it is
    // created, so a logger built at every open must not build a new one each time
    readonly Dictionary<Guid, (string? ShortName, LogDefinitionFolder Folder)> _logDefinitionFolders = [];

    /// <summary>The folder on disk holding the log definitions of the database with this short name (null for none).</summary>
    internal string LogDefinitionsFolderPath(string? shortName) => Path.Combine([_rootDataFolderPath, .. DatabaseShortName.LogDefinitionsFolder(shortName).Split('/')]);

    /// <summary>
    /// The folder the definitions of one database's custom logs are kept in. A short name changed while
    /// the server runs - on the settings page, or by taking such a change back - takes the folder along
    /// the next time the database's logger is built: it is moved, unless the new place already has
    /// definitions or another database still has the old name.
    /// </summary>
    internal LogDefinitionFolder LogDefinitionsFor(NodeStoreContainerSettingsBase settings) {
        var shortName = DatabaseShortName.Of(settings);
        lock (_logDefinitionFolders) {
            if (_logDefinitionFolders.TryGetValue(settings.Id, out var known)) {
                if (string.Equals(known.ShortName, shortName, StringComparison.Ordinal)) return known.Folder;
                followShortName(settings, known.ShortName, shortName);
            } else {
                moveFromEarlierLayout(settings, shortName);
            }
            var display = DatabaseShortName.LogDefinitionsFolder(shortName);
            var folder = new LogDefinitionFolder(new IOProviderDisk(LogDefinitionsFolderPath(shortName), plainFolder: true), [], display);
            _logDefinitionFolders[settings.Id] = (shortName, folder);
            return folder;
        }
    }

    void followShortName(NodeStoreContainerSettingsBase settings, string? before, string? after) {
        var from = LogDefinitionsFolderPath(before);
        var to = LogDefinitionsFolderPath(after);
        // only the case changed: the same folder where names are compared without case, and nothing to
        // move where they are not - the definitions are then read from the folder of the new spelling
        if (DatabaseShortName.Same(before, after) || !hasDefinitions(from)) return;
        var sharing = GetContainers().FirstOrDefault(c => c.Settings.Id != settings.Id && DatabaseShortName.Same(DatabaseShortName.Of(c.Settings), before));
        if (sharing != null) {
            Log("The log definitions in " + DatabaseShortName.LogDefinitionsFolder(before) + " stay where they are: the database \""
                + nameOf(sharing.Settings) + "\" has the short name " + DatabaseShortName.Describe(before) + ".");
            return;
        }
        moveDefinitions(settings, from, to, DatabaseShortName.LogDefinitionsFolder(before), DatabaseShortName.LogDefinitionsFolder(after));
        // a database's settings folder left with nothing in it goes too: relatude.settings/{old short name}
        if (before != null) removeIfEmpty(Path.Combine([_rootDataFolderPath, .. DatabaseShortName.SettingsFolder(before).Split('/')]));
    }

    // For a few days of 2026-10 the definitions were kept in relatude.settings/logs/{short name}/, with
    // "db" for a database without one. What is found there is moved to where it is kept now, once.
    void moveFromEarlierLayout(NodeStoreContainerSettingsBase settings, string? shortName) {
        var earlier = Path.Combine([_rootDataFolderPath, .. Defaults.SettingsFolderPath.Split('/'), DatabaseShortName.LogsFolderName, shortName ?? "db"]);
        if (!hasDefinitions(earlier)) return;
        moveDefinitions(settings, earlier, LogDefinitionsFolderPath(shortName),
            Defaults.SettingsFolderPath + "/" + DatabaseShortName.LogsFolderName + "/" + (shortName ?? "db"), DatabaseShortName.LogDefinitionsFolder(shortName));
    }

    // Every definition file from one folder into another, one at a time: the folders can be nested (the
    // earlier relatude.settings/logs/db sits inside relatude.settings/logs), and a definition already in
    // the new place is never overwritten. The old folder goes once nothing is left in it.
    void moveDefinitions(NodeStoreContainerSettingsBase settings, string from, string to, string fromDisplay, string toDisplay) {
        var moved = 0;
        var left = new List<string>();
        try {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from, "*.json")) {
                var target = Path.Combine(to, Path.GetFileName(file));
                if (File.Exists(target)) {
                    left.Add(Path.GetFileName(file));
                    continue;
                }
                File.Move(file, target);
                moved++;
            }
        } catch (Exception err) {
            startupWarning("Could not move the log definitions of the database \"" + nameOf(settings) + "\" from " + fromDisplay + " to " + toDisplay
                + " (" + err.Message + "). Move them by hand.");
            return;
        }
        if (moved > 0) Log("Moved the log definitions of the database \"" + nameOf(settings) + "\" from " + fromDisplay + " to " + toDisplay + ".");
        if (left.Count > 0) {
            startupWarning("The log definitions " + string.Join(", ", left) + " of the database \"" + nameOf(settings) + "\" are left in " + fromDisplay
                + ": " + toDisplay + " already has definitions with those names, and only those are read.");
        }
        removeIfEmpty(from);
    }

    static bool hasDefinitions(string folder) => Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.json").Any();
    static void removeIfEmpty(string folder) {
        try {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        } catch {
            // an empty folder left behind is harmless
        }
    }

    /// <summary>
    /// Says at start what is wrong with the databases' short names: one that cannot name a folder (the
    /// database is taken to have none), and two databases with the same one - or both without one -
    /// which then share their settings folder: a log defined in one is defined in the other too.
    /// </summary>
    void warnAboutShortNames() {
        var databases = GetContainers().Select(c => c.Settings).ToArray();
        foreach (var settings in databases) {
            var problem = DatabaseShortName.Problem(settings.ShortName);
            if (problem != null) {
                startupWarning("The short name \"" + settings.ShortName + "\" of the database \"" + nameOf(settings) + "\" cannot be used, and the database is taken to have none: " + problem);
            }
        }
        foreach (var group in databases.GroupBy(s => DatabaseShortName.Of(s) ?? "", StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)) {
            var names = string.Join(" and ", group.Select(s => "\"" + nameOf(s) + "\""));
            var shortName = group.Key.Length == 0 ? null : group.Key;
            startupWarning((shortName == null ? "The databases " + names + " have no short name" : "The databases " + names + " have the same short name, " + DatabaseShortName.Describe(shortName))
                + ", so they share " + DatabaseShortName.LogDefinitionsFolder(shortName) + "/: a log defined in one is defined in the others too. "
                + "Give each its own short name in its settings - only one database can be without one.");
        }
        // the files every installation shares are found by the short name, so one saved for this
        // installation alone has it read other ones than the rest
        foreach (var settings in databases) {
            if (_overridesFile?.ValueEntryFor(SettingsOverlay.OverridePath(settings.Id, nameof(settings.ShortName))) == null) continue;
            startupWarning("The short name of the database \"" + nameOf(settings) + "\" is only this installation's: it is in " + _overridesFile.Display
                + ", not in " + Defaults.SettingsFileName + ". So this installation reads its shared settings files from " + DatabaseShortName.SettingsFolder(DatabaseShortName.Of(settings))
                + ", and the others from where " + Defaults.SettingsFileName + " says. Move it into " + Defaults.SettingsFileName + " (Settings, Overrides), or set it back.");
        }
    }

    static string nameOf(NodeStoreContainerSettingsBase settings) => string.IsNullOrEmpty(settings.Name) ? settings.Id.ToString() : settings.Name;
}
