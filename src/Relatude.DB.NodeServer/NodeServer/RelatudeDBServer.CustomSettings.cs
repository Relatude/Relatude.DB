using Relatude.DB.IO;
using Relatude.DB.NodeServer.Settings;

namespace Relatude.DB.NodeServer;

/// <summary>
/// Where the custom settings the application registers are kept (see <see cref="ICustomSettingsDefinition"/>),
/// beside the files of the settings they sit next to in the admin UI:
/// <list type="bullet">
/// <item>SHARED: <c>relatude.settings/{FileKey}.json</c> for a set of the server's, and in the database's own
/// folder in relatude.settings for a set per database - <c>relatude.settings/{short name}/{FileKey}.json</c>,
/// or <c>relatude.settings/{FileKey}.json</c> for the database without a short name. File keys are unique
/// among the sets, so a set of the server's and one of that database never share a file.</item>
/// <item>THIS SERVER: <c>{FileKey}.overrides.json</c> beside relatude.db.overrides.json for a set of the
/// server's, and in the overrides folder of the database's own storage for a set per database. Neither
/// is written when <see cref="ServerOptions.UseSettingsOverridesFile"/> is off: saves go into SHARED.</item>
/// <item>APPSETTINGS: <c>RelatudeDB:CustomSettings:{FileKey}</c>, which for a set per database applies to
/// every database, and <c>RelatudeDB:CustomSettings:Databases:{short name or id}:{FileKey}</c> over it.</item>
/// </list>
/// </summary>
public partial class RelatudeDBServer {
    CustomSettingsRegistry? _customSettings;

    /// <summary>The custom settings the application registers, and the sets of them in force.</summary>
    public CustomSettingsRegistry CustomSettings => _customSettings ??= new CustomSettingsRegistry(this);

    /// <summary>The definitions in <see cref="ServerOptions.CustomSettings"/> and those registered as
    /// services, checked: a mistake in one stops the start.</summary>
    void registerCustomSettings(IServiceProvider services) {
        var fromServices = services.GetServices<ICustomSettingsDefinition>();
        CustomSettings.Register([.. Options?.CustomSettings ?? [], .. fromServices]);
        foreach (var definition in CustomSettings.Definitions) {
            Log("Custom settings \"" + definition.Title + "\" (" + definition.FileKey + ", " + definition.Scope.ToString().ToLowerInvariant() + ") registered.");
        }
    }

    /// <summary>Reads one set of custom settings: of the server's, or of the database <paramref name="storeId"/>.</summary>
    internal CustomSettingsSet LoadCustomSettings(ICustomSettingsDefinition definition, Guid? storeId) {
        NodeStoreContainerSettings? database = null;
        if (storeId != null) {
            if (!Containers.TryGetValue(storeId.Value, out var container)) throw new InvalidOperationException("Database not found. ");
            database = container.Settings;
        }
        var label = "\"" + definition.Title + "\"" + (database == null ? "" : " of \"" + nameOf(database) + "\"");
        return CustomSettingsSet.Load(new CustomSettingsSources(
            definition,
            storeId,
            database,
            () => customSettingsFile(definition, database),
            customDataFile(definition, database),
            customOverlays(definition, database, label),
            Log,
            startupWarning));
    }

    CustomSettingsFile customSettingsFile(ICustomSettingsDefinition definition, NodeStoreContainerSettings? database) {
        var name = definition.FileKey + ".json";
        if (database == null) {
            return CustomSettingsFile.OnDisk(Path.Combine(SettingsFolderPath(null), name), Defaults.SettingsFolderPath + "/" + name);
        }
        var (folder, display) = SettingsFolderFor(database);
        return CustomSettingsFile.OnDisk(Path.Combine(folder.BaseFolder, name), display + "/" + name);
    }

    static string customDataFileName(ICustomSettingsDefinition definition) => definition.FileKey + ".overrides.json";

