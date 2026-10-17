using Relatude.DB.Common;
using Relatude.DB.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Relatude.DB.NodeServer.Settings;

/// <summary>
/// One file of a set of custom settings: a plain file on disk - the SHARED file in relatude.settings, or
/// the THIS SERVER file on local storage - or a key in a storage provider, for a THIS SERVER file kept in
/// a blob container.
/// </summary>
public sealed class CustomSettingsFile {
    static readonly TimeSpan _writeTimeout = TimeSpan.FromSeconds(10);
    readonly string? _diskPath;
    readonly IIOProvider? _io;
    readonly string[] _key;

    CustomSettingsFile(string? diskPath, IIOProvider? io, string[] key, string display) {
        _diskPath = diskPath;
        _io = io;
        _key = key;
        Display = display;
    }

    internal static CustomSettingsFile OnDisk(string path, string display) => new(Path.GetFullPath(path), null, [Path.GetFileName(path)], display);
    internal static CustomSettingsFile InStorage(IIOProvider io, string[] key, string display)
        => io is IOProviderDisk disk ? new(Path.GetFullPath(Path.Combine([disk.BaseFolder, .. key])), null, key, display) : new(null, io, key, display);

    /// <summary>Where the file is, for people: relatude.settings/webshop.json.</summary>
    public string Display { get; }
    /// <summary>The file on disk, when it is one.</summary>
    public string? DiskPath => _diskPath;

    internal bool SameAs(CustomSettingsFile other) => _diskPath != null || other._diskPath != null
        ? string.Equals(_diskPath, other._diskPath, StringComparison.OrdinalIgnoreCase)
        : ReferenceEquals(_io, other._io) && _key.IsSameKey(other._key);

    internal bool Exists() => _diskPath != null ? File.Exists(_diskPath) : _io!.Exists(_key);

    internal string? ReadText() {
        if (_diskPath != null) {
            var path = _diskPath;
            return File.Exists(path) ? FileOpenRetry.Open(path, () => File.ReadAllText(path)) : null;
        }
        var io = _io!;
        return io.ExistsAndIsNotEmpty(_key) ? io.ReadAllTextUTF8(_key) : null;
    }

    /// <summary>Writes the file, or deletes it for null. On disk it is written whole and then put in
    /// place, so a crash mid-write cannot leave half a file - which would stop the settings from being read.</summary>
    internal void WriteText(string? text) {
        if (_diskPath != null) {
            var path = _diskPath;
            if (text == null) {
                if (File.Exists(path)) FileOpenRetry.Open(path, () => File.Delete(path), _writeTimeout);
                return;
            }
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            var temp = path + ".tmp";
            File.WriteAllText(temp, text);
            FileOpenRetry.Replace(temp, path, _writeTimeout);
            return;
        }
        if (text == null) _io!.DeleteFileIfItExists(_key);
        else _io!.WriteAllTextUTF8(_key, text);
    }
}

/// <summary>The files and configuration one set of custom settings is read from; see <see cref="CustomSettingsSet"/>.</summary>
/// <param name="Definition">The set's definition.</param>
/// <param name="StoreId">The database the set belongs to; null for a set of the server's.</param>
/// <param name="Database">That database's settings, for <see cref="ICustomSettingsDefinition{TSettings}.OnInit"/>.</param>
/// <param name="Info">Where what was read is logged.</param>
/// <param name="Warn">Where what was wrong with it is logged.</param>
/// <param name="SettingsFile">SHARED, found again for every read and write, since a database's short name - which names the folder - can change while the server runs.</param>
/// <param name="DataFile">THIS SERVER. Null when the overrides files are turned off (<see cref="ServerOptions.UseSettingsOverridesFile"/>),
/// and saves go into SHARED. Throws when the storage it is kept in cannot be reached.</param>
/// <param name="Overlays">The configuration sections over both files, in the order they apply.</param>
public sealed record CustomSettingsSources(
    ICustomSettingsDefinition Definition,
    Guid? StoreId,
    NodeStoreContainerSettingsBase? Database,
    Func<CustomSettingsFile> SettingsFile,
    Func<CustomSettingsFile>? DataFile,
    SettingsOverlay[] Overlays,
    Action<string> Info,
    Action<string> Warn);

