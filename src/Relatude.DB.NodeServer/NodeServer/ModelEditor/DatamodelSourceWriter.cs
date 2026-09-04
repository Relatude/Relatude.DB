using System.Text.Json;
using Relatude.DB.CodeGeneration;
using Relatude.DB.Datamodels;
using Relatude.DB.IO;

namespace Relatude.DB.NodeServer.ModelEditor;

/// <summary>
/// Works out how a draft model is written back into the datamodel sources it came from, as a plan
/// of files to write and delete, without touching anything. The plan is what the editor shows before
/// an activation and what <see cref="DatamodelActivator"/> then carries out.
///
/// A source is written as a whole: every file that holds model types is regenerated from the draft
/// (JSON files by <see cref="DatamodelJson.SerializeForSourceFile"/>, C# files by
/// <see cref="ModelGen"/>), so hand-written members in a C# model file do not survive - that is the
/// deal the editor makes, and the plan says which files it touches. A file is only rewritten when
/// its content actually changes, and a file that used to hold model types and would end up holding
/// none is deleted, since leaving it would put the removed types straight back at the next open.
/// Files nothing in the model points at - an enum, a helper - are never touched.
/// </summary>
public static class DatamodelSourceWriter {

    /// <summary>
    /// Whether the editor can write into the source, why not, and whether a write only takes effect
    /// after the application is rebuilt (the compiled kinds).
    /// </summary>
    public static (bool writable, string? reason, bool requiresRebuild) Writability(DatamodelSource source, string rootFolder) {
        switch (source.Type) {
            case DatamodelSourceType.Code:
                return (false, "These types are registered from application code (OnDatamodelInit); change the code instead.", false);
            case DatamodelSourceType.JsonFile:
            case DatamodelSourceType.CSharpCodeFile:
                return (true, null, false);
            case DatamodelSourceType.AssemblyNameReference:
            case DatamodelSourceType.TypeNameReference: {
                    var folder = DatamodelSourceLoader.ResolveSourceCodeFolder(source, rootFolder);
                    if (folder == null) return (false, "The types are compiled into the application and the source has no source code folder set, so there is nowhere to write them.", false);
                    if (!Directory.Exists(folder)) return (false, "The source code folder \"" + folder + "\" does not exist.", false);
                    return (true, null, true);
                }
            default:
                return (false, "Unknown source type.", false);
        }
    }