    // null when the overrides files are turned off; otherwise a lookup that throws when the storage the
    // file is kept in cannot be reached, which the set reports and survives
    Func<CustomSettingsFile>? customDataFile(ICustomSettingsDefinition definition, NodeStoreContainerSettings? database) {
        if (Options?.UseSettingsOverridesFile == false) return null;
        if (database == null) {
            var overrides = _overridesFile;
            if (overrides == null) return null;
            return () => {
                if (overrides.Unavailable != null) throw new Exception(overrides.Unavailable);
                return customDataFileBeside(overrides.Location, definition);
            };
        }
        return () => {
            var ioId = database.IoDatabase;
            if (ioId == null || ioId == Guid.Empty) throw new Exception("the database \"" + nameOf(database) + "\" has no primary storage provider to keep it in");
            if (!TryGetIO(ioId.Value, out var io)) throw new Exception("the storage provider of the database \"" + nameOf(database) + "\" is not in its settings");
            string[] key = [FileKeyUtility.OverridesFolderName, customDataFileName(definition)];
            return CustomSettingsFile.InStorage(io, key, DataDisplayOf(io, key));
        };
    }

    // a set of the server's is kept where relatude.db.overrides.json is, and follows it
    CustomSettingsFile customDataFileBeside(SettingsOverridesLocation location, ICustomSettingsDefinition definition) {
        var name = customDataFileName(definition);
        if (location.DiskPath != null) {
            var path = Path.Combine(Path.GetDirectoryName(location.DiskPath)!, name);
            return CustomSettingsFile.OnDisk(path, SettingsFileLocation.Display(path, _rootDataFolderPath));
        }
        string[] key = [.. location.Key[..^1], name];
        return CustomSettingsFile.InStorage(location.Io!, key, DataDisplayOf(location.Io!, key));
    }

    // the section for every database, and over it the one for this database by short name or id
    SettingsOverlay[] customOverlays(ICustomSettingsDefinition definition, NodeStoreContainerSettings? database, string label) {
        var sectionName = ConfigurationSectionName;
        if (sectionName == null || _configuration == null) return [];
        var custom = _configuration.GetSection(sectionName).GetSection(CustomSettingsCatalog.ConfigurationKey);
        var warn = (string message) => { Log(message); Console.Error.WriteLine("relatude.db: " + message); };
        var overlays = new List<SettingsOverlay>();
        var all = SettingsOverlay.ForCustomSettings(custom.GetSection(definition.FileKey), definition.SettingsType, label, Log, warn);
        if (all != null) overlays.Add(all);
        if (database != null) {
            var shortName = DatabaseShortName.Of(database);
            var own = custom.GetSection(CustomSettingsCatalog.DatabasesConfigurationKey).GetChildren()
                .FirstOrDefault(c => (shortName != null && string.Equals(c.Key, shortName, StringComparison.OrdinalIgnoreCase))
                    || (Guid.TryParse(c.Key, out var id) && id == database.Id));
            var one = own == null ? null : SettingsOverlay.ForCustomSettings(own.GetSection(definition.FileKey), definition.SettingsType, label, Log, warn);
            if (one != null) overlays.Add(one);
        } else if (custom.GetSection(CustomSettingsCatalog.DatabasesConfigurationKey).GetChildren().Any(c => c.GetSection(definition.FileKey).Exists())) {
            warn("The custom settings \"" + definition.Title + "\" are the server's, not a database's: what " + sectionName + ":" + CustomSettingsCatalog.ConfigurationKey + ":"
                + CustomSettingsCatalog.DatabasesConfigurationKey + " says for them is ignored. Use " + sectionName + ":" + CustomSettingsCatalog.ConfigurationKey + ":" + definition.FileKey + ".");
        }
        return [.. overlays];
    }

    /// <summary>The SHARED files of the sets per database, by name, for a database whose short name
    /// changes: they move with its folder.</summary>
    IEnumerable<string> customSettingsFileNamesPerDatabase()
        => (_customSettings?.Definitions ?? []).Where(d => d.Scope == CustomSettingsScope.Database).Select(d => d.FileKey + ".json");

    /// <summary>After relatude.db.overrides.json has moved with the default database, the THIS SERVER
    /// files of the server's sets go with it.</summary>
    void customSettingsFollowOverridesFile(SettingsOverridesLocation location) {
        if (_customSettings == null) return;
        foreach (var set in _customSettings.LoadedSets().Where(s => s.StoreId == null && s.HasDataFile)) {
            try {
                set.MoveDataFile(customDataFileBeside(location, set.Definition));
            } catch (Exception error) {
                startupWarning("The overrides file of the custom settings \"" + set.Definition.Title + "\" could not follow relatude.db.overrides.json to "
                    + location.Display + " (" + error.Message + "). It stays where it was; move it by hand before the next start, or it will not be found.");
            }
        }
    }
}