/// <summary>
/// One set of custom settings in force - the server's, or one database's - with the layers it was made
/// from (see <see cref="ICustomSettingsDefinition"/>): the defaults of the settings class, SHARED
/// (<c>{FileKey}.json</c> in relatude.settings), THIS SERVER (<c>{FileKey}.overrides.json</c>, the difference from
/// SHARED, in the format of <see cref="SettingsPatch"/>), the configuration sections and the application's
/// code.
///
/// <para>The settings object in force (<see cref="Value"/>) is never changed in place: a save makes a new
/// one, and code that asks again sees it. Saving takes the code's values and then the configuration's back
/// out before it compares with SHARED, so neither ever reaches a file - the same as
/// <c>RelatudeDBServer.UpdateWAFServerSettingsFile</c> does for the server's own settings.</para>
/// </summary>
public sealed class CustomSettingsSet {
    const string _dataHeader =
        "// Custom settings \"{0}\" changed in the Relatude.DB admin UI on this installation. They are merged over\n"
        + "// {1}, which every installation has, when the server starts. The server rewrites this file\n"
        + "// on every save from the tab; move a change into {1} there to make it part of the application.\n";
    const string _settingsHeader =
        "// Custom settings \"{0}\" of the application: kept in source control and deployed with it. What the admin UI\n"
        + "// changes on an installation is kept in {1} and merged over this file.\n";

    // the settings files' own options, less what a settings class computes from its other values: a
    // getter-only property is not a setting, and would read as a change whenever those change
    static readonly JsonSerializerOptions _json = new(LocalSettingsLoaderFile.JsonOptions) { IgnoreReadOnlyProperties = true };

    readonly object _lock = new();
    readonly CustomSettingsSources _sources;
    CustomSettingsFile? _dataFile;
    string? _dataUnavailable;
    string? _error;
    object _current;
    JsonObject _base = [];
    JsonObject _patch = [];
    List<SettingsPatch.Entry>? _entries;
    JsonObject? _codeRestore;
    List<SettingsPatch.Entry> _codeEntries = [];

    CustomSettingsSet(CustomSettingsSources sources) {
        _sources = sources;
        _current = newSettings();
    }

    /// <summary>Reads every layer. Never throws: a file that cannot be read leaves <see cref="Error"/> set,
    /// which <see cref="Value"/> throws, and storage that cannot be reached leaves <see cref="DataUnavailable"/>
    /// set, which only refuses saves.</summary>
    public static CustomSettingsSet Load(CustomSettingsSources sources) {
        var set = new CustomSettingsSet(sources);
        set.read();
        return set;
    }

    public ICustomSettingsDefinition Definition => _sources.Definition;
    /// <summary>The database the set belongs to; null for a set of the server's.</summary>
    public Guid? StoreId => _sources.StoreId;
    /// <summary>
    /// The settings in force, an instance of <see cref="ICustomSettingsDefinition.SettingsType"/>. Replaced
    /// by each save rather than changed, so hold on to it no longer than one piece of work. Throws when a
    /// file could not be read (<see cref="Error"/>).
    /// </summary>
    public object Value => _error != null ? throw new InvalidOperationException(_error) : Volatile.Read(ref _current);
    /// <summary>Why the files could not be read, when they could not: nothing is in force, and nothing is saved.</summary>
    public string? Error => _error;
    /// <summary>Why the THIS SERVER file could not be read, when it could not: the settings come from
    /// SHARED, configuration and code alone, and saves are refused, since a save would write over it.</summary>
    public string? DataUnavailable => _dataUnavailable;
    /// <summary>SHARED, for people: relatude.settings/webshop.json.</summary>
    public string SettingsFileDisplay => settingsFile().Display;
    /// <summary>THIS SERVER, for people; null when the overrides files are turned off and saves go into SHARED.</summary>
    public string? DataFileDisplay => _dataFile?.Display ?? (_dataUnavailable != null ? "the overrides folder of the storage" : null);
    /// <summary>Whether saves go into THIS SERVER rather than SHARED.</summary>
    public bool HasDataFile => _sources.DataFile != null;
    /// <summary>The configuration sections over the files, for messages that name them.</summary>
    public IReadOnlyList<SettingsOverlay> Overlays => _sources.Overlays;
    /// <summary>SHARED as read, over the defaults of the class, in the form the settings serialize to.</summary>
    public JsonObject Base { get { lock (_lock) return (JsonObject)_base.DeepClone(); } }
    /// <summary>Everything THIS SERVER changes, one entry per setting, added element or removed element.</summary>
    public IReadOnlyList<SettingsPatch.Entry> Entries { get { lock (_lock) return _entries ??= SettingsPatch.Entries(_patch, _base); } }

