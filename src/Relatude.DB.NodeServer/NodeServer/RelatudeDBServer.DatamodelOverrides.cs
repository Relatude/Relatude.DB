using Relatude.DB.NodeServer.Settings;

namespace Relatude.DB.NodeServer;

/// <summary>
/// Where each database's shared datamodel overrides are kept: relatude.settings/datamodel.overrides.json
/// for the database without a short name, relatude.settings/{short name}/datamodel.overrides.json for the
/// others - with the application's other settings, so they are deployed, and kept in source control,
/// with it (see <see cref="DatamodelOverridesFile"/>).
/// </summary>
public partial class RelatudeDBServer {
    // the short name each database's shared overrides were last looked for under, so that a short name
    // changed while the server runs takes the file along, the way LogDefinitionsFor takes the log definitions
    readonly Dictionary<Guid, string?> _sharedOverridesShortNames = [];
    string _environmentName = "Production";

    /// <summary>The hosting environment the application runs in (Development, Staging, Production...).</summary>
    public string EnvironmentName => _environmentName;
    /// <summary>
    /// Whether the application runs in the Development environment - typically from its project folder,
    /// where relatude.settings is the one in source control. Elsewhere it is usually a deployed copy that
    /// the next deployment replaces.
    /// </summary>
    public bool IsDevelopment => string.Equals(_environmentName, "Development", StringComparison.OrdinalIgnoreCase);

    /// <summary>The shared datamodel overrides on disk of the database with this short name (null for none).</summary>
    internal string SharedDatamodelOverridesPath(string? shortName) => Path.Combine([_rootDataFolderPath, .. DatabaseShortName.DatamodelOverridesFile(shortName).Split('/')]);

    /// <summary>
    /// The file on disk holding the datamodel overrides every installation of one database shares. A short
    /// name changed while the server runs takes the file along the next time it is asked for: it is moved,
    /// unless the new place already has one or another database still has the old name. The first time
    /// a database is asked for, a file the setting DatamodelOverridesPath named (which is no longer used)
    /// is copied here.
    /// </summary>
    internal string SharedDatamodelOverridesPathFor(NodeStoreContainerSettings settings) {
        var shortName = DatabaseShortName.Of(settings);
        lock (_sharedOverridesShortNames) {
            if (_sharedOverridesShortNames.TryGetValue(settings.Id, out var known)) {
                if (!string.Equals(known, shortName, StringComparison.Ordinal)) followShortNameWithOverrides(settings, known, shortName);
            } else {
                copyFromOverridesPath(settings, shortName);
            }
            _sharedOverridesShortNames[settings.Id] = shortName;
        }
        return SharedDatamodelOverridesPath(shortName);
    }

    void followShortNameWithOverrides(NodeStoreContainerSettings settings, string? before, string? after) {
        if (DatabaseShortName.Same(before, after)) return; // the same file where names are compared without case
        var from = SharedDatamodelOverridesPath(before);
        var to = SharedDatamodelOverridesPath(after);
        var fromDisplay = DatabaseShortName.DatamodelOverridesFile(before);
        var toDisplay = DatabaseShortName.DatamodelOverridesFile(after);
        if (!File.Exists(from)) return;
        var sharing = GetContainers().FirstOrDefault(c => c.Settings.Id != settings.Id && DatabaseShortName.Same(DatabaseShortName.Of(c.Settings), before));
        if (sharing != null) {
            Log("The shared datamodel overrides in " + fromDisplay + " stay where they are: the database \"" + nameOf(sharing.Settings)
                + "\" has the short name " + DatabaseShortName.Describe(before) + ".");
            return;
        }
        if (File.Exists(to)) {
            Log("The shared datamodel overrides of \"" + nameOf(settings) + "\" in " + fromDisplay + " are left there: " + toDisplay
                + " already has some, and only those are read.");
            return;
        }
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to);
            Log("Moved the shared datamodel overrides of \"" + nameOf(settings) + "\" from " + fromDisplay + " to " + toDisplay + ".");
        } catch (Exception err) {
            Log("Could not move the shared datamodel overrides of \"" + nameOf(settings) + "\" from " + fromDisplay + " to " + toDisplay
                + " (" + err.Message + "). Move the file by hand.");
            return;
        }
        // a database's settings folder left with nothing in it goes too: relatude.settings/{old short name}
        if (before != null) removeIfEmpty(Path.Combine([_rootDataFolderPath, .. DatabaseShortName.SettingsFolder(before).Split('/')]));
    }

    // DatamodelOverridesPath named a file of the site that took the place of the database's overrides, for
    // a team that wanted them in source control. The shared file is that now; what the setting names is
    // copied there once, and left for the team to remove with the setting.
    void copyFromOverridesPath(NodeStoreContainerSettings settings, string? shortName) {
#pragma warning disable CS0618 // read only to carry its file over
        var named = settings.DatamodelOverridesPath?.Trim();
#pragma warning restore CS0618
        if (string.IsNullOrEmpty(named)) return;
        var path = Path.IsPathRooted(named) ? named : Path.GetFullPath(Path.Combine(_rootDataFolderPath, named));
        var target = SharedDatamodelOverridesPath(shortName);
        var display = DatabaseShortName.DatamodelOverridesFile(shortName);
        var unused = "The setting DatamodelOverridesPath of \"" + nameOf(settings) + "\" is no longer used: the overrides every installation shares are kept in " + display + ". ";
        try {
            if (!File.Exists(path)) {
                startupWarning(unused + "The file it names, " + named + ", is not there. Remove the setting.");
            } else if (File.Exists(target) && !string.Equals(Path.GetFullPath(path), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) {
                startupWarning(unused + named + " is not read, since " + display + " is there. Remove the setting, and the old file once nothing in it is missed.");
            } else if (!File.Exists(target)) {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(path, target);
                startupWarning(unused + named + " was copied there. Remove the setting and the old file.");
            }
        } catch (Exception err) {
            startupWarning(unused + "Copying " + named + " there failed (" + err.Message + "); copy it by hand.");
        }
    }
}
