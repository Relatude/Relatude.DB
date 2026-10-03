using Relatude.DB.NodeServer.Settings;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Relatude.DB.NodeServer;

/// <summary>
/// Where the settings in force come from, and where a save puts them. Four layers, each merged over
/// the one before at every start and every soft restart:
/// <list type="number">
/// <item>relatude.db.json, read by the <see cref="ISettingsLoader"/>;</item>
/// <item>relatude.db.overrides.json, what the admin UI changed (<see cref="SettingsOverridesFile"/>);</item>
/// <item>the RelatudeDB configuration section (<see cref="SettingsOverlay"/>);</item>
/// <item>the application's code: <see cref="ServerOptions.OnServerSettingsInit"/>,
/// <see cref="ServerOptions.OnContainerSettingsInit"/> and <see cref="ServerOptions.OnStoreSettingsInit"/>.</item>
/// </list>
/// A save takes the last two off again - the code's changes first, then configuration - and writes
/// what is left: its difference from relatude.db.json into the overrides file, or all of it into
/// relatude.db.json when the overrides file is turned off. So neither a secret from configuration nor a
/// value the code forces at every start is ever written to either file.
/// <para>The overrides file is kept with the default database (<see cref="SettingsOverridesLocation"/>),
/// found in relatude.db.json with the configuration section over it - which is why the section is
/// read before the files are, though it is merged after them.</para>
/// </summary>
public partial class RelatudeDBServer {
    SettingsOverridesFile? _overridesFile;
    readonly List<CodeChange> _codeChanges = [];
    readonly object _settingsWriteLock = new();

    /// <summary>What one settings callback changed: the patch that undoes it, and the settings it touched.</summary>
    sealed record CodeChange(JsonObject Restore, List<SettingsPatch.Entry> Entries);

    /// <summary>relatude.db.overrides.json, or null when <see cref="ServerOptions.UseSettingsOverridesFile"/>
    /// turns it off and the admin UI writes to relatude.db.json as it always did.</summary>
    public SettingsOverridesFile? OverridesFile => _overridesFile;

    /// <summary>
    /// Where the overrides file is for settings files that say <paramref name="fileSettings"/>: the file
    /// <see cref="ServerOptions.SettingsOverridesFilePath"/> names, or else with the default database
    /// as these settings and the configuration section describe it. Can throw: building the default
    /// database's storage provider is a real connection for blob storage.
    /// </summary>
    SettingsOverridesLocation resolveOverridesLocation(RelatudeDBServerSettings fileSettings) {
        var path = Options?.SettingsOverridesFilePath;
        if (!string.IsNullOrWhiteSpace(path)) return SettingsOverridesLocation.OnDisk(path, _rootDataFolderPath);
        var described = _settingsOverlay == null ? fileSettings : _settingsOverlay.Preview(fileSettings);
        return SettingsOverridesLocation.Resolve(described, _rootDataFolderPath);
    }

    /// <summary>
    /// Layers one and two: relatude.db.json, and the overrides file merged over it. A file that is not
    /// valid JSON stops the start. Storage that cannot be reached does not: the server starts on
    /// relatude.db.json alone - the default database would not open there either - and the settings
    /// pages refuse to save until a restart reads the file, since a save would write over it.
    /// </summary>
    async Task<RelatudeDBServerSettings> readSettingsFilesAsync() {
        lock (_codeChanges) _codeChanges.Clear();
        _overridesFile = null;
        var fileSettings = await _settingsLoader!.ReadAsync();
        if (Options?.UseSettingsOverridesFile == false) return fileSettings;
        SettingsOverridesLocation? location = null;
        try {
            location = resolveOverridesLocation(fileSettings);
            if (location.Note != null) Log(location.Note);
            _overridesFile = SettingsOverridesFile.Open(location, fileSettings, Log, startupWarning, out var effective);
            return effective;
        } catch (Exception err) when (err is not SettingsOverridesFileInvalidException) {
            var where = location?.Display ?? "the default database's storage";
            var reason = "the overrides file in " + where + " could not be read when the server started (" + err.Message
                + "). The settings in force come from " + Defaults.SettingsFileName + " and configuration alone; restart once the storage can be reached.";
            startupWarning("Settings changed in the admin UI are not applied: " + reason);
            _overridesFile = SettingsOverridesFile.ForUnavailable(location ?? SettingsOverridesLocation.OnDisk(SettingsOverridesFile.FallbackRelativePath, _rootDataFolderPath),
                fileSettings, reason);
            return fileSettings;
        }
    }

