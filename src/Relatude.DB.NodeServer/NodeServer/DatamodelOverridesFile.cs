using System.Text.Json;
using Relatude.DB.Datamodels;
using Relatude.DB.IO;
using Relatude.DB.NodeServer.ModelEditor;
using Relatude.DB.NodeServer.Settings;

namespace Relatude.DB.NodeServer;

/// <summary>
/// Where one database keeps its datamodel overrides (<see cref="Datamodel.Overrides"/>), and reading and
/// planning to write them. By default they are kept with the database -
/// <see cref="FileKeyUtility.Datamodel_OverridesFileKey"/> on its primary storage provider, beside the
/// editor's drafts and history - and <see cref="NodeStoreContainerSettings.DatamodelOverridesPath"/> can
/// name a file of the site instead.
/// </summary>
public sealed class DatamodelOverridesFile {
    /// <summary>What the editor's write plan files the overrides file under, the way it files a source's files under the source's id.</summary>
    public static readonly Guid PlanId = new("00000000-0000-0000-0000-0000000a77e5");
    public Guid? IoId { get; private init; }
    public string[]? IoKey { get; private init; }
    /// <summary>The absolute path, when the settings name a file rather than leaving the overrides with the database.</summary>
    public string? DiskPath { get; private init; }
    public bool InDatabase => DiskPath == null;
    /// <summary>False when the overrides are to be kept with a database that has no primary storage provider.</summary>
    public bool CanWrite => DiskPath != null || IoId != null;
    /// <summary>Where they are, for people.</summary>
    public string Location => DiskPath ?? (IoKey == null ? "" : IoKey.AsKeyString()) + (InDatabase ? " in the database's storage" : "");

    public static DatamodelOverridesFile For(RelatudeDBServer server, NodeStoreContainerSettings settings) {
        if (!string.IsNullOrWhiteSpace(settings.DatamodelOverridesPath)) {
            var path = settings.DatamodelOverridesPath.Trim();
            if (!Path.IsPathRooted(path)) path = Path.GetFullPath(Path.Combine(server.RootDataFolderPath, path));
            return new() { DiskPath = path };
        }
        var ioId = settings.IoDatabase;
        return new() { IoId = ioId.HasValue && ioId.Value != Guid.Empty ? ioId : null, IoKey = FileKeyUtility.Datamodel_OverridesFileKey };
    }

    public bool Exists(RelatudeDBServer server) {
        if (DiskPath != null) return File.Exists(DiskPath);
        return IoId != null && server.TryGetIO(IoId.Value, out var io) && io.ExistsAndIsNotEmpty(IoKey!);
    }
    /// <summary>
    /// The overrides, or null when there are none. A file that is not valid JSON stops the model from
    /// loading rather than being ignored: the overrides may keep content from the public (a default read
    /// access, a hidden type), and opening without them would quietly undo that.
    /// </summary>
    public DatamodelOverrides? Read(RelatudeDBServer server) {
        string? json = null;
        if (DiskPath != null) {
            if (File.Exists(DiskPath)) json = File.ReadAllText(DiskPath);
        } else if (IoId != null && server.TryGetIO(IoId.Value, out var io) && io.ExistsAndIsNotEmpty(IoKey!)) {
            json = io.ReadAllTextUTF8(IoKey!);
        }
        if (string.IsNullOrWhiteSpace(json)) return null;
        try {
            return Parse(json);
        } catch (JsonException ex) {
            throw new Exception("The datamodel overrides in " + Location + " are not valid: " + ex.Message
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

    /// <summary>
    /// The write (or delete, when nothing is left) that makes the file hold the draft's overrides, or null
    /// when it already does. The names next to the ids are brought up to date with the draft.
    /// </summary>
    internal PlannedFile? Plan(RelatudeDBServer server, DatamodelOverrides? active, DatamodelOverrides? draft, Datamodel draftModel) {
        if (Same(active, draft)) return null;
        var exists = Exists(server);
        DatamodelOverrides? content = null;
        if (draft != null) {
            content = Parse(Serialize(draft));
            content.RemoveEmpty();
            content.UpdateNames(draftModel);
            if (content.IsEmpty) content = null;
        }
        var path = DiskPath ?? "provider:" + IoId + "/" + IoKey!.AsKeyString();
        return new PlannedFile {
            SourceId = PlanId,
            Path = path,
            RelativePath = DiskPath != null ? Path.GetFileName(DiskPath) : IoKey!.AsKeyString(),
            Action = content == null ? PlannedFileAction.Delete : PlannedFileAction.Write,
            Content = content == null ? null : Serialize(content),
            Exists = exists,
            Changed = content != null || exists,
            IoId = DiskPath == null ? IoId : null,
            IoKey = DiskPath == null ? IoKey : null,
        };
    }
}
