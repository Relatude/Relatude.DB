using System.Runtime.CompilerServices;
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
/// <item>the shared file, relatude.settings/[short name]/datamodel.overrides.json below the root data
/// folder (<see cref="DatabaseShortName.DatamodelOverridesFile"/>) - part of the application, in source
/// control and deployed to every installation. Nothing but moving overrides there writes it;</item>
/// <item>the installation's file, <see cref="FileKeyUtility.Datamodel_OverridesFileKey"/> on the
/// database's primary storage provider - what the data model editor changed on this installation,
/// merged over the shared file. Older versions kept it among the editor's drafts and history, in
/// datamodels/; such a file is moved the first time the location is asked for.</item>
/// </list>
/// </summary>
public sealed class DatamodelOverridesFile {
    /// <summary>What the editor's write plan files the installation's overrides file under, the way it files a source's files under the source's id.</summary>
    public static readonly Guid PlanId = new("00000000-0000-0000-0000-0000000a77e5");
    public Guid? IoId { get; private init; }
    public string[] IoKey { get; } = FileKeyUtility.Datamodel_OverridesFileKey;
    /// <summary>False when the database has no primary storage provider to keep the installation's overrides in.</summary>
    public bool CanWrite => IoId != null;
    /// <summary>Where the installation's overrides are, for people.</summary>
    public string Location => IoKey.AsKeyString() + " in the database's storage";
    /// <summary>The shared file on disk.</summary>
    public required string SharedPath { get; init; }
    /// <summary>The shared file relative to the root data folder, for people: relatude.settings/[short name]/datamodel.overrides.json.</summary>
    public required string SharedLocation { get; init; }

    const string installationHeader =
        "// The datamodel overrides of this installation, written by the data model editor of the Relatude.DB admin UI.\n"
        + "// They are merged over the overrides every installation shares, in {0}:\n"
        + "// a value here replaces the shared one, and null takes the shared one away. Move them there from the editor\n"
        + "// (Models, Sources, Overrides) to have them in source control and on every installation.\n";
    const string sharedHeader =
        "// The datamodel overrides every installation of the application shares: kept in source control and deployed\n"
        + "// with it. Each installation merges its own, made in the data model editor, over these. The admin UI writes\n"
        + "// this file only when overrides are moved here (Models, Sources, Overrides).\n";

    public static DatamodelOverridesFile For(RelatudeDBServer server, NodeStoreContainerSettings settings) {
        var ioId = settings.IoDatabase;
        var file = new DatamodelOverridesFile {
            IoId = ioId.HasValue && ioId.Value != Guid.Empty ? ioId : null,
            SharedPath = server.SharedDatamodelOverridesPathFor(settings),
            SharedLocation = DatabaseShortName.DatamodelOverridesFile(DatabaseShortName.Of(settings)),
        };
        if (file.IoId != null) moveFromLegacyPlace(server, file.IoId.Value, settings);
        return file;
    }