    /// <summary>
    /// The default database and the overrides file share one storage provider instance where they can:
    /// a second instance would cost a second connection - a blob provider lists its whole container
    /// when it is created - and would not see the other's files. Only when the database's provider
    /// settings are still the ones the file was found with; an edit in the admin UI that moves the
    /// database gets a provider of its own, and the file stays where it was found.
    /// </summary>
    void shareOverridesFileProvider() {
        var location = _overridesFile?.Location;
        if (location?.Io == null || location.IoId == null) return;
        var settings = _serverSettings.ContainerSettings?.SelectMany(c => c.IOSettings ?? []).FirstOrDefault(s => s.Id == location.IoId);
        if (settings == null || !location.IsProviderFor(settings)) return;
        lock (_ios) _ios.TryAdd(location.IoId.Value, location.Io);
    }

    void startupWarning(string message) {
        Log(message);
        Console.Error.WriteLine("relatude.db: " + message);
    }

    /// <summary>Runs <see cref="ServerOptions.OnServerSettingsInit"/> and remembers what it changed.</summary>
    void raiseServerSettingsInitRecorded() {
        if (Options?.OnServerSettingsInit == null) return;
        var before = SettingsOverridesFile.ToJson(_serverSettings);
        RaiseEventServerSettingsInit(_serverSettings);
        recordCodeChange(before, SettingsOverridesFile.ToJson(_serverSettings), null);
    }

    /// <summary>
    /// Runs <see cref="ServerOptions.OnContainerSettingsInit"/> and <see cref="ServerOptions.OnStoreSettingsInit"/>
    /// for one database and remembers what they changed. Used both at start and for a database added in
    /// the admin UI, so the two are built the same way and neither has the code's values saved.
    /// </summary>
    internal void RaiseContainerSettingsInitRecorded(NodeStoreContainerSettings settings) {
        if (Options?.OnContainerSettingsInit == null && Options?.OnStoreSettingsInit == null) return;
        var before = containerJson(settings);
        RaiseEventContainerSettingsInit(settings);
        if (settings.LocalSettings != null) RaiseEventStoreSettingsInit(settings.LocalSettings, settings);
        recordCodeChange(before, containerJson(settings), settings.Id);
    }

    static JsonObject containerJson(NodeStoreContainerSettings settings)
        => JsonSerializer.SerializeToNode(settings, LocalSettingsLoaderFile.JsonOptions) as JsonObject
            ?? throw new Exception("Could not serialize the database settings.");

    void recordCodeChange(JsonObject before, JsonObject after, Guid? containerId) {
        var forward = SettingsPatch.Diff(before, after);
        if (forward.Count == 0) return;
        var restore = SettingsPatch.Diff(after, before);
        var baseRoot = before;
        if (containerId != null) {
            // a database's callbacks see only its own settings, so their patches are placed under its
            // element of the server's list of databases, addressed by id like any other element
            forward = inContainer(containerId.Value, forward);
            restore = inContainer(containerId.Value, restore);
            baseRoot = inContainer(containerId.Value, before);
        }
        var entries = SettingsPatch.Entries(forward, baseRoot);
        lock (_codeChanges) _codeChanges.Add(new CodeChange(restore, entries));
        Log("Set by the application's code: " + string.Join(", ", entries.Select(e => SettingsPatch.Display(e.Path))) + ". These are not saved to the settings files.");
    }

    static JsonObject inContainer(Guid containerId, JsonObject containerPatch) {
        var element = new JsonObject { ["Id"] = containerId.ToString() };
        foreach (var (key, value) in containerPatch) {
            if (key == "Id") continue;
            element[key] = value?.DeepClone();
        }
        return new JsonObject { ["ContainerSettings"] = new JsonArray(element) };
    }

    /// <summary>True when one of the settings callbacks sets this setting, or an object or list it is
    /// part of, at every start. Paths are those of <see cref="SettingsOverlay.OverridePath"/>.</summary>
    internal bool IsSetByCode(string path) {
        lock (_codeChanges) return _codeChanges.Any(c => c.Entries.Any(e => SettingsPatch.IsAtOrBelow(path, e.Path)));
    }

    /// <summary>
    /// What decides a setting when it is not the settings files: the configuration section or the
    /// application's code, said the way a message names it - or null when the files do. A setting
    /// decided elsewhere is not one to edit in the admin UI, since the edit would be undone at the next
    /// start, and is never moved into or out of the files.
    /// </summary>
    internal string? DecidedOutsideTheSettingsFiles(string path) {
        if (_settingsOverlay != null && _settingsOverlay.IsOverridden(path, out _)) return "the " + _settingsOverlay.SectionName + " configuration section";
        if (IsSetByCode(path)) return "the application's code";
        return null;
    }

