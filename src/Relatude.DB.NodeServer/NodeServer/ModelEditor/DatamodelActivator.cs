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
    /// <summary>The database's overrides file was written (or removed).</summary>
    public bool OverridesChanged { get; set; }
    /// <summary>How many nodes had their indexed text emptied because their type stopped being text indexed.</summary>
    public int TextCleared { get; set; }
    /// <summary>How many text indexing tasks were queued because what goes into a type's text changed.</summary>
    public int TextReindexQueued { get; set; }
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

        result.OverridesChanged = plan.OverridesChange;

        // 4. compiled sources: the model is on disk, the running application still has the old one
        if (plan.RequiresRebuild) {
            var existing = _drafts.PeekDraft();
            var kept = DatamodelJson.Deserialize(draftJson);
            // the path a renamed source was given in the settings (see the validator), or the next
            // activation of this draft would take it away again
            DatamodelSourceWriter.KeepDefaultPaths(DatamodelSourceWriter.DefaultPaths(active.Sources), kept.Sources);
            _drafts.SaveDraft(new DatamodelDraft {
                Model = kept,
                Checksum = validation.DraftChecksum,
                BaseChecksum = existing?.BaseChecksum ?? validation.ActiveChecksum,
                AwaitingRebuild = true,
                AwaitingRebuildSinceUtc = DateTime.UtcNow,
                FilesWritten = result.FilesWritten,
                FilesDeleted = result.FilesDeleted,
                Note = note ?? existing?.Note,
            });
            result.AwaitingRebuild = true;
            result.Message = "The model was written into source code that is compiled into the application. The running application keeps its current model until it is rebuilt and restarted. "
                + "The draft is kept, marked as waiting for a rebuild, and removed by itself when the database opens with the written model. ";
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

        // 6. a type that stops being text indexed has its text taken out first: the index only removes
        // the text of a node whose type it indexes, which after the reopen this type no longer is
        if (validation.TextIndexOffTypes.Count > 0 && _container.IsOpen() && _container.Store != null) {
            try {
                result.TextCleared = _container.Store.Datastore.ClearIndexedText(validation.TextIndexOffTypes);
            } catch (Exception error) {
                result.Message = "The model is written, but the text of the types no longer text indexed could not be taken out of the text index: " + error.Message
                    + " Their nodes stay findable by text search until the indexes are rebuilt. ";
            }
        }

        // 7. the running database picks the new model up by reopening
        if (_container.IsOpenOrOpening()) {
            _container.ApplyNewSettings(_container.Settings, reopenIfOpen: true);
            result.Reopened = true;
        }

        // 8. and nodes whose text is made differently now are indexed again
        if (validation.TextReindexTypes.Count > 0 && _container.IsOpen() && _container.Store != null) {
            try {
                result.TextReindexQueued = _container.Store.Datastore.ReIndexText(validation.TextReindexTypes);
            } catch (Exception error) {
                result.Message ??= "The model is active, but its nodes could not be queued for text indexing: " + error.Message + " Rebuild the text index from the database's page. ";
            }
        }
        result.Activated = true;
        result.Message ??= _container.IsOpen()
            ? "The model is active. "
                + (result.TextCleared > 0 ? "The text of " + result.TextCleared + " nodes was taken out of the text index. " : "")
                + (result.TextReindexQueued > 0 ? result.TextReindexQueued + " indexing task" + (result.TextReindexQueued == 1 ? " is" : "s are") + " queued for the types whose text changed. " : "")
            : "The sources are written; the database is closed and will use the new model when it opens. "
                + (validation.TextReindexTypes.Count > 0 || validation.TextIndexOffTypes.Count > 0 ? "What goes into the text index changes: rebuild the text index once it is open. " : "");
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