    /// <summary>A copy of the settings in force, to change and hand to <see cref="Save"/>.</summary>
    public object Copy() => fromJson(toJson(Value));

    /// <summary>The value SHARED gives a setting, or null when it gives none.</summary>
    public JsonNode? FileValue(string path) {
        lock (_lock) return SettingsPatch.Read(_base, path)?.DeepClone();
    }
    /// <summary>The entry of THIS SERVER that decides this setting, see <see cref="SettingsOverridesFile.ValueEntryFor"/>.</summary>
    public SettingsPatch.Entry? ValueEntryFor(string path)
        => Entries.FirstOrDefault(e => e.Kind == SettingsPatch.EntryKind.Value && SettingsPatch.IsAtOrBelow(path, e.Path));
    /// <summary>True when the list element at this path is one THIS SERVER adds.</summary>
    public bool IsAdded(string elementPath)
        => Entries.Any(e => e.Kind == SettingsPatch.EntryKind.Added && string.Equals(e.Path, elementPath, StringComparison.OrdinalIgnoreCase));
    /// <summary>The elements of the list at this path that THIS SERVER removes.</summary>
    public IEnumerable<SettingsPatch.Entry> RemovedFrom(string listPath)
        => Entries.Where(e => e.Kind == SettingsPatch.EntryKind.Removed && e.Path.StartsWith(listPath + "[", StringComparison.OrdinalIgnoreCase)
            && e.Path.IndexOf(']', listPath.Length) == e.Path.Length - 1);

    /// <summary>True when a configuration section decides this setting, or an object or list it is part
    /// of; <paramref name="value"/> is what it is set to there.</summary>
    public bool IsConfigured(string path, out JsonNode? value) {
        // the last section to apply is the one whose value is in force
        for (var i = _sources.Overlays.Length - 1; i >= 0; i--) {
            if (_sources.Overlays[i].IsOverridden(path, out value)) return true;
        }
        value = null;
        return false;
    }
    /// <summary>True when <see cref="ICustomSettingsDefinition{TSettings}.OnInit"/> sets this setting, or an object or list it is part of.</summary>
    public bool IsSetByCode(string path) {
        lock (_lock) return _codeEntries.Any(e => SettingsPatch.IsAtOrBelow(path, e.Path));
    }
    /// <summary>What decides a setting when the files do not, said the way a message names it, or null.</summary>
    public string? DecidedOutsideTheFiles(string path) {
        for (var i = _sources.Overlays.Length - 1; i >= 0; i--) {
            if (_sources.Overlays[i].IsOverridden(path, out _)) return "the " + _sources.Overlays[i].SectionName + " configuration section";
        }
        return IsSetByCode(path) ? "the application's code" : null;
    }

    /// <summary>
    /// Saves <paramref name="edited"/> - a <see cref="Copy"/> with changes made to it - and puts it in force.
    /// What differs from SHARED, the code's and configuration's values taken out, is written to THIS
    /// SERVER, which is deleted when nothing is left; with the overrides files turned off it is all written
    /// to SHARED. Returns true when a file changed.
    /// </summary>
    public bool Save(object edited) {
        if (edited.GetType() != Definition.SettingsType) throw new ArgumentException("The settings are a " + edited.GetType().Name + ", not a " + Definition.SettingsType.Name + ".");
        lock (_lock) {
            requireWritable();
            var layer = fileLayer(edited);
            bool changed;
            if (_dataFile != null) {
                var next = SettingsPatch.Diff(_base, layer);
                changed = !JsonNode.DeepEquals(next, _patch) || (next.Count > 0) != _dataFile.Exists();
                if (changed) writeData(_dataFile, next);
                _patch = next;
                _entries = null;
            } else {
                changed = !JsonNode.DeepEquals(layer, _base);
                if (changed) writeSettings(layer);
                _base = layer;
            }
            Volatile.Write(ref _current, edited);
            return changed;
        }
    }

