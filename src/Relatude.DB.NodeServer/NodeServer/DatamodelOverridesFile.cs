using System.Text.Json;
using System.Text.Json.Nodes;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.IO;
using Relatude.DB.NodeServer.ModelEditor;
using Relatude.DB.NodeServer.Settings;

namespace Relatude.DB.NodeServer;

/// <summary>
/// Where one database keeps its datamodel overrides (<see cref="Datamodel.Overrides"/>), reading them,
/// and planning to write them. They are kept in two files (see <see cref="DatamodelOverridesLayers"/>):
/// <list type="bullet">
/// <item>SETTINGS: datamodel.json in the database's folder in relatude.settings
/// (<see cref="DatabaseShortName.DatamodelFile"/>) - part of the application, in source control and
/// deployed to every installation. Nothing but moving overrides there writes it;</item>
/// <item>DATA: <see cref="FileKeyUtility.Datamodel_OverridesFileKey"/> on the database's primary storage
/// provider - what the data model editor changed on this installation, merged over the SETTINGS file.</item>
/// </list>
/// </summary>
public sealed class DatamodelOverridesFile {
    /// <summary>What the editor's write plan files the DATA file under, the way it files a source's files under the source's id.</summary>
    public static readonly Guid PlanId = new("00000000-0000-0000-0000-0000000a77e5");
    public Guid? IoId { get; private init; }
    public string[] IoKey { get; } = FileKeyUtility.Datamodel_OverridesFileKey;
    /// <summary>False when the database has no primary storage provider to keep the DATA file in.</summary>
    public bool CanWrite => IoId != null;
    /// <summary>Where the DATA file is, for people: relatude.data/overrides/datamodel.overrides.json on local disk.</summary>
    public required string DataLocation { get; init; }
    /// <summary>The SETTINGS file on disk.</summary>
    public required string SettingsPath { get; init; }
    /// <summary>The SETTINGS file relative to the root data folder, for people: relatude.settings/[short name]/datamodel.json.</summary>
    public required string SettingsLocation { get; init; }

    const string dataHeader =
        "// The datamodel overrides of this installation, written by the data model editor of the Relatude.DB admin UI.\n"
        + "// They are merged over the ones in {0}, which every installation has:\n"
        + "// a value here replaces the one there, and null takes it away. Move them there from the editor\n"
        + "// (Models, Sources, Overrides) to have them in source control and on every installation.\n";
    const string settingsHeader =
        "// The datamodel overrides every installation of the application has: kept in source control and deployed\n"
        + "// with it. Each installation merges its own, made in the data model editor, over these. The admin UI writes\n"
        + "// this file only when overrides are moved here (Models, Sources, Overrides).\n";

    public static DatamodelOverridesFile For(RelatudeDBServer server, NodeStoreContainerSettings settings) {
        var ioId = settings.IoDatabase.HasValue && settings.IoDatabase.Value != Guid.Empty ? settings.IoDatabase : null;
        var (folder, display) = server.SettingsFolderFor(settings);
        var dataLocation = FileKeyUtility.Datamodel_OverridesFileKey.AsKeyString() + " in the database's storage";
        try {
            if (ioId != null && server.TryGetIO(ioId.Value, out var io)) dataLocation = server.DataDisplayOf(io, FileKeyUtility.Datamodel_OverridesFileKey);
        } catch {
            // storage that cannot be reached: the open says so
        }
        return new DatamodelOverridesFile {
            IoId = ioId,
            DataLocation = dataLocation,
            SettingsPath = Path.Combine(folder.BaseFolder, DatabaseShortName.DatamodelFileName),
            SettingsLocation = display + "/" + DatabaseShortName.DatamodelFileName,
        };
    }

    /// <summary>Whether the DATA file is there.</summary>
    public bool Exists(RelatudeDBServer server) => IoId != null && server.TryGetIO(IoId.Value, out var io) && io.ExistsAndIsNotEmpty(IoKey);
    /// <summary>Whether the SETTINGS file is there.</summary>
    public bool SettingsExists => File.Exists(SettingsPath);

    // ---- reading ----

    /// <summary>
    /// The overrides in force - the SETTINGS file's with the DATA file's merged over them - or null when
    /// there are none. A file that is not valid JSON stops the model from loading rather than being
    /// ignored: the overrides may keep content from the public (a default read access, a hidden type),
    /// and opening without them would quietly undo that.
    /// </summary>
    public DatamodelOverrides? Read(RelatudeDBServer server) => DatamodelOverridesLayers.ToOverrides(DatamodelOverridesLayers.Merge(ReadSettingsLayer(), ReadDataLayer(server)));
    /// <summary>The SETTINGS file's overrides, or null when it has none.</summary>
    public DatamodelOverrides? ReadSettings() => DatamodelOverridesLayers.ToOverrides(ReadSettingsLayer());

