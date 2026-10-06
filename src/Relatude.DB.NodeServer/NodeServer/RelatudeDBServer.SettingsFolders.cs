using Relatude.DB.IO;
using Relatude.DB.NodeServer.Settings;

namespace Relatude.DB.NodeServer;

/// <summary>
/// Where each database keeps what it is set up with, in two places (the same split as relatude.db.json and
/// relatude.db.overrides.json):
/// <list type="bullet">
/// <item>SETTINGS - its folder in relatude.settings, part of the application, kept in source control and
/// deployed to every installation: relatude.settings/ for the database without a short name,
/// relatude.settings/{short name}/ for the others (<see cref="DatabaseShortName"/>). It holds the
/// definitions of the custom logs (logs/), of the GraphQL endpoints (graphql/) and the datamodel overrides
/// (datamodel.json).</item>
/// <item>DATA - the overrides folder on the database's own storage, this installation's: what the admin UI
/// saves goes there (overrides/logs/, overrides/graphql/, overrides/datamodel.overrides.json), and is moved
/// into the SETTINGS folder from there.</item>
/// </list>
/// </summary>
public partial class RelatudeDBServer {
    // one SETTINGS folder per database id, built once: a disk provider registers itself for good when it is
    // created, so a logger built at every open must not build a new one each time. The short name it was
    // built for is kept with it, so a short name changed while the server runs takes the files along
    readonly Dictionary<Guid, (string? ShortName, IOProviderDisk Io)> _settingsFolders = [];
    string _environmentName = "Production";

    /// <summary>The hosting environment the application runs in (Development, Staging, Production...).</summary>
    public string EnvironmentName => _environmentName;
    /// <summary>
    /// Whether the application runs in the Development environment - typically from its project folder,
    /// where relatude.settings is the one in source control. Elsewhere it is usually a deployed copy that
    /// the next deployment replaces.
    /// </summary>
    public bool IsDevelopment => string.Equals(_environmentName, "Development", StringComparison.OrdinalIgnoreCase);

    /// <summary>The SETTINGS folder on disk of the database with this short name (null for none).</summary>
    internal string SettingsFolderPath(string? shortName) => Path.Combine([_rootDataFolderPath, .. DatabaseShortName.SettingsFolder(shortName).Split('/')]);

    /// <summary>
    /// The SETTINGS folder of a database, as a plain folder on disk, with where it is for people
    /// (relatude.settings/{short name}). A short name changed while the server runs takes the folder's
    /// files along the next time it is asked for: each is moved unless the new place already has one, and
    /// nothing moves while another database still has the old name.
    /// </summary>
    internal (IOProviderDisk Io, string Display) SettingsFolderFor(NodeStoreContainerSettingsBase settings) {
        var shortName = DatabaseShortName.Of(settings);
        lock (_settingsFolders) {
            if (_settingsFolders.TryGetValue(settings.Id, out var known)) {
                if (string.Equals(known.ShortName, shortName, StringComparison.Ordinal)) return (known.Io, DatabaseShortName.SettingsFolder(shortName));
                followShortName(settings, known.ShortName, shortName);
            }
            var io = new IOProviderDisk(SettingsFolderPath(shortName), plainFolder: true);
            _settingsFolders[settings.Id] = (shortName, io);
            return (io, DatabaseShortName.SettingsFolder(shortName));
        }
    }

    /// <summary>A folder of the database's DATA - below overrides/ on its primary storage provider - or null
    /// when it has none.</summary>
    internal DefinitionFolder? DataFolderFor(NodeStoreContainerSettingsBase settings, params string[] folder) {
        var ioId = settings is NodeStoreContainerSettings full ? full.IoDatabase : null;
        if (ioId == null || ioId == Guid.Empty) return null;
        IIOProvider? io;
        try {
            if (!TryGetIO(ioId.Value, out io)) return null;
        } catch {
            return null; // storage that cannot be reached: the open says so
        }
        string[] key = [FileKeyUtility.OverridesFolderName, .. folder];
        return new DefinitionFolder(io, key, DataDisplayOf(io, key));
    }
    /// <summary>A file or folder of a database's storage, for people: relative to the root data folder on
    /// local disk (relatude.data/overrides/logs), else the key in the storage.</summary>
    internal string DataDisplayOf(IIOProvider io, string[] key) {
        if (io is IOProviderDisk disk) {
            var full = Path.GetFullPath(Path.Combine([disk.BaseFolder, .. key]));
            var relative = Path.GetRelativePath(_rootDataFolderPath, full);
            return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? full : relative.Replace('\\', '/');
        }
        return key.AsKeyString();
    }