    /// <summary>
    /// Moves entries out of THIS SERVER and into SHARED: the ones at <paramref name="paths"/>, or every one
    /// when it is null. Nothing in force changes. Entries a configuration section decides stay where they
    /// are, so configuration never reaches SHARED. SHARED is rewritten whole, without its comments.
    /// Returns the paths moved.
    /// </summary>
    public string[] MoveIntoSettingsFile(IReadOnlyCollection<string>? paths = null) {
        lock (_lock) {
            requireWritable();
            if (_dataFile == null) throw new InvalidOperationException("The overrides files are turned off (ServerOptions.UseSettingsOverridesFile), so " + Definition.Title + " are saved into " + SettingsFileDisplay + " already. ");
            var wanted = paths == null ? null : new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            var moved = new List<string>();
            var selected = SettingsPatch.Select(_patch, _base, entry => {
                if (wanted != null && !wanted.Contains(entry.Path)) return false;
                if (IsConfigured(entry.Path, out _)) return false;
                moved.Add(entry.Path);
                return true;
            });
            if (moved.Count == 0) return [];
            var merged = (JsonObject)_base.DeepClone();
            SettingsPatch.Merge(merged, selected);
            var newBase = toJson(fromJson(merged));
            writeSettings(newBase);
            _base = newBase;
            // THIS SERVER is the difference from SHARED, which has just changed under it
            var next = SettingsPatch.Diff(_base, fileLayer(_current));
            writeData(_dataFile, next);
            _patch = next;
            _entries = null;
            _sources.Info("Moved into " + SettingsFileDisplay + " from " + _dataFile.Display + ": " + string.Join(", ", moved.Select(SettingsPatch.Display)) + ".");
            return [.. moved];
        }
    }

    /// <summary>SHARED as it is on disk now, its keys matched to the settings class, or null when there is
    /// none or it cannot be read - for the warning about secrets kept in it.</summary>
    public JsonObject? ReadSettingsFileJson() {
        try {
            var text = settingsFile().ReadText();
            if (string.IsNullOrWhiteSpace(text)) return null;
            return SettingsOverridesFile.Normalize(parse(text, SettingsFileDisplay), Definition.SettingsType, SettingsFileDisplay, _ => { });
        } catch {
            return null;
        }
    }

    /// <summary>Puts THIS SERVER somewhere else: written there first and only then taken away from where it
    /// was, so a failure between the two leaves a copy rather than nothing. For the server's sets, which
    /// are kept beside relatude.db.overrides.json and go where it goes.</summary>
    internal void MoveDataFile(CustomSettingsFile next) {
        lock (_lock) {
            if (_dataFile == null || _dataUnavailable != null || _dataFile.SameAs(next)) return;
            if (_patch.Count > 0) writeData(next, _patch);
            writeData(_dataFile, []);
            _dataFile = next;
        }
    }

    // ---- reading ----

    void read() {
        var definition = Definition;
        var type = definition.SettingsType;
        try {
            // SHARED, over the defaults of the class: what a property it leaves out holds
            var shared = settingsFile();
            object fileSettings = newSettings();
            var text = shared.ReadText();
            if (!string.IsNullOrWhiteSpace(text)) {
                var normalized = SettingsOverridesFile.Normalize(parse(text, shared.Display), type, shared.Display, _sources.Warn);
                fileSettings = normalized.Deserialize(type, _json) ?? fileSettings;
            }
            _base = toJson(fileSettings);
            // THIS SERVER: the difference from SHARED, read the way relatude.db.overrides.json is
            _patch = [];
            if (_sources.DataFile != null) readData(type);
            var effective = (JsonObject)_base.DeepClone();
            SettingsPatch.Merge(effective, _patch);
            // APPSETTINGS
            foreach (var overlay in _sources.Overlays) overlay.Apply(effective);
            var current = fromJson(effective);
            // CODE: what it changes is remembered, to be taken out again before every save
            var before = toJson(current);
            try {
                definition.ApplyCode(current, _sources.Database);
            } catch (Exception error) {
                _sources.Warn("The OnInit of the custom settings \"" + definition.Title + "\" failed: " + error.Message);
            }
            var after = toJson(current);
            var forward = SettingsPatch.Diff(before, after);
            if (forward.Count > 0) {
                _codeRestore = SettingsPatch.Diff(after, before);
                _codeEntries = SettingsPatch.Entries(forward, before);
                _sources.Info("Set by the application's code in the custom settings \"" + definition.Title + "\": "
                    + string.Join(", ", _codeEntries.Select(e => SettingsPatch.Display(e.Path))) + ". These are not saved to the settings files.");
            }
            _current = current;
        } catch (Exception error) {
            _error = "The custom settings \"" + definition.Title + "\" could not be read: " + error.Message;
            _sources.Warn(_error);
        }
    }

