using System.Text.Json;
using Relatude.DB.CodeGeneration;
using Relatude.DB.Datamodels;
using Relatude.DB.IO;
using Relatude.DB.NodeServer.ModelEditor;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// The data model editor of one database. The model travels as the JSON <see cref="DatamodelJson"/>
/// writes (a JsonElement inside the responses, so its property names are the model's own rather than
/// camelCased): the client edits that object and posts it back whole as the draft, and the server
/// never has to know which field changed. Drafts and history live in the database's primary storage
/// provider (<see cref="DatamodelDrafts"/>); validation and activation are
/// <see cref="DatamodelValidator"/> and <see cref="DatamodelActivator"/>.
/// </summary>
sealed class UIDatamodel {
    readonly RelatudeDBServer _server;
    static readonly QueryContext countContext = QueryContext.Default.Admin().Hidden().Unpublished().CultureFallbacks();
    internal UIDatamodel(RelatudeDBServer server) => _server = server;

    internal void Register(UICommands commands) {
        commands.Register("datamodel", ctx => get(ctx.Payload<StorePayload>().StoreId));
        commands.Register("datamodel-schema", ctx => DatamodelCatalog.Schema);
        commands.Register("datamodel-draft-save", ctx => saveDraft(ctx.Payload<DraftPayload>()));
        commands.Register("datamodel-draft-discard", ctx => discardDraft(ctx.Payload<StorePayload>().StoreId));
        commands.Register("datamodel-validate", ctx => validate(ctx.Payload<ValidatePayload>()));
        commands.Register("datamodel-activate", ctx => activate(ctx.Payload<ActivatePayload>()));
        commands.Register("datamodel-history-load", ctx => historyLoad(ctx.Payload<HistoryPayload>()));
        commands.Register("datamodel-history-delete", ctx => historyDelete(ctx.Payload<HistoryPayload>()));
        commands.Register("datamodel-export", ctx => export(ctx.Payload<ExportPayload>()));
        // the type reference form: process wide lookups, on demand (see AssemblyScanner)
        commands.Register("datamodel-scan-assemblies", ctx => AssemblyScanner.ScanAssemblies());
        commands.Register("datamodel-scan-namespaces", ctx => AssemblyScanner.ScanNamespaces(ctx.Payload<ReferencePayload>().Reference));
        commands.Register("datamodel-probe-types", ctx => { var p = ctx.Payload<ProbePayload>(); return AssemblyScanner.Probe(p.Reference, p.Namespace); });
    }

    NodeStoreContainer container(Guid storeId) {
        if (!_server.Containers.TryGetValue(storeId, out var c)) throw new Exception("Database not found. ");
        return c;
    }
    DatamodelDrafts drafts(NodeStoreContainer c) {
        var io = _server.GetOrNullIO(c.Settings.IoDatabase);
        if (io == null) throw new Exception("The database has no primary storage provider (IoDatabase), so there is nowhere to keep a draft. ");
        return new DatamodelDrafts(io);
    }

    // ---- the page ----