    /// <summary>
    /// Saves the settings in force. With the overrides file on - the default - what differs from
    /// relatude.db.json is written there and relatude.db.json is not touched; with it off, the settings
    /// are written to relatude.db.json. Either way the configuration section and the application's code
    /// are taken out first.
    /// </summary>
    public void UpdateWAFServerSettingsFile() {
        lock (_settingsWriteLock) {
            _serverSettings.ContainerSettings = GetContainers().Select(c => c.Settings).ToArray();
            var fileLayer = fileLayerJson();
            if (_overridesFile != null) _overridesFile.Save(fileLayer);
            else _settingsLoader!.WriteAsync(SettingsOverridesFile.FromJson(fileLayer)).Wait();
            if (Containers.TryGetValue(_serverSettings.DefaultStoreId, out var defaultContainer)) _defaultContainer = defaultContainer;
        }
    }

    /// <summary>
    /// After relatude.db.json has changed: when it now puts the default database somewhere else - a move
    /// of its storage folder, its provider, or which database is the default - the overrides file goes
    /// there too, or the next start would look for it where it is not. A destination that cannot be
    /// reached leaves the file where it is, and says so.
    /// </summary>
    void followDefaultDatabase(SettingsOverridesFile file, RelatudeDBServerSettings fileSettings) {
        try {
            var next = resolveOverridesLocation(fileSettings);
            var from = file.Display;
            if (file.MoveTo(next)) {
                Log("The overrides file follows the default database: moved from " + from + " to " + next.Display + ".");
                shareOverridesFileProvider();
            }
        } catch (Exception err) {
            startupWarning(Defaults.SettingsFileName + " now keeps the default database somewhere else, but the overrides file could not be moved"
                + " there (" + err.Message + "). It stays in " + file.Display + "; move it by hand before the next start, or it will not be found.");
        }
    }

    // the settings in force with the code's changes and then the configuration section taken off again,
    // in the opposite order to the one they were put on in
    JsonObject fileLayerJson() {
        var json = SettingsOverridesFile.ToJson(_serverSettings);
        CodeChange[] changes;
        lock (_codeChanges) changes = [.. _codeChanges];
        for (var i = changes.Length - 1; i >= 0; i--) SettingsPatch.Merge(json, changes[i].Restore);
        _settingsOverlay?.RemoveOverridesBeforeSave(json);
        return json;
    }

    /// <summary>
    /// Moves settings out of relatude.db.overrides.json and into relatude.db.json: the entries at
    /// <paramref name="paths"/>, or every entry when it is null. Nothing in force changes - the same
    /// values now come from the other file. Entries the configuration section decides are left where
    /// they are, so a value supplied by configuration can never end up in relatude.db.json this way.
    /// relatude.db.json is rewritten by the settings loader, which does not keep comments.
    /// Returns the paths that were moved.
    /// </summary>
    public string[] MoveOverridesIntoSettingsFile(IReadOnlyCollection<string>? paths = null) {
        lock (_settingsWriteLock) {
            var file = _overridesFile ?? throw new InvalidOperationException("The settings overrides file is turned off (ServerOptions.UseSettingsOverridesFile), so there is nothing to move. ");
            var wanted = paths == null ? null : new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            var moved = new List<string>();
            var fileJson = file.BaseWith(entry => {
                if (wanted != null && !wanted.Contains(entry.Path)) return false;
                if (_settingsOverlay != null && _settingsOverlay.IsOverridden(entry.Path, out _)) return false;
                moved.Add(entry.Path);
                return true;
            });
            if (moved.Count == 0) return [];
            var fileSettings = SettingsOverridesFile.FromJson(fileJson);
            _settingsLoader!.WriteAsync(fileSettings).Wait();
            file.Rebase(fileSettings);
            // the overrides file is the difference from relatude.db.json, which has just changed under it
            _serverSettings.ContainerSettings = GetContainers().Select(c => c.Settings).ToArray();
            file.Save(fileLayerJson());
            Log("Moved into " + Defaults.SettingsFileName + " from " + file.Display + ": " + string.Join(", ", moved.Select(SettingsPatch.Display)) + ".");
            followDefaultDatabase(file, fileSettings);
            return [.. moved];
        }
    }
}