    /// <summary>The SETTINGS file's content (see <see cref="DatamodelOverridesLayers.Parse"/>), or null when there is none.</summary>
    public JsonObject? ReadSettingsLayer() {
        string? json = null;
        if (File.Exists(SettingsPath)) json = FileOpenRetry.Open(SettingsPath, () => File.ReadAllText(SettingsPath));
        return parse(json, SettingsLocation, keepResets: false);
    }
    /// <summary>The DATA file's content, resets kept, or null when there is none.</summary>
    public JsonObject? ReadDataLayer(RelatudeDBServer server) {
        string? json = null;
        if (IoId != null && server.TryGetIO(IoId.Value, out var io) && io.ExistsAndIsNotEmpty(IoKey)) json = io.ReadAllTextUTF8(IoKey);
        return parse(json, DataLocation, keepResets: true);
    }
    static JsonObject? parse(string? json, string where, bool keepResets) {
        try {
            return DatamodelOverridesLayers.Parse(json, keepResets);
        } catch (JsonException ex) {
            throw new Exception("The datamodel overrides in " + where + " are not valid: " + ex.Message
                + " Fix the file, or remove it to open the database without its overrides. ", ex);
        }
    }
    public static DatamodelOverrides Parse(string json) => JsonSerializer.Deserialize<DatamodelOverrides>(json, DatamodelJson.Options) ?? new();
    public static string Serialize(DatamodelOverrides overrides) => JsonSerializer.Serialize(overrides, DatamodelJson.Options);

    /// <summary>Whether two sets of overrides say the same, entries that override nothing aside.</summary>
    public static bool Same(DatamodelOverrides? a, DatamodelOverrides? b) => canonical(a) == canonical(b);
    static string canonical(DatamodelOverrides? o) {
        if (o == null) return "";
        var copy = Parse(Serialize(o));
        copy.RemoveEmpty();
        return copy.IsEmpty ? "" : DatamodelJson.CanonicalJson(copy, DatamodelJson.CompareOptions); // the names are for people
    }

    // ---- writing ----

    /// <summary>
    /// The write (or delete, when nothing is left) that makes the DATA file hold what the draft's overrides
    /// differ in from the SETTINGS file, or null when the draft overrides what the active model does. The
    /// SETTINGS file is never written here. The names next to the ids are brought up to date with the draft.
    /// </summary>
    internal PlannedFile? Plan(RelatudeDBServer server, DatamodelOverrides? active, DatamodelOverrides? draft, Datamodel draftModel) {
        if (Same(active, draft)) return null;
        var exists = Exists(server);
        var content = DatamodelOverridesLayers.Diff(ReadSettingsLayer(), DatamodelOverridesLayers.FromOverrides(draft));
        DatamodelOverridesLayers.UpdateNames(content, draftModel);
        var empty = DatamodelOverridesLayers.IsEmpty(content);
        return new PlannedFile {
            SourceId = PlanId,
            Path = "provider:" + IoId + "/" + IoKey.AsKeyString(),
            RelativePath = DataLocation,
            Action = empty ? PlannedFileAction.Delete : PlannedFileAction.Write,
            Content = empty ? null : DataText(content),
            Exists = exists,
            Changed = !empty || exists,
            IoId = IoId,
            IoKey = IoKey,
        };
    }
    /// <summary>The DATA file as it is written.</summary>
    internal string DataText(JsonObject layer) => string.Format(dataHeader, SettingsLocation) + DatamodelOverridesLayers.Serialize(layer) + Environment.NewLine;
    static string settingsText(JsonObject layer) => settingsHeader + DatamodelOverridesLayers.Serialize(layer) + Environment.NewLine;

    /// <summary>
    /// Moves the attributes named from the DATA file into the SETTINGS file (see
    /// <see cref="DatamodelOverridesLayers.Move"/>). What is in force does not change, so nothing has to be
    /// reopened. The SETTINGS file is written first: should the second write fail, the attributes are in
    /// both files, which says the same. Returns how many were moved; the ones the DATA file does not have
    /// are passed over.
    /// </summary>
    public int MoveToSettings(RelatudeDBServer server, IEnumerable<DatamodelOverridesLayers.AttributePath> paths, Datamodel? model) {
        if (IoId == null || !server.TryGetIO(IoId.Value, out var io)) throw new Exception("The database has no primary storage provider, so it keeps no overrides of its own to move. ");
        var (settings, rest, moved) = DatamodelOverridesLayers.Move(ReadSettingsLayer(), ReadDataLayer(server), paths);
        if (moved == 0) return 0;
        if (model != null) {
            DatamodelOverridesLayers.UpdateNames(settings, model);
            DatamodelOverridesLayers.UpdateNames(rest, model);
        }
        writeSettings(settings);
        if (DatamodelOverridesLayers.IsEmpty(rest)) io.DeleteFileIfItExists(IoKey);
        else io.WriteAllTextUTF8(IoKey, DataText(rest));
        return moved;
    }

    /// <summary>
    /// The SETTINGS file as moving the attributes named would make it, without writing anything: for an
    /// installation where the SETTINGS file is replaced by the next deployment, to be put into source
    /// control by hand.
    /// </summary>
    public string SettingsTextWith(RelatudeDBServer server, IEnumerable<DatamodelOverridesLayers.AttributePath> paths, Datamodel? model) {
        var (settings, _, _) = DatamodelOverridesLayers.Move(ReadSettingsLayer(), ReadDataLayer(server), paths);
        if (model != null) DatamodelOverridesLayers.UpdateNames(settings, model);
        return settingsText(settings);
    }

    // written whole and then put in place, so a crash mid-write cannot leave half a file - which would
    // stop the database from opening, see Read
    void writeSettings(JsonObject layer) {
        if (DatamodelOverridesLayers.IsEmpty(layer)) {
            if (File.Exists(SettingsPath)) FileOpenRetry.Open(SettingsPath, () => File.Delete(SettingsPath), TimeSpan.FromSeconds(10));
            return;
        }
        var folder = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, settingsText(layer));
        FileOpenRetry.Replace(temp, SettingsPath, TimeSpan.FromSeconds(10));
    }
}