    /// <summary>
    /// Plans the writes that turn the sources from what <paramref name="active"/> says into what
    /// <paramref name="draft"/> says. Both models must be initialized. Read only sources whose types
    /// differ, types without a source and types in a disabled source come back as errors in the plan.
    /// </summary>
    public static SourceWritePlan Plan(Datamodel active, Datamodel draft, string rootFolder, Func<Guid, IIOProvider?> resolveIO) {
        var plan = new SourceWritePlan();
        var activeTypes = active.NodeTypes.Values.Where(t => t.Id != NodeConstants.BaseNodeTypeId).ToDictionary(t => t.Id);
        var draftTypes = draft.NodeTypes.Values.Where(t => t.Id != NodeConstants.BaseNodeTypeId).ToDictionary(t => t.Id);
        var sources = draft.Sources.ToList();
        // types added from code are tagged with the synthetic code source, which a client may have dropped
        // from the source list; it is read only either way
        if (!sources.Any(s => s.Id == DatamodelSource.CodeSourceId)
            && (draftTypes.Values.Any(t => t.DatamodelSourceId == DatamodelSource.CodeSourceId) || draft.Relations.Values.Any(r => r.DatamodelSourceId == DatamodelSource.CodeSourceId))) {
            sources.Add(DatamodelSource.CreateCodeSource());
        }
        var sourceIds = sources.Select(s => s.Id).ToHashSet();

        foreach (var source in sources) {
            var activeSource = active.Sources.FirstOrDefault(s => s.Id == source.Id);
            var (writable, reason, requiresRebuild) = Writability(source, rootFolder);
            var change = new SourceChange {
                SourceId = source.Id,
                Name = string.IsNullOrEmpty(source.Name) ? source.Id.ToString() : source.Name,
                Type = source.Type,
                Writable = writable,
                ReadOnlyReason = reason,
                RequiresRebuild = requiresRebuild,
                Added = activeSource == null && source.Type != DatamodelSourceType.Code,
            };
            var dTypes = draftTypes.Values.Where(t => t.DatamodelSourceId == source.Id).ToList();
            var aTypes = activeTypes.Values.Where(t => t.DatamodelSourceId == source.Id).ToList();
            var dRelations = draft.Relations.Values.Where(r => r.DatamodelSourceId == source.Id).ToList();
            var aRelations = active.Relations.Values.Where(r => r.DatamodelSourceId == source.Id).ToList();
            foreach (var t in dTypes) {
                if (!activeTypes.TryGetValue(t.Id, out var a) || a.DatamodelSourceId != source.Id) change.AddedTypes.Add(t.Id);
                else if (Fingerprint(a) != Fingerprint(t)) change.ChangedTypes.Add(t.Id);
            }
            foreach (var a in aTypes) {
                if (!draftTypes.TryGetValue(a.Id, out var d) || d.DatamodelSourceId != source.Id) change.RemovedTypes.Add(a.Id);
            }
            foreach (var r in dRelations) {
                if (!active.Relations.TryGetValue(r.Id, out var a) || a.DatamodelSourceId != source.Id) change.AddedRelations.Add(r.Id);
                else if (Fingerprint(a) != Fingerprint(r)) change.ChangedRelations.Add(r.Id);
            }
            foreach (var a in aRelations) {
                if (!draft.Relations.TryGetValue(a.Id, out var d) || d.DatamodelSourceId != source.Id) change.RemovedRelations.Add(a.Id);
            }
            plan.Sources.Add(change);
            if (source.Type != DatamodelSourceType.Code) {
                if (activeSource == null || definition(activeSource) != definition(source)) plan.SettingsChange = true;
            }
            if (!source.Enabled) {
                foreach (var t in dTypes) plan.Issues.Add(DatamodelIssue.Error("source-disabled", "The type " + t.FullName + " belongs to the source \"" + change.Name + "\", which is turned off. Turn the source on or move the type to another source.", nodeType: t.Id, source: source.Id));
                foreach (var r in dRelations) plan.Issues.Add(DatamodelIssue.Error("source-disabled", "The relation " + r.FullName() + " belongs to the source \"" + change.Name + "\", which is turned off.", relation: r.Id, source: source.Id));
                continue;
            }
            if (!change.HasModelChanges) continue;
            if (!writable) {
                foreach (var id in change.AddedTypes.Concat(change.ChangedTypes)) {
                    var t = draftTypes[id];
                    plan.Issues.Add(DatamodelIssue.Error("read-only-source", "The type " + t.FullName + " is " + (change.AddedTypes.Contains(id) ? "added to" : "changed in") + " the source \"" + change.Name + "\", which cannot be written: " + reason, nodeType: id, source: source.Id));
                }
                foreach (var id in change.RemovedTypes) {
                    plan.Issues.Add(DatamodelIssue.Error("read-only-source", "The type " + activeTypes[id].FullName + " is removed from the source \"" + change.Name + "\", which cannot be written: " + reason, nodeType: id, source: source.Id));
                }
                foreach (var id in change.AddedRelations.Concat(change.ChangedRelations)) {
                    plan.Issues.Add(DatamodelIssue.Error("read-only-source", "The relation " + draft.Relations[id].FullName() + " is " + (change.AddedRelations.Contains(id) ? "added to" : "changed in") + " the source \"" + change.Name + "\", which cannot be written: " + reason, relation: id, source: source.Id));
                }
                foreach (var id in change.RemovedRelations) {
                    plan.Issues.Add(DatamodelIssue.Error("read-only-source", "The relation " + active.Relations[id].FullName() + " is removed from the source \"" + change.Name + "\", which cannot be written: " + reason, relation: id, source: source.Id));
                }
                continue;
            }
            try {
                planFiles(plan, source, change, draft, dTypes, dRelations, aTypes, aRelations, rootFolder, resolveIO);
            } catch (Exception error) {
                plan.Issues.Add(DatamodelIssue.Error("write-plan", "The source \"" + change.Name + "\" cannot be written: " + error.Message, source: source.Id));
            }
        }
        // sources the draft dropped: they leave the settings, their files stay where they are
        foreach (var activeSource in active.Sources) {
            if (activeSource.Type == DatamodelSourceType.Code || sourceIds.Contains(activeSource.Id)) continue;
            var change = new SourceChange {
                SourceId = activeSource.Id,
                Name = string.IsNullOrEmpty(activeSource.Name) ? activeSource.Id.ToString() : activeSource.Name,
                Type = activeSource.Type,
                Removed = true,
            };
            change.RemovedTypes.AddRange(activeTypes.Values.Where(t => t.DatamodelSourceId == activeSource.Id).Select(t => t.Id));
            change.RemovedRelations.AddRange(active.Relations.Values.Where(r => r.DatamodelSourceId == activeSource.Id).Select(r => r.Id));
            plan.Sources.Add(change);
            plan.SettingsChange = true;
        }
        // types that point at no source at all
        foreach (var t in draftTypes.Values.Where(t => !sourceIds.Contains(t.DatamodelSourceId))) {
            plan.Issues.Add(DatamodelIssue.Error("unknown-source", "The type " + t.FullName + " belongs to a source (" + t.DatamodelSourceId + ") that is not in the model. Pick a source for it.", nodeType: t.Id));
        }
        foreach (var r in draft.Relations.Values.Where(r => !sourceIds.Contains(r.DatamodelSourceId))) {
            plan.Issues.Add(DatamodelIssue.Error("unknown-source", "The relation " + r.FullName() + " belongs to a source (" + r.DatamodelSourceId + ") that is not in the model. Pick a source for it.", relation: r.Id));
        }
        return plan;
    }