    object get(Guid storeId) {
        var c = container(storeId);
        var state = c.HasFailed ? "Error" : c.Store?.State.ToString() ?? "Closed";
        Datamodel? active = null;
        string? activeError = null;
        try {
            active = new DatamodelValidator(_server, c).LoadActive();
        } catch (Exception error) {
            activeError = error.Message;
        }
        DatamodelDrafts? store = null;
        string? draftError = null;
        try { store = drafts(c); } catch (Exception error) { draftError = error.Message; }
        DatamodelDraft? draft = null;
        List<DatamodelHistoryEntry> history = [];
        if (store != null) {
            try { draft = store.LoadDraft(); } catch (Exception error) { draftError = "The draft could not be read: " + error.Message; }
            try { history = store.ListHistory(); } catch { }
        }
        var overlay = _server.ConfigurationOverlay;
        var sourcesLocked = overlay != null && overlay.IsOverridden(Settings.SettingsOverlay.OverridePath(c.Settings.Id, "DatamodelSources"), out _);
        return new {
            StoreId = storeId,
            Open = c.IsOpen(),
            State = state,
            RootFolder = _server.RootDataFolderPath,
            BaseTypeId = NodeConstants.BaseNodeTypeId,
            CodeSourceId = DatamodelSource.CodeSourceId,
            Active = active == null ? null : new { Checksum = DatamodelJson.Checksum(active), Model = element(active) },
            ActiveError = activeError,
            Draft = draft == null ? null : describeDraft(draft, withModel: true),
            DraftError = draftError,
            History = history.Select(describeHistory),
            TypeCounts = typeCounts(c, active),
            Sources = describeSources(c, active),
            SourcesLocked = sourcesLocked,
            IoProviders = (c.Settings.IOSettings ?? []).Select(io => new { io.Id, io.Name }),
        };
    }
    static JsonElement element(Datamodel model) {
        using var document = JsonDocument.Parse(DatamodelJson.Serialize(model));
        return document.RootElement.Clone();
    }
    static object describeDraft(DatamodelDraft draft, bool withModel) => new {
        draft.SavedUtc,
        draft.Checksum,
        draft.BaseChecksum,
        draft.AwaitingRebuild,
        draft.AwaitingRebuildSinceUtc,
        draft.Note,
        Model = withModel ? element(draft.Model) : (JsonElement?)null,
    };
    static object describeHistory(DatamodelHistoryEntry e) => new { e.Key, e.SavedUtc, e.Reason, e.Checksum, e.NodeTypes, e.Relations, e.Properties, e.Size };
    static Dictionary<Guid, long> typeCounts(NodeStoreContainer c, Datamodel? active) {
        var counts = new Dictionary<Guid, long>();
        if (active == null || !c.IsOpen() || c.Store == null) return counts;
        foreach (var t in active.NodeTypes.Values.Where(t => !t.IsInnerNode)) {
            try { counts[t.Id] = c.Store.QueryType(t.Id, countContext).Count(); } catch { }
        }
        return counts;
    }
    object[] describeSources(NodeStoreContainer c, Datamodel? active) {
        var root = _server.RootDataFolderPath;
        var settings = (c.Settings.DatamodelSources ?? []).ToList();
        var all = settings.Select(s => (source: s, inSettings: true)).ToList();
        if (active != null) {
            foreach (var s in active.Sources) if (!settings.Any(x => x.Id == s.Id)) all.Add((s, false));
        }
        return all.Select(x => {
            var s = x.source;
            var (writable, reason, rebuild) = DatamodelSourceWriter.Writability(s, root);
            string? resolved = null;
            bool? exists = null;
            try {
                if (s.Type == DatamodelSourceType.TypeReference) {
                    resolved = DatamodelSourceLoader.ResolveSourceCodeFolder(s, root);
                    if (resolved != null) exists = Directory.Exists(resolved);
                } else if (s.IsJsonFiles && s.FileIO != null) {
                    resolved = "provider " + s.FileIO + " / " + s.Reference;
                } else if (s.Type == DatamodelSourceType.TextFiles) {
                    resolved = DatamodelSourceLoader.ResolveFilePath(s, root, DatamodelSourceLoader.DefaultFolder(s));
                    exists = File.Exists(resolved) || Directory.Exists(resolved);
                }
            } catch { }
            var types = active?.NodeTypes.Values.Where(t => t.DatamodelSourceId == s.Id && t.Id != NodeConstants.BaseNodeTypeId).ToList() ?? [];
            var relations = active?.Relations.Values.Where(r => r.DatamodelSourceId == s.Id).ToList() ?? [];
            return (object)new {
                s.Id,
                Name = string.IsNullOrEmpty(s.Name) ? (s.Type == DatamodelSourceType.Code ? "Code" : s.Id.ToString()) : s.Name,
                Type = s.Type.ToString(),
                FileFormat = s.FileFormat.ToString(),
                s.Enabled,
                s.Namespace,
                s.Filepath,
                s.Reference,
                s.FileIO,
                s.SourceCodePath,
                s.GenerateModelFile,
                s.Color,
                IsCode = s.Type == DatamodelSourceType.Code,
                InSettings = x.inSettings,
                Writable = writable,
                ReadOnlyReason = reason,
                RequiresRebuild = rebuild,
                ResolvedPath = resolved,
                PathExists = exists,
                TypeCount = types.Count,
                RelationCount = relations.Count,
                Files = types.Select(t => t.DatamodelSourceFilename).Concat(relations.Select(r => r.DatamodelSourceFilename))
                    .Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase),
            };
        }).ToArray();
    }

    // ---- the draft ----

    object saveDraft(DraftPayload p) {
        var c = container(p.StoreId);
        var store = drafts(c);
        var json = p.Model.GetRawText();
        var model = DatamodelJson.Deserialize(json); // throws on malformed input before anything is written
        Guid checksum;
        try {
            var copy = DatamodelJson.Deserialize(json);
            copy.EnsureInitalization();
            checksum = DatamodelJson.Checksum(copy);
        } catch {
            checksum = DatamodelJson.Checksum(model); // a draft in progress need not initialize yet
        }
        var existing = store.PeekDraft();
        Guid? baseChecksum = existing?.BaseChecksum;
        if (baseChecksum == null) {
            try { baseChecksum = DatamodelJson.Checksum(new DatamodelValidator(_server, c).LoadActive()); } catch { }
        }
        var draft = new DatamodelDraft {
            Model = model,
            Checksum = checksum,
            BaseChecksum = baseChecksum,
            AwaitingRebuild = false, // an edit after a write means the written model is no longer the draft
            Note = p.Note ?? existing?.Note,
        };
        store.SaveDraft(draft);
        return describeDraft(draft, withModel: false);
    }
    object discardDraft(Guid storeId) {
        var c = container(storeId);
        drafts(c).DeleteDraft();
        return new { Ok = true };
    }

    // ---- checking and activating ----

    object validate(ValidatePayload p) {
        var c = container(p.StoreId);
        var validation = new DatamodelValidator(_server, c).Validate(p.Model.GetRawText(), dryRun: p.DryRun);
        return describeValidation(validation);
    }
    object activate(ActivatePayload p) {
        var c = container(p.StoreId);
        var result = new DatamodelActivator(_server, c, drafts(c)).Activate(p.Model.GetRawText(), p.AcceptWarnings, p.Note);
        return new {
            Validation = describeValidation(result.Validation),
            result.NeedsConfirmation,
            result.Activated,
            result.AwaitingRebuild,
            result.Reopened,
            result.SettingsChanged,
            result.FilesWritten,
            result.FilesDeleted,
            result.ChecksumMatches,
            result.Message,
        };
    }
    static object describeValidation(DatamodelValidation v) => new {
        Issues = v.Issues.Select(i => new { Severity = i.Severity.ToString().ToLower(), i.Code, i.Message, i.NodeTypeId, i.PropertyId, i.RelationId, i.SourceId, i.File }),
        v.HasErrors,
        v.HasWarnings,
        v.Compiled,
        v.RequiresRebuild,
        v.DraftChecksum,
        v.ActiveChecksum,
        Plan = v.Plan == null ? null : new {
            v.Plan.SettingsChange,
            v.Plan.RequiresRebuild,
            Sources = v.Plan.Sources.Select(s => new {
                s.SourceId, s.Name, Type = s.Type.ToString(), s.Writable, s.ReadOnlyReason, s.RequiresRebuild, s.Removed, s.Added,
                s.AddedTypes, s.ChangedTypes, s.RemovedTypes, s.AddedRelations, s.ChangedRelations, s.RemovedRelations, s.HasModelChanges,
            }),
            Files = v.Plan.Files.Select(f => new {
                f.SourceId, f.Path, f.RelativePath, Action = f.Action.ToString().ToLower(), f.Exists, f.Changed, f.NodeTypeIds, f.RelationIds, f.Content,
            }),
        },
    };

    // ---- history ----

    object historyLoad(HistoryPayload p) {
        var c = container(p.StoreId);
        var entry = drafts(c).LoadHistory(p.Key) ?? throw new Exception("The history entry was not found. ");
        return new { entry.Key, entry.SavedUtc, entry.Reason, entry.Checksum, entry.NodeTypes, entry.Relations, entry.Properties, Model = element(entry.Model!) };
    }
    object historyDelete(HistoryPayload p) {
        var c = container(p.StoreId);
        return new { Deleted = drafts(c).DeleteHistory(p.Key) };
    }

    // ---- export ----

    object export(ExportPayload p) {
        var c = container(p.StoreId);
        Datamodel model;
        if (p.Model != null) {
            model = DatamodelJson.Deserialize(p.Model.Value.GetRawText());
        } else {
            model = new DatamodelValidator(_server, c).LoadActive();
        }
        var name = string.IsNullOrEmpty(c.Settings.Name) ? "datamodel" : c.Settings.Name.Replace(' ', '-').ToLowerInvariant();
        if (string.Equals(p.Format, "csharp", StringComparison.OrdinalIgnoreCase)) {
            model.EnsureInitalization();
            var code = ModelGen.GenerateCSharpModelCode(model, p.Attributes ?? true);
            return new { Content = code, FileName = name + ".cs", ContentType = "text/plain" };
        }
        return new { Content = DatamodelJson.Serialize(model), FileName = name + ".json", ContentType = "application/json" };
    }

    sealed record StorePayload(Guid StoreId);
    sealed record DraftPayload(Guid StoreId, JsonElement Model, string? Note);
    sealed record ValidatePayload(Guid StoreId, JsonElement Model, bool DryRun = true);
    sealed record ActivatePayload(Guid StoreId, JsonElement Model, bool AcceptWarnings, string? Note);
    sealed record HistoryPayload(Guid StoreId, string Key);
    sealed record ExportPayload(Guid StoreId, JsonElement? Model, string Format, bool? Attributes);
    sealed record ReferencePayload(string? Reference);
    sealed record ProbePayload(string? Reference, string? Namespace);
}