    /// <summary>The two folders of a database's custom log definitions: logs/ in its SETTINGS folder, and
    /// overrides/logs/ in its DATA. Null when the database has no storage for the DATA one.</summary>
    internal LayeredDefinitionFolders? LogDefinitionsFor(NodeStoreContainerSettingsBase settings) => layered(settings, DatabaseShortName.LogsFolderName);
    /// <summary>The two folders of a database's GraphQL endpoint definitions: graphql/ in its SETTINGS folder,
    /// and overrides/graphql/ in its DATA. Null when the database has no storage for the DATA one.</summary>
    internal LayeredDefinitionFolders? GraphQLDefinitionsFor(NodeStoreContainerSettingsBase settings) => layered(settings, DatabaseShortName.GraphQLFolderName);
    LayeredDefinitionFolders? layered(NodeStoreContainerSettingsBase settings, string name) {
        var data = DataFolderFor(settings, name);
        if (data == null) return null;
        var (io, display) = SettingsFolderFor(settings);
        return new LayeredDefinitionFolders(new DefinitionFolder(io, [name], display + "/" + name), data);
    }

    // the files a database's SETTINGS folder holds; its folder may be relatude.settings itself, which holds
    // relatude.db.json and the folders of the other databases too, so only these are ever moved
    static readonly string[] settingsFolderItems = [DatabaseShortName.LogsFolderName, DatabaseShortName.GraphQLFolderName];

    void followShortName(NodeStoreContainerSettingsBase settings, string? before, string? after) {
        // only the case changed: the same folder where names are compared without case, and nothing to
        // move where they are not - the files are then read from the folder of the new spelling
        if (DatabaseShortName.Same(before, after)) return;
        var from = SettingsFolderPath(before);
        var to = SettingsFolderPath(after);
        var sharing = GetContainers().FirstOrDefault(c => c.Settings.Id != settings.Id && DatabaseShortName.Same(DatabaseShortName.Of(c.Settings), before));
        if (sharing != null) {
            Log("The settings files in " + DatabaseShortName.SettingsFolder(before) + " stay where they are: the database \""
                + nameOf(sharing.Settings) + "\" has the short name " + DatabaseShortName.Describe(before) + ".");
            return;
        }
        var files = new List<(string From, string To)>();
        foreach (var item in settingsFolderItems) {
            var folder = Path.Combine(from, item);
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.GetFiles(folder, "*.json")) files.Add((file, Path.Combine(to, item, Path.GetFileName(file))));
        }
        var datamodel = Path.Combine(from, DatabaseShortName.DatamodelFileName);
        if (File.Exists(datamodel)) files.Add((datamodel, Path.Combine(to, DatabaseShortName.DatamodelFileName)));
        if (files.Count == 0) return;
        var moved = 0;
        var left = new List<string>();
        try {
            foreach (var (file, target) in files) {
                if (File.Exists(target)) {
                    left.Add(Path.GetRelativePath(from, file).Replace('\\', '/'));
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(file, target);
                moved++;
            }
        } catch (Exception err) {
            Log("Could not move the settings files of \"" + nameOf(settings) + "\" from " + DatabaseShortName.SettingsFolder(before) + " to "
                + DatabaseShortName.SettingsFolder(after) + " (" + err.Message + "). Move them by hand.");
            return;
        }
        if (moved > 0) Log("Moved the settings files of \"" + nameOf(settings) + "\" from " + DatabaseShortName.SettingsFolder(before) + " to " + DatabaseShortName.SettingsFolder(after) + ".");
        if (left.Count > 0) {
            Log("The settings files " + string.Join(", ", left) + " of \"" + nameOf(settings) + "\" are left in " + DatabaseShortName.SettingsFolder(before)
                + ": " + DatabaseShortName.SettingsFolder(after) + " already has files with those names, and only those are read.");
        }
        foreach (var item in settingsFolderItems) removeIfEmpty(Path.Combine(from, item));
        // a database's own folder left with nothing in it goes too: relatude.settings/{old short name}
        if (before != null) removeIfEmpty(from);
    }

    static void removeIfEmpty(string folder) {
        try {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        } catch {
            // an empty folder left behind is harmless
        }
    }

    /// <summary>
    /// Says at start what is wrong with the databases' short names: one that cannot name a folder (the
    /// database is taken to have none), two databases with the same one - or both without one - which then
    /// share their SETTINGS folder, and one saved for this installation alone, which has it read other
    /// SETTINGS files than the other installations.
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
                + ", so they share " + DatabaseShortName.SettingsFolder(shortName) + "/: a log or endpoint defined in one is defined in the others too. "
                + "Give each its own short name in its settings - only one database can be without one.");
        }
        foreach (var settings in databases) {
            if (_overridesFile?.ValueEntryFor(SettingsOverlay.OverridePath(settings.Id, nameof(settings.ShortName))) == null) continue;
            startupWarning("The short name of the database \"" + nameOf(settings) + "\" is only this installation's: it is in " + _overridesFile.Display
                + ", not in " + Defaults.SettingsFileName + ". So this installation reads its settings files from " + DatabaseShortName.SettingsFolder(DatabaseShortName.Of(settings))
                + ", and the others from where " + Defaults.SettingsFileName + " says. Move it into " + Defaults.SettingsFileName + " (Settings, Data), or set it back.");
        }
    }

    static string nameOf(NodeStoreContainerSettingsBase settings) => string.IsNullOrEmpty(settings.Name) ? settings.Id.ToString() : settings.Name;
}