    // the providers already looked at for a file in the old place: once per provider instance, since a
    // provider whose settings change is built again, and the new one may point at another folder
    static readonly ConditionalWeakTable<IIOProvider, object> _lookedAt = [];
    /// <summary>
    /// Moves the overrides an older version kept in datamodels/ into overrides/: renamed where the
    /// provider can, and copied, read back and deleted where it cannot. A file already in the new place
    /// is never overwritten - the old one is left and said so. A move that fails leaves the file where it
    /// is, and <see cref="Read"/> reads it there.
    /// </summary>
    static void moveFromLegacyPlace(RelatudeDBServer server, Guid ioId, NodeStoreContainerSettings settings) {
        IIOProvider? io;
        try {
            if (!server.TryGetIO(ioId, out io)) return;
        } catch {
            return; // storage that cannot be reached: the open says so
        }
        lock (_lookedAt) {
            if (_lookedAt.TryGetValue(io, out _)) return;
            _lookedAt.Add(io, new object());
        }
        var legacy = FileKeyUtility.Datamodel_LegacyOverridesFileKey;
        var key = FileKeyUtility.Datamodel_OverridesFileKey;
        var name = string.IsNullOrEmpty(settings.Name) ? settings.Id.ToString() : settings.Name;
        try {
            if (!io.ExistsAndIsNotEmpty(legacy)) return;
            if (io.ExistsAndIsNotEmpty(key)) {
                server.Log("The datamodel overrides of \"" + name + "\" in " + legacy.AsKeyString() + " are an older copy and are not read: "
                    + key.AsKeyString() + " is. Delete the old file once nothing in it is missed.");
                return;
            }
            if (io.CanRenameFile) {
                io.RenameFile(legacy, key);
            } else {
                var text = io.ReadAllTextUTF8(legacy);
                io.WriteAllTextUTF8(key, text);
                if (io.ReadAllTextUTF8(key) != text) throw new Exception("the copy did not read back the same");
                io.DeleteFileIfItExists(legacy);
            }
            server.Log("Moved the datamodel overrides of \"" + name + "\" from " + legacy.AsKeyString() + " to " + key.AsKeyString() + ".");
        } catch (Exception err) {
            server.Log("Could not move the datamodel overrides of \"" + name + "\" from " + legacy.AsKeyString() + " to " + key.AsKeyString()
                + " (" + err.Message + "). They are read where they are, and written to the new place by the next change.");
        }
    }
    // the file the overrides are read from: the one in overrides/, or one an older version kept that could not be moved
    string[] readKey(IIOProvider io) => io.ExistsAndIsNotEmpty(IoKey) || !io.ExistsAndIsNotEmpty(FileKeyUtility.Datamodel_LegacyOverridesFileKey)
        ? IoKey : FileKeyUtility.Datamodel_LegacyOverridesFileKey;

    /// <summary>Whether the installation's file is there.</summary>
    public bool Exists(RelatudeDBServer server) => IoId != null && server.TryGetIO(IoId.Value, out var io) && io.ExistsAndIsNotEmpty(readKey(io));
    /// <summary>Whether the shared file is there.</summary>
    public bool SharedExists => File.Exists(SharedPath);

    // ---- reading ----

    /// <summary>
    /// The overrides in force - the shared ones with the installation's merged over them - or null when
    /// there are none. A file that is not valid JSON stops the model from loading rather than being
    /// ignored: the overrides may keep content from the public (a default read access, a hidden type),
    /// and opening without them would quietly undo that.
    /// </summary>
    public DatamodelOverrides? Read(RelatudeDBServer server) => DatamodelOverridesLayers.ToOverrides(DatamodelOverridesLayers.Merge(ReadSharedLayer(), ReadInstallationLayer(server)));
    /// <summary>The shared file's overrides, or null when it has none.</summary>
    public DatamodelOverrides? ReadShared() => DatamodelOverridesLayers.ToOverrides(ReadSharedLayer());