    void readData(Type type) {
        string? text;
        try {
            _dataFile = _sources.DataFile!();
            text = _dataFile.ReadText();
        } catch (Exception error) {
            _dataFile = null;
            _dataUnavailable = "the overrides file of the custom settings \"" + Definition.Title + "\" could not be read (" + error.Message
                + "). Their values come from " + SettingsFileDisplay + ", configuration and code alone; restart once the storage can be reached.";
            _sources.Warn("Custom settings changed in the admin UI are not applied: " + _dataUnavailable);
            return;
        }
        if (string.IsNullOrWhiteSpace(text)) return;
        var patch = SettingsOverridesFile.Normalize(parse(text, _dataFile.Display), type, _dataFile.Display, _sources.Warn);
        var merged = (JsonObject)_base.DeepClone();
        SettingsPatch.Merge(merged, patch, message => _sources.Warn(_dataFile.Display + ": " + message));
        // what the file says once read: values written the way the settings write them, and nothing that
        // changes nothing, so the tab and the next save see the same difference
        _patch = SettingsPatch.Diff(_base, toJson(fromJson(merged)));
        var entries = SettingsPatch.Entries(_patch, _base);
        if (entries.Count > 0) {
            _sources.Info("Custom settings \"" + Definition.Title + "\" from " + _dataFile.Display + " applied: " + string.Join(", ", entries.Select(e => SettingsPatch.Display(e.Path))));
        }
    }

    static JsonObject parse(string text, string display) {
        try {
            var node = JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return node as JsonObject ?? throw new JsonException("The file does not hold a JSON object.");
        } catch (JsonException error) {
            throw new Exception(display + " is not valid JSON: " + error.Message + " Correct it, or move it away to use the defaults.", error);
        }
    }

    // ---- writing ----

    void requireWritable() {
        if (_error != null) throw new InvalidOperationException(_error);
        if (_dataUnavailable != null) throw new InvalidOperationException("Settings cannot be saved: " + _dataUnavailable);
    }

    // the settings with the code's changes and then the configuration taken off again, in the opposite
    // order to the one they were put on in
    JsonObject fileLayer(object settings) {
        var json = toJson(settings);
        if (_codeRestore != null) SettingsPatch.Merge(json, _codeRestore);
        for (var i = _sources.Overlays.Length - 1; i >= 0; i--) _sources.Overlays[i].RemoveOverridesBeforeSave(json);
        return json;
    }

    void writeData(CustomSettingsFile file, JsonObject patch) {
        file.WriteText(patch.Count == 0 ? null
            : string.Format(_dataHeader, Definition.Title, SettingsFileDisplay) + patch.ToJsonString(_json) + Environment.NewLine);
    }

    void writeSettings(JsonObject settings) {
        var where = _dataFile?.Display ?? Definition.FileKey + ".overrides.json";
        settingsFile().WriteText(string.Format(_settingsHeader, Definition.Title, where) + settings.ToJsonString(_json) + Environment.NewLine);
    }

    CustomSettingsFile settingsFile() => _sources.SettingsFile();

    object newSettings() => Activator.CreateInstance(Definition.SettingsType)
        ?? throw new Exception("Could not make a " + Definition.SettingsType.Name + ".");

    JsonObject toJson(object settings) => JsonSerializer.SerializeToNode(settings, Definition.SettingsType, _json) as JsonObject
        ?? throw new Exception(Definition.SettingsType.Name + " does not serialize to a JSON object.");

    object fromJson(JsonObject json) => json.Deserialize(Definition.SettingsType, _json)
        ?? throw new Exception("Could not read the " + Definition.SettingsType.Name + ".");
}