    /// <summary>What a type or relation says, without where it came from. Equal fingerprints mean nothing to write.</summary>
    public static string Fingerprint(NodeTypeModel type) => JsonSerializer.Serialize(type, DatamodelJson.CompareOptions);
    public static string Fingerprint(RelationModel relation) => JsonSerializer.Serialize(relation, DatamodelJson.CompareOptions);
    static string definition(DatamodelSource source) => JsonSerializer.Serialize(source, DatamodelJson.Options);

    static void planFiles(SourceWritePlan plan, DatamodelSource source, SourceChange change, Datamodel draft,
        List<NodeTypeModel> dTypes, List<RelationModel> dRelations, List<NodeTypeModel> aTypes, List<RelationModel> aRelations,
        string rootFolder, Func<Guid, IIOProvider?> resolveIO) {
        var isJson = source.Type == DatamodelSourceType.JsonFile;
        var ext = isJson ? ".json" : ".cs";
        // what makes a file worth rewriting: a type or relation in it that changed or is new, or one
        // that arrived from or left for another file. A file whose members are all unchanged is left
        // exactly as it is - hand-written comments and formatting included
        var changedIds = change.AddedTypes.Concat(change.ChangedTypes).Concat(change.AddedRelations).Concat(change.ChangedRelations).ToHashSet();
        if (isJson && source.FileIO != null) {
            // the legacy shape: one JSON file read through a storage provider
            var io = resolveIO(source.FileIO.Value) ?? throw new Exception("No IO provider with id " + source.FileIO.Value + " is configured. ");
            if (string.IsNullOrEmpty(source.Reference)) throw new Exception("The source has no Reference naming the file to read from the provider. ");
            var key = source.Reference.SplitKey();
            var content = DatamodelJson.SerializeForSourceFile(draft, dTypes.Select(t => t.Id), dRelations.Select(r => r.Id));
            var exists = io.ExistsAndIsNotEmpty(key);
            var existing = exists ? io.ReadAllTextUTF8(key) : null;
            plan.Files.Add(new PlannedFile {
                SourceId = source.Id, Path = "provider:" + source.FileIO.Value + "/" + source.Reference, RelativePath = source.Reference,
                Action = PlannedFileAction.Write, Content = content, Exists = exists, Changed = !sameText(existing, content),
                NodeTypeIds = dTypes.Select(t => t.Id).ToList(), RelationIds = dRelations.Select(r => r.Id).ToList(),
                IoId = source.FileIO, IoKey = key,
            });
            return;
        }
        string baseFolder;
        string? singleFile = null;
        // for a compiled source, the declaring files by full type name: the loader stamps them on the
        // model at open, but the code folder may have been set in this very draft, in which case the
        // active model carries no file names and the folder is read here instead
        Dictionary<string, string>? fileByFullName = null;
        if (source.Type is DatamodelSourceType.AssemblyNameReference or DatamodelSourceType.TypeNameReference) {
            baseFolder = DatamodelSourceLoader.ResolveSourceCodeFolder(source, rootFolder)!;
            fileByFullName = ModelSourceFiles.MapTypesToFiles(baseFolder);
        } else {
            var target = DatamodelSourceLoader.ResolveFilePath(source, rootFolder, isJson ? DatamodelSourceLoader.DefaultJsonFolder : DatamodelSourceLoader.DefaultCSharpFolder);
            if (File.Exists(target) || (!Directory.Exists(target) && target.EndsWith(ext, StringComparison.OrdinalIgnoreCase))) {
                singleFile = Path.GetFileName(target);
                baseFolder = Path.GetDirectoryName(target)!;
            } else {
                baseFolder = target;
            }
        }
        // the files the active model knows hold this source's types: the only ones a delete may touch
        string? declaredIn(string fullName) {
            if (fileByFullName == null || !fileByFullName.TryGetValue(fullName, out var file)) return null;
            return Path.GetRelativePath(baseFolder, file);
        }
        var activeIdsByFile = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        void knownIn(string? file, Guid id) {
            if (string.IsNullOrEmpty(file)) return;
            var key = normalize(file);
            if (!activeIdsByFile.TryGetValue(key, out var set)) activeIdsByFile[key] = set = [];
            set.Add(id);
        }
        foreach (var t in aTypes) knownIn(t.DatamodelSourceFilename ?? declaredIn(t.FullName), t.Id);
        foreach (var r in aRelations) knownIn(r.DatamodelSourceFilename ?? declaredIn(r.FullName()), r.Id);
        var activeFiles = activeIdsByFile.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // where a type that has no file yet goes: next to the other types of its namespace, else next
        // to the source's other types, else in the folder root
        string subfolderFor(string? ns) {
            var stamped = dTypes.Where(t => trusted(t.DatamodelSourceFilename, ext)).ToList();
            var candidates = stamped.Where(t => string.Equals(t.Namespace ?? "", ns ?? "", StringComparison.Ordinal)).ToList();
            if (candidates.Count == 0) candidates = stamped;
            if (candidates.Count == 0) return "";
            return candidates.GroupBy(t => Path.GetDirectoryName(normalize(t.DatamodelSourceFilename!)) ?? "", StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).First().Key;
        }
        string fileFor(string? stamped, string codeName, string? ns) {
            if (singleFile != null) return singleFile;
            if (trusted(stamped, ext)) return normalize(stamped!);
            var relative = normalize(Path.Combine(subfolderFor(ns), codeName + ext));
            // a file of that name that holds no model type is somebody else's file: leave it alone
            if (File.Exists(Path.Combine(baseFolder, relative)) && !activeFiles.Contains(relative)) relative = normalize(Path.Combine(subfolderFor(ns), codeName + ".model" + ext));
            return relative;
        }
        var byFile = new Dictionary<string, (List<Guid> types, List<Guid> relations)>(StringComparer.OrdinalIgnoreCase);
        (List<Guid> types, List<Guid> relations) bucket(string file) {
            if (!byFile.TryGetValue(file, out var b)) byFile[file] = b = ([], []);
            return b;
        }
        foreach (var t in dTypes) bucket(fileFor(t.DatamodelSourceFilename ?? declaredIn(t.FullName), t.CodeName, t.Namespace)).types.Add(t.Id);
        foreach (var r in dRelations) bucket(fileFor(r.DatamodelSourceFilename ?? declaredIn(r.FullName()), r.CodeName, r.Namespace)).relations.Add(r.Id);

        foreach (var (relative, (typeIds, relationIds)) in byFile.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)) {
            var typeSet = typeIds.ToHashSet();
            var relationSet = relationIds.ToHashSet();
            var members = typeSet.Concat(relationSet).ToHashSet();
            var before = activeIdsByFile.TryGetValue(relative, out var known) ? known : [];
            var touched = members.Any(changedIds.Contains) || !members.SetEquals(before);
            if (!touched) continue;
            var content = isJson
                ? DatamodelJson.SerializeForSourceFile(draft, typeIds, relationIds)
                : ModelGen.GenerateCSharpModelCode(draft, true, t => typeSet.Contains(t.Id), r => relationSet.Contains(r.Id));
            var path = Path.Combine(baseFolder, relative);
            var exists = File.Exists(path);
            var existing = exists ? File.ReadAllText(path) : null;
            plan.Files.Add(new PlannedFile {
                SourceId = source.Id, Path = path, RelativePath = relative, Action = PlannedFileAction.Write,
                Content = content, Exists = exists, Changed = !sameText(existing, content),
                NodeTypeIds = typeIds, RelationIds = relationIds,
            });
        }
        foreach (var relative in activeFiles.Where(f => !byFile.ContainsKey(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
            var path = Path.Combine(baseFolder, relative);
            if (!File.Exists(path)) continue;
            plan.Files.Add(new PlannedFile {
                SourceId = source.Id, Path = path, RelativePath = relative, Action = PlannedFileAction.Delete, Exists = true, Changed = true,
            });
        }
    }
    /// <summary>A stamped file name the plan can use: relative, inside the folder, with the right extension.</summary>
    static bool trusted(string? stamped, string ext) {
        if (string.IsNullOrEmpty(stamped)) return false;
        if (Path.IsPathRooted(stamped)) return false;
        if (!stamped.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return false;
        return !stamped.Split('/', '\\').Any(part => part == "..");
    }
    static string normalize(string relative) => relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
    static bool sameText(string? existing, string content) {
        if (existing == null) return false;
        return existing.Replace("\r\n", "\n").TrimEnd() == content.Replace("\r\n", "\n").TrimEnd();
    }
}