    /// <summary>The shared file's content (see <see cref="DatamodelOverridesLayers.Parse"/>), or null when there is none.</summary>
    public JsonObject? ReadSharedLayer() {
        string? json = null;
        if (File.Exists(SharedPath)) json = FileOpenRetry.Open(SharedPath, () => File.ReadAllText(SharedPath));
        return parse(json, SharedLocation, keepResets: false);
    }
    /// <summary>The installation's file's content, resets kept, or null when there is none.</summary>
    public JsonObject? ReadInstallationLayer(RelatudeDBServer server) {
        string? json = null;
        if (IoId != null && server.TryGetIO(IoId.Value, out var io) && io.ExistsAndIsNotEmpty(readKey(io))) json = io.ReadAllTextUTF8(readKey(io));
        return parse(json, Location, keepResets: true);
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
    /// The write (or delete, when nothing is left) that makes the installation's file hold what the
    /// draft's overrides differ in from the shared file, or null when the draft overrides what the active
    /// model does. The shared file is never written here. The names next to the ids are brought up to
    /// date with the draft.
    /// </summary>
    internal PlannedFile? Plan(RelatudeDBServer server, DatamodelOverrides? active, DatamodelOverrides? draft, Datamodel draftModel) {
        if (Same(active, draft)) return null;
        var exists = Exists(server);
        var content = DatamodelOverridesLayers.Diff(ReadSharedLayer(), DatamodelOverridesLayers.FromOverrides(draft));
        DatamodelOverridesLayers.UpdateNames(content, draftModel);
        var empty = DatamodelOverridesLayers.IsEmpty(content);
        return new PlannedFile {
            SourceId = PlanId,
            Path = "provider:" + IoId + "/" + IoKey.AsKeyString(),
            RelativePath = IoKey.AsKeyString(),
            Action = empty ? PlannedFileAction.Delete : PlannedFileAction.Write,
            Content = empty ? null : InstallationText(content),
            Exists = exists,
            Changed = !empty || exists,
            IoId = IoId,
            IoKey = IoKey,
        };
    }
    /// <summary>The installation's file as it is written.</summary>
    internal string InstallationText(JsonObject layer) => string.Format(installationHeader, SharedLocation) + DatamodelOverridesLayers.Serialize(layer) + Environment.NewLine;
    static string sharedText(JsonObject layer) => sharedHeader + DatamodelOverridesLayers.Serialize(layer) + Environment.NewLine;

    /// <summary>
    /// Moves the attributes named from the installation's file into the shared file (see
    /// <see cref="DatamodelOverridesLayers.Move"/>). What is in force does not change, so nothing has to
    /// be reopened. The shared file is written first: should the second write fail, the attributes are in
    /// both files, which says the same. Returns how many were moved; the ones the installation's file does
    /// not have are passed over.
    /// </summary>
    public int MoveToShared(RelatudeDBServer server, IEnumerable<DatamodelOverridesLayers.AttributePath> paths, Datamodel? model) {
        if (IoId == null || !server.TryGetIO(IoId.Value, out var io)) throw new Exception("The database has no primary storage provider, so it keeps no overrides of its own to move. ");
        // a file an older version kept in datamodels/ that could not be moved is read there until now; the
        // write below puts what is left of it in the new place, and the old copy would then only mislead
        var legacy = FileKeyUtility.Datamodel_LegacyOverridesFileKey;
        var readFromLegacy = !ReferenceEquals(readKey(io), IoKey);
        var installation = ReadInstallationLayer(server);
        var (shared, rest, moved) = DatamodelOverridesLayers.Move(ReadSharedLayer(), installation, paths);
        if (moved == 0) return 0;
        if (model != null) {
            DatamodelOverridesLayers.UpdateNames(shared, model);
            DatamodelOverridesLayers.UpdateNames(rest, model);
        }
        writeShared(shared);
        if (DatamodelOverridesLayers.IsEmpty(rest)) io.DeleteFileIfItExists(IoKey);
        else io.WriteAllTextUTF8(IoKey, InstallationText(rest));
        if (readFromLegacy) io.DeleteFileIfItExists(legacy);
        return moved;
    }

    /// <summary>
    /// The shared file as moving the attributes named would make it, without writing anything: for an
    /// installation where the shared file is replaced by the next deployment, to be put into source
    /// control by hand.
    /// </summary>
    public string SharedTextWith(RelatudeDBServer server, IEnumerable<DatamodelOverridesLayers.AttributePath> paths, Datamodel? model) {
        var (shared, _, _) = DatamodelOverridesLayers.Move(ReadSharedLayer(), ReadInstallationLayer(server), paths);
        if (model != null) DatamodelOverridesLayers.UpdateNames(shared, model);
        return sharedText(shared);
    }

    // written whole and then put in place, so a crash mid-write cannot leave half a file - which would
    // stop the database from opening, see Read
    void writeShared(JsonObject layer) {
        if (DatamodelOverridesLayers.IsEmpty(layer)) {
            if (File.Exists(SharedPath)) FileOpenRetry.Open(SharedPath, () => File.Delete(SharedPath), TimeSpan.FromSeconds(10));
            return;
        }
        var folder = System.IO.Path.GetDirectoryName(SharedPath);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        var temp = SharedPath + ".tmp";
        File.WriteAllText(temp, sharedText(layer));
        FileOpenRetry.Replace(temp, SharedPath, TimeSpan.FromSeconds(10));
    }
}
