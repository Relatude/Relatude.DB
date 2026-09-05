using Relatude.DB.Datamodels;
using Relatude.DB.IO;

namespace Relatude.DB.NodeServer.ModelEditor;

public sealed class DatamodelActivationResult {
    public required DatamodelValidation Validation { get; init; }
    /// <summary>The draft has warnings and the caller did not accept them; nothing was done.</summary>
    public bool NeedsConfirmation { get; set; }
    /// <summary>The sources now say what the draft says and the draft is gone.</summary>
    public bool Activated { get; set; }
    /// <summary>The sources were written but are compiled into the application: rebuild and restart to finish.</summary>
    public bool AwaitingRebuild { get; set; }
    public bool Reopened { get; set; }
    public bool SettingsChanged { get; set; }
    public List<string> FilesWritten { get; } = [];
    public List<string> FilesDeleted { get; } = [];
    /// <summary>Whether reloading the written sources gives exactly the draft; null when not checked.</summary>
    public bool? ChecksumMatches { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// Makes a draft the active model: validates it (with the dry run), keeps the model it replaces in
/// the history, writes the planned files, brings the settings' source list in line with the draft's,
/// and then either reopens the database with the new model or - when a compiled source was written -
/// leaves the draft marked as waiting for a rebuild. Nothing is written while the draft has errors.
/// </summary>
public sealed class DatamodelActivator {
    readonly RelatudeDBServer _server;
    readonly NodeStoreContainer _container;
    readonly DatamodelDrafts _drafts;
    public DatamodelActivator(RelatudeDBServer server, NodeStoreContainer container, DatamodelDrafts drafts) {
        _server = server;
        _container = container;
        _drafts = drafts;
    }

    public DatamodelActivationResult Activate(string draftJson, bool acceptWarnings, string? note) {
        var validation = new DatamodelValidator(_server, _container).Validate(draftJson, dryRun: true);
        var result = new DatamodelActivationResult { Validation = validation };
        if (validation.HasErrors) {
            result.Message = "The draft has errors and was not activated. ";
            return result;
        }
        if (validation.HasWarnings && !acceptWarnings) {
            result.NeedsConfirmation = true;
            return result;
        }
        var plan = validation.Plan!;
        var draft = validation.Draft!;
        var active = validation.Active!;

        // 1. the model being replaced goes into the history first, so it is there whatever happens next
        _drafts.Snapshot(active, "replaced");

        // 2. the files: deletes first, so a generated folder is emptied before it is filled again
        foreach (var file in plan.Files.Where(f => f.Changed).OrderBy(f => f.Action == PlannedFileAction.Delete ? 0 : 1)) {
            if (file.IoId != null && file.IoKey != null) {
                var io = _server.GetIO(file.IoId.Value);
                if (file.Action == PlannedFileAction.Delete) io.DeleteFileIfItExists(file.IoKey);
                else io.WriteAllTextUTF8(file.IoKey, file.Content ?? "");
            } else if (file.Action == PlannedFileAction.Delete) {
                if (File.Exists(file.Path)) File.Delete(file.Path);
            } else {
                Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
                File.WriteAllText(file.Path, file.Content ?? "");
            }
            (file.Action == PlannedFileAction.Delete ? result.FilesDeleted : result.FilesWritten).Add(file.Path);
        }

        // 3. the source list in the settings follows the draft's
        if (plan.SettingsChange) {
            SyncSources(_container.Settings, draft.Sources);
            _server.UpdateWAFServerSettingsFile();
            result.SettingsChanged = true;
        }

        // 4. compiled sources: the model is on disk, the running application still has the old one
        if (plan.RequiresRebuild) {
            var existing = _drafts.PeekDraft();
            _drafts.SaveDraft(new DatamodelDraft {
                Model = DatamodelJson.Deserialize(draftJson),
                Checksum = validation.DraftChecksum,
                BaseChecksum = existing?.BaseChecksum ?? validation.ActiveChecksum,
                AwaitingRebuild = true,
                AwaitingRebuildSinceUtc = DateTime.UtcNow,
                Note = note ?? existing?.Note,
            });
            result.AwaitingRebuild = true;
            result.Message = "The model was written into the source code. Rebuild and restart the application to activate it; the draft is kept until then. ";
            return result;
        }

        // 5. the proof: the sources now load as the draft
        Datamodel reloaded;
        try {
            reloaded = _container.LoadDatamodelFromSettings();
            reloaded.EnsureInitalization();
        } catch (Exception error) {
            result.Message = "The files were written, but loading the sources back failed: " + error.Message + " The draft is kept; fix the sources or the draft and activate again. ";
            return result;
        }
        var reloadedChecksum = DatamodelJson.Checksum(reloaded);
        result.ChecksumMatches = reloadedChecksum == validation.DraftChecksum;
        if (result.ChecksumMatches == true) {
            _drafts.DeleteDraft();
        } else {
            result.Message = "The files were written, but the sources load as a model that differs from the draft (see the round trip warnings). The database uses what the sources say; the draft is kept for comparison. ";
        }

        // 6. the running database picks the new model up by reopening
        if (_container.IsOpenOrOpening()) {
            _container.ApplyNewSettings(_container.Settings, reopenIfOpen: true);
            result.Reopened = true;
        }
        result.Activated = true;
        result.Message ??= _container.IsOpen()
            ? "The model is active. "
            : "The sources are written; the database is closed and will use the new model when it opens. ";
        return result;
    }

    /// <summary>Makes the settings' source list say what the draft's does: same sources, same definitions, same order.</summary>
    public static void SyncSources(Settings.NodeStoreContainerSettings settings, List<DatamodelSource> draftSources) {
        var existing = (settings.DatamodelSources ?? []).ToDictionary(s => s.Id);
        var list = new List<DatamodelSource>();
        foreach (var ds in draftSources.Where(s => s.Type != DatamodelSourceType.Code)) {
            if (!existing.TryGetValue(ds.Id, out var target)) target = new DatamodelSource { Id = ds.Id };
            target.Name = ds.Name;
            target.Namespace = ds.Namespace;
            target.Type = ds.Type;
            target.FileFormat = ds.FileFormat;
            target.Filepath = ds.Filepath;
            target.Reference = ds.Reference;
            target.FileIO = ds.FileIO;
            target.SourceCodePath = ds.SourceCodePath;
            target.GenerateModelFile = ds.GenerateModelFile;
            target.Enabled = ds.Enabled;
            target.Color = ds.Color;
            list.Add(target);
        }
        settings.DatamodelSources = list.ToArray();
    }
}
