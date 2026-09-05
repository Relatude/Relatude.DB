using System.Text.Json;
using Relatude.DB.CodeGeneration;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.IO;

namespace Relatude.DB.NodeServer.ModelEditor;

/// <summary>
/// Checks a draft model before it is activated, in three layers. First the structure, with every
/// finding reported at once rather than the first one the model's own initialization throws at;
/// then what writing it would do to the sources (<see cref="DatamodelSourceWriter.Plan"/>) and to
/// the stored data; and finally, on request, a dry run: the planned files are written to a scratch
/// folder, loaded the way the database loads its sources, and the mapper code the store would
/// generate from the result is compiled. A model that passes the dry run is one the database will
/// open with. The dry run also compares what comes back from the written files against the draft,
/// so a setting the code generator cannot express is reported before anything is written.
/// </summary>
public sealed class DatamodelValidator {
    readonly RelatudeDBServer _server;
    readonly NodeStoreContainer _container;
    static readonly QueryContext countContext = QueryContext.Default.Admin().Hidden().Unpublished().CultureFallbacks();
    public DatamodelValidator(RelatudeDBServer server, NodeStoreContainer container) {
        _server = server;
        _container = container;
    }

    /// <summary>The model the draft is compared against: what the configured sources say right now.
    /// The open database's model when there is one, else the sources loaded afresh.</summary>
    public Datamodel LoadActive() {
        var json = _container.DatamodelAsLoadedJson;
        var dm = json != null ? DatamodelJson.Deserialize(json) : _container.LoadDatamodelFromSettings();
        dm.EnsureInitalization();
        return dm;
    }

    public DatamodelValidation Validate(string draftJson, bool dryRun) {
        var result = new DatamodelValidation();
        Datamodel raw;
        try {
            raw = DatamodelJson.Deserialize(draftJson);
        } catch (Exception error) {
            result.Issues.Add(DatamodelIssue.Error("invalid-json", "The draft is not a valid datamodel: " + error.Message));
            return result;
        }
        checkStructure(raw, result.Issues);
        if (result.HasErrors) return result;

        // the structure holds: initialize a second copy (initialization mutates) and let the model's
        // own checks have the last word on it
        Datamodel draft;
        try {
            draft = DatamodelJson.Deserialize(draftJson);
            draft.EnsureInitalization();
        } catch (Exception error) {
            result.Issues.Add(DatamodelIssue.Error("structure", error.Message));
            return result;
        }
        result.Draft = draft;
        result.DraftChecksum = DatamodelJson.Checksum(draft);

        Datamodel active;
        try {
            active = LoadActive();
        } catch (Exception error) {
            result.Issues.Add(DatamodelIssue.Warning("active-unavailable", "The configured sources could not be loaded, so the draft is compared against an empty model: " + error.Message));
            active = new Datamodel();
            active.EnsureInitalization();
        }
        result.Active = active;
        result.ActiveChecksum = DatamodelJson.Checksum(active);

        var plan = DatamodelSourceWriter.Plan(active, draft, _server.RootDataFolderPath, id => _server.TryGetIO(id, out var io) ? io : null);
        result.Plan = plan;
        result.Issues.AddRange(plan.Issues);
        if (plan.SettingsChange) {
            var overlay = _server.ConfigurationOverlay;
            if (overlay != null && overlay.IsOverridden(Settings.SettingsOverlay.OverridePath(_container.Settings.Id, "DatamodelSources"), out _)) {
                result.Issues.Add(DatamodelIssue.Error("sources-locked", "The model source list of this database is set by the " + overlay.SectionName
                    + " configuration section, so sources cannot be added, removed or changed from here. "));
            }
        }
        checkBackingClasses(draft, result.Issues);
        checkDataImpact(active, draft, result.Issues);
        if (!result.HasErrors && dryRun) {
            try {
                dryRunCompile(draft, plan, result.Issues);
                result.Compiled = true;
            } catch (Exception error) {
                result.Issues.Add(DatamodelIssue.Error("compile", error.Message));
            }
        }
        return result;
    }

    // ---- structure ----

    static readonly HashSet<string> csharpKeywords = new(StringComparer.Ordinal) {
        "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const","continue","decimal","default","delegate","do","double","else","enum","event","explicit","extern","false","finally","fixed","float","for","foreach","goto","if","implicit","in","int","interface","internal","is","lock","long","namespace","new","null","object","operator","out","override","params","private","protected","public","readonly","ref","return","sbyte","sealed","short","sizeof","stackalloc","static","string","struct","switch","this","throw","true","try","typeof","uint","ulong","unchecked","unsafe","ushort","using","virtual","void","volatile","while",
    };
    /// <summary>A C# identifier: what a type, property or relation name has to be to end up in generated code.</summary>
    public static bool IsValidIdentifier(string? name) {
        if (string.IsNullOrEmpty(name)) return false;
        if (!(char.IsLetter(name[0]) || name[0] == '_')) return false;
        for (var i = 1; i < name.Length; i++) if (!(char.IsLetterOrDigit(name[i]) || name[i] == '_')) return false;
        return !csharpKeywords.Contains(name);
    }
    static bool isValidNamespace(string? ns) => string.IsNullOrEmpty(ns) || ns.Split('.').All(IsValidIdentifier);

    static void checkStructure(Datamodel dm, List<DatamodelIssue> issues) {
        var types = dm.NodeTypes.Values.Where(t => t.Id != NodeConstants.BaseNodeTypeId).ToList();
        var byFullName = new Dictionary<string, List<NodeTypeModel>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in types) {
            if (t.Id == Guid.Empty) issues.Add(DatamodelIssue.Error("missing-id", "A node type (" + t.CodeName + ") has no id.", nodeType: t.Id));
            if (!IsValidIdentifier(t.CodeName)) issues.Add(DatamodelIssue.Error("invalid-name", "\"" + t.CodeName + "\" is not a valid type name. Use letters, digits and underscores, starting with a letter.", nodeType: t.Id));
            if (!isValidNamespace(t.Namespace)) issues.Add(DatamodelIssue.Error("invalid-namespace", "The namespace \"" + t.Namespace + "\" of " + t.CodeName + " is not valid.", nodeType: t.Id));
            if (!byFullName.TryGetValue(t.FullName, out var list)) byFullName[t.FullName] = list = [];
            list.Add(t);
        }
        foreach (var (name, list) in byFullName.Where(kv => kv.Value.Count > 1)) {
            foreach (var t in list) issues.Add(DatamodelIssue.Error("duplicate-type", "Two node types have the full name " + name + ". Rename one of them or give them different namespaces.", nodeType: t.Id));
        }
        // inheritance
        foreach (var t in types) {
            foreach (var parentId in t.Parents.Where(p => p != NodeConstants.BaseNodeTypeId)) {
                if (!dm.NodeTypes.TryGetValue(parentId, out var parent)) {
                    issues.Add(DatamodelIssue.Error("missing-parent", t.FullName + " inherits from a type (" + parentId + ") that is not in the model.", nodeType: t.Id));
                    continue;
                }
                if (parentId == t.Id) issues.Add(DatamodelIssue.Error("self-parent", t.FullName + " inherits from itself.", nodeType: t.Id));
                if (!parent.CanInherit) issues.Add(DatamodelIssue.Error("struct-parent", t.FullName + " inherits from " + parent.FullName + ", which is a struct and cannot be inherited.", nodeType: t.Id));
                if (t.ModelType == ModelType.Interface && parent.ModelType != ModelType.Interface)
                    issues.Add(DatamodelIssue.Error("interface-parent", "The interface " + t.FullName + " inherits from " + parent.FullName + ", which is not an interface.", nodeType: t.Id));
                if (t.ModelType == ModelType.Class && parent.ModelType == ModelType.Record || t.ModelType == ModelType.Record && parent.ModelType == ModelType.Class)
                    issues.Add(DatamodelIssue.Error("class-record-parent", t.FullName + " (" + t.ModelType.ToString().ToLower() + ") cannot inherit from " + parent.FullName + " (" + parent.ModelType.ToString().ToLower() + "): a class and a record cannot inherit from each other.", nodeType: t.Id));
            }
            var concreteParents = t.Parents.Where(p => p != NodeConstants.BaseNodeTypeId && dm.NodeTypes.TryGetValue(p, out var pt) && pt.ModelType != ModelType.Interface).ToList();
            if (concreteParents.Count > 1) issues.Add(DatamodelIssue.Error("multiple-base-classes", t.FullName + " inherits from more than one class (" + string.Join(", ", concreteParents.Select(p => dm.NodeTypes[p].CodeName)) + "). A type can extend one class and implement any number of interfaces.", nodeType: t.Id));
            if (t.ModelType == ModelType.Struct && t.Parents.Any(p => p != NodeConstants.BaseNodeTypeId && dm.NodeTypes.TryGetValue(p, out var pt) && pt.ModelType != ModelType.Interface))
                issues.Add(DatamodelIssue.Error("struct-inherits", t.FullName + " is a struct and cannot inherit from a class.", nodeType: t.Id));
            if (hasCycle(dm, t)) issues.Add(DatamodelIssue.Error("inheritance-cycle", t.FullName + " is part of an inheritance cycle.", nodeType: t.Id));
        }
        // properties
        var propertyOwners = new Dictionary<Guid, NodeTypeModel>();
        foreach (var t in types) {
            var names = new Dictionary<string, PropertyModel>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in t.Properties.Values) {
                if (!IsValidIdentifier(p.CodeName)) issues.Add(DatamodelIssue.Error("invalid-name", "\"" + p.CodeName + "\" on " + t.CodeName + " is not a valid property name.", nodeType: t.Id, property: p.Id));
                if (!names.TryAdd(p.CodeName, p)) issues.Add(DatamodelIssue.Error("duplicate-property", t.FullName + " has two properties named " + p.CodeName + " (names are compared case-insensitively).", nodeType: t.Id, property: p.Id));
                if (propertyOwners.TryGetValue(p.Id, out var other)) issues.Add(DatamodelIssue.Error("duplicate-property-id", "The properties " + other.CodeName + "." + other.Properties[p.Id].CodeName + " and " + t.CodeName + "." + p.CodeName + " have the same id " + p.Id + ". Property ids must be unique across the model.", nodeType: t.Id, property: p.Id));
                else propertyOwners[p.Id] = t;
                if (string.Equals(p.CodeName, t.CodeName, StringComparison.Ordinal)) issues.Add(DatamodelIssue.Error("property-named-as-type", t.CodeName + "." + p.CodeName + ": a member cannot have the same name as its type.", nodeType: t.Id, property: p.Id));
                checkProperty(dm, t, p, issues);
            }
        }
        // relations
        var relationNames = new Dictionary<string, RelationModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in dm.Relations.Values) {
            if (!IsValidIdentifier(r.CodeName)) issues.Add(DatamodelIssue.Error("invalid-name", "\"" + r.CodeName + "\" is not a valid relation name.", relation: r.Id));
            if (!isValidNamespace(r.Namespace)) issues.Add(DatamodelIssue.Error("invalid-namespace", "The namespace \"" + r.Namespace + "\" of the relation " + r.CodeName + " is not valid.", relation: r.Id));
            if (!relationNames.TryAdd(r.FullName(), r)) issues.Add(DatamodelIssue.Error("duplicate-relation", "Two relations have the full name " + r.FullName() + ".", relation: r.Id));
            if (byFullName.ContainsKey(r.FullName())) issues.Add(DatamodelIssue.Error("relation-named-as-type", "The relation " + r.FullName() + " has the same full name as a node type.", relation: r.Id));
            if (r.SourceTypes.Count == 0) issues.Add(DatamodelIssue.Error("relation-no-source", "The relation " + r.CodeName + " has no source type.", relation: r.Id));
            if (r.TargetTypes.Count == 0) issues.Add(DatamodelIssue.Error("relation-no-target", "The relation " + r.CodeName + " has no target type.", relation: r.Id));
            foreach (var id in r.SourceTypes.Concat(r.TargetTypes)) {
                if (!dm.NodeTypes.ContainsKey(id)) issues.Add(DatamodelIssue.Error("relation-missing-type", "The relation " + r.CodeName + " refers to a type (" + id + ") that is not in the model.", relation: r.Id));
            }
        }
    }
    static void checkProperty(Datamodel dm, NodeTypeModel t, PropertyModel p, List<DatamodelIssue> issues) {
        string where = t.CodeName + "." + p.CodeName;
        switch (p) {
            case ReferencePropertyModel rp:
                foreach (var id in rp.NodeTypes) if (!dm.NodeTypes.ContainsKey(id)) issues.Add(DatamodelIssue.Error("missing-reference-type", where + " refers to a node type (" + id + ") that is not in the model.", nodeType: t.Id, property: p.Id));
                if (rp.NodeTypes.Count == 0 && (rp.NodeTypesNames == null || rp.NodeTypesNames.Count == 0)) issues.Add(DatamodelIssue.Error("reference-no-type", where + " does not say which node types it can refer to.", nodeType: t.Id, property: p.Id));
                break;
            case ReferencesPropertyModel rsp:
                foreach (var id in rsp.NodeTypes) if (!dm.NodeTypes.ContainsKey(id)) issues.Add(DatamodelIssue.Error("missing-reference-type", where + " refers to a node type (" + id + ") that is not in the model.", nodeType: t.Id, property: p.Id));
                if (rsp.NodeTypes.Count == 0 && (rsp.NodeTypesNames == null || rsp.NodeTypesNames.Count == 0)) issues.Add(DatamodelIssue.Error("reference-no-type", where + " does not say which node types it can refer to.", nodeType: t.Id, property: p.Id));
                break;
            case EmbeddedPropertyModel ep:
                foreach (var id in ep.InnerNodeTypes) if (!dm.NodeTypes.ContainsKey(id)) issues.Add(DatamodelIssue.Error("missing-inner-type", where + " embeds a node type (" + id + ") that is not in the model.", nodeType: t.Id, property: p.Id));
                if (ep.InnerNodeTypes.Count == 0 && (ep.InnerNodeTypesNames == null || ep.InnerNodeTypesNames.Count == 0)) issues.Add(DatamodelIssue.Error("embedded-no-type", where + " does not say which inner node type it embeds.", nodeType: t.Id, property: p.Id));
                break;
            case RelationPropertyModel rlp:
                if (rlp.RelationId != Guid.Empty && !dm.Relations.ContainsKey(rlp.RelationId)) issues.Add(DatamodelIssue.Error("missing-relation", where + " uses a relation (" + rlp.RelationId + ") that is not in the model.", nodeType: t.Id, property: p.Id));
                if (rlp.NodeTypeOfRelated != Guid.Empty && !dm.NodeTypes.ContainsKey(rlp.NodeTypeOfRelated)) issues.Add(DatamodelIssue.Error("missing-related-type", where + " points at a node type (" + rlp.NodeTypeOfRelated + ") that is not in the model.", nodeType: t.Id, property: p.Id));
                break;
            case StringPropertyModel sp:
                if (sp.MinLength < 0 || sp.MaxLength < sp.MinLength) issues.Add(DatamodelIssue.Error("bad-range", where + ": the length range is not valid.", nodeType: t.Id, property: p.Id));
                if (sp.IndexedBySemantic && !sp.IndexedByWords) issues.Add(DatamodelIssue.Warning("semantic-without-words", where + " is in the semantic index but not the word index; searches will only find it through similarity.", nodeType: t.Id, property: p.Id));
                break;
            case IntegerPropertyModel ip:
                if (ip.MinValue > ip.MaxValue) issues.Add(DatamodelIssue.Error("bad-range", where + ": the minimum is above the maximum.", nodeType: t.Id, property: p.Id));
                if (ip.IsEnum && string.IsNullOrEmpty(ip.FullEnumTypeName) && (ip.LegalValues == null || ip.LegalValues.Length == 0)) issues.Add(DatamodelIssue.Warning("enum-without-values", where + " is an enum without an enum type or a list of legal values.", nodeType: t.Id, property: p.Id));
                break;
        }
    }
    static bool hasCycle(Datamodel dm, NodeTypeModel start) {
        var seen = new HashSet<Guid>();
        var stack = new Stack<Guid>(start.Parents);
        while (stack.Count > 0) {
            var id = stack.Pop();
            if (id == start.Id) return true;
            if (!seen.Add(id) || !dm.NodeTypes.TryGetValue(id, out var t)) continue;
            foreach (var p in t.Parents) stack.Push(p);
        }
        return false;
    }

    // ---- what the stored data feels ----

    void checkDataImpact(Datamodel active, Datamodel draft, List<DatamodelIssue> issues) {
        var store = _container.IsOpen() ? _container.Store : null;
        long count(Guid typeId) {
            if (store == null) return -1;
            try { return store.QueryType(typeId, countContext).Count(); } catch { return -1; }
        }
        string nodes(long n) => n < 0 ? "nodes" : n == 1 ? "1 node" : n + " nodes";
        foreach (var a in active.NodeTypes.Values.Where(t => t.Id != NodeConstants.BaseNodeTypeId)) {
            if (!draft.NodeTypes.TryGetValue(a.Id, out var d)) {
                var n = count(a.Id);
                if (n != 0) issues.Add(DatamodelIssue.Warning("type-removed", "The type " + a.FullName + " is removed. " + (n > 0 ? nodes(n) + " of this type stay in the database as nodes without a type, and their property values are no longer readable." : "Nodes of this type would lose their type and their property values."), nodeType: a.Id));
                else issues.Add(DatamodelIssue.Info("type-removed", "The type " + a.FullName + " is removed; it has no nodes.", nodeType: a.Id));
                continue;
            }
            if (!string.Equals(a.FullName, d.FullName, StringComparison.Ordinal)) issues.Add(DatamodelIssue.Info("type-renamed", a.FullName + " is renamed to " + d.FullName + ". The id is kept, so its nodes follow.", nodeType: a.Id));
            if (a.ModelType != d.ModelType) issues.Add(DatamodelIssue.Warning("type-kind-changed", d.FullName + " changes from " + a.ModelType.ToString().ToLower() + " to " + d.ModelType.ToString().ToLower() + "; application code that uses the type has to follow.", nodeType: a.Id));
            long typeCount = -2; // counted on first need
            foreach (var ap in a.Properties.Values) {
                if (!d.Properties.TryGetValue(ap.Id, out var dp)) {
                    if (draft.Properties.ContainsKey(ap.Id)) continue; // moved to another type: the owner check below reports if that is a problem
                    if (typeCount == -2) typeCount = count(a.Id);
                    if (typeCount != 0) issues.Add(DatamodelIssue.Warning("property-removed", "The property " + a.CodeName + "." + ap.CodeName + " is removed; its values on " + nodes(typeCount) + " are dropped at the next log rewrite.", nodeType: a.Id, property: ap.Id));
                    else issues.Add(DatamodelIssue.Info("property-removed", "The property " + a.CodeName + "." + ap.CodeName + " is removed; the type has no nodes.", nodeType: a.Id, property: ap.Id));
                    continue;
                }
                if (ap.PropertyType != dp.PropertyType) {
                    if (typeCount == -2) typeCount = count(a.Id);
                    issues.Add(DatamodelIssue.Warning("property-type-changed", a.CodeName + "." + ap.CodeName + " changes from " + ap.PropertyType + " to " + dp.PropertyType + "; existing values" + (typeCount > 0 ? " on " + nodes(typeCount) : "") + " are converted where possible and dropped otherwise.", nodeType: a.Id, property: ap.Id));
                } else if (!string.Equals(ap.CodeName, dp.CodeName, StringComparison.Ordinal)) {
                    issues.Add(DatamodelIssue.Info("property-renamed", a.CodeName + "." + ap.CodeName + " is renamed to " + dp.CodeName + ". The id is kept, so the values follow.", nodeType: a.Id, property: ap.Id));
                }
            }
        }
        foreach (var ar in active.Relations.Values) {
            if (!draft.Relations.ContainsKey(ar.Id)) issues.Add(DatamodelIssue.Warning("relation-removed", "The relation " + ar.FullName() + " is removed; every link it holds is dropped.", relation: ar.Id));
            else if (draft.Relations[ar.Id].RelationType != ar.RelationType) issues.Add(DatamodelIssue.Warning("relation-kind-changed", "The relation " + ar.FullName() + " changes from " + ar.RelationType + " to " + draft.Relations[ar.Id].RelationType + "; links that break the new cardinality are dropped.", relation: ar.Id));
        }
        // index settings that force a rebuild of the state and indexes at the next open
        var draftIndexed = draft.Properties.Values.Count(p => p.Indexed) + draft.Properties.Values.Count(p => p is StringPropertyModel s && (s.IndexedByWords || s.IndexedBySemantic));
        var activeIndexed = active.Properties.Values.Count(p => p.Indexed) + active.Properties.Values.Count(p => p is StringPropertyModel s && (s.IndexedByWords || s.IndexedBySemantic));
        if (draftIndexed != activeIndexed && store != null) {
            issues.Add(DatamodelIssue.Info("indexes-change", "Indexed properties change (" + activeIndexed + " to " + draftIndexed + "). The database rebuilds its state and indexes from the log when it opens with the new model, which takes a while on a large database."));
        }
    }

    // ---- JSON types and the classes behind them ----

    static void checkBackingClasses(Datamodel draft, List<DatamodelIssue> issues) {
        var jsonSources = draft.Sources.Where(s => s.IsJsonFiles && s.Enabled).Select(s => s.Id).ToHashSet();
        if (jsonSources.Count == 0) return;
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).ToList();
        foreach (var t in draft.NodeTypes.Values.Where(t => jsonSources.Contains(t.DatamodelSourceId) && t.ModelType != ModelType.Interface)) {
            Type? clr = null;
            foreach (var assembly in assemblies) {
                try { clr = assembly.GetType(t.FullName, throwOnError: false); } catch { }
                if (clr != null) break;
            }
            if (clr == null) {
                issues.Add(DatamodelIssue.Warning("no-backing-class", "No class named " + t.FullName + " is loaded. A JSON-defined class needs a plain class with that name and the same property names in the application for queries to map onto; without one the type is only reachable as untyped nodes.", nodeType: t.Id));
                continue;
            }
            var members = clr.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Select(p => p.Name)
                .Concat(clr.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Select(f => f.Name))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var p in t.Properties.Values.Where(p => !p.Internal)) {
                if (members.Contains(p.CodeName)) continue;
                var hint = members.FirstOrDefault(m => string.Equals(m, p.CodeName, StringComparison.OrdinalIgnoreCase));
                issues.Add(DatamodelIssue.Error("backing-class-member", "The class " + clr.FullName + " behind the JSON type has no member named " + p.CodeName
                    + (hint != null ? " (it has \"" + hint + "\"; names are case sensitive)" : "") + ". Add the member to the class, or remove the property. The class is compiled into the application, so the model cannot add it.", nodeType: t.Id, property: p.Id));
            }
        }
    }

    // ---- the dry run ----

    void dryRunCompile(Datamodel draft, SourceWritePlan plan, List<DatamodelIssue> issues) {
        var tempRoot = Path.Combine(Path.GetTempPath(), "RelatudeDB", "datamodel-dryrun", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try {
            var dm = new Datamodel();
            // types of compiled sources come back from a separate probe model rather than from dm:
            // see below
            var probed = new Dictionary<Guid, NodeTypeModel>();
            var probedRelations = new Dictionary<Guid, RelationModel>();
            var rootFolder = _server.RootDataFolderPath;
            foreach (var source in draft.Sources) {
                if (source.Type == DatamodelSourceType.Code) continue; // added below by the event, like at a real open
                if (!source.Enabled) continue;
                var change = plan.Sources.FirstOrDefault(s => s.SourceId == source.Id);
                var files = plan.Files.Where(f => f.SourceId == source.Id).ToList();
                var rewritten = change != null && change.Writable && change.HasModelChanges && files.Count > 0;
                if (rewritten && change!.RequiresRebuild) {
                    // A compiled source's new code cannot stand in for the old inside this process: the
                    // application's assembly still defines the same type names, and mapper code compiled
                    // against both would find every type twice. So the generated files are compiled on
                    // their own, to prove they compile and to read back what they say, while the model
                    // keeps the types the running application has. The mapper is verified for real when
                    // the application is rebuilt and the database opens.
                    var mirrored = mirrorSource(source, files, draft, tempRoot, rootFolder);
                    var probe = new Datamodel();
                    DatamodelSourceLoader.Load(probe, mirrored, rootFolder, id => _server.TryGetIO(id, out var io) ? io : null);
                    probe.EnsureInitalization();
                    foreach (var t in probe.NodeTypes.Values.Where(t => t.Id != NodeConstants.BaseNodeTypeId)) probed[t.Id] = t;
                    foreach (var r in probe.Relations.Values) probedRelations[r.Id] = r;
                    DatamodelSourceLoader.Load(dm, source, rootFolder, id => _server.TryGetIO(id, out var io) ? io : null);
                    continue;
                }
                var toLoad = rewritten ? mirrorSource(source, files, draft, tempRoot, rootFolder) : source;
                DatamodelSourceLoader.Load(dm, toLoad, rootFolder, id => _server.TryGetIO(id, out var io) ? io : null);
            }
            _server.RaiseEventDatamodelInit(dm, _container.Settings);
            dm.EnsureInitalization();
            try {
                MapperCompileCheck.Verify(dm);
            } catch (Exception error) when (plan.RequiresRebuild) {
                // with compiled types still the old ones, a member of another source may point at
                // something that only exists after the rebuild: worth knowing, not a reason to stop
                issues.Add(DatamodelIssue.Warning("compile-pending-rebuild", "The mapper code could not be compiled against the running application, which still has the old compiled types; "
                    + "if the failure is about the types being changed, it resolves itself with the rebuild. " + error.Message));
            }
            // the compiled sources' types come from the probe; everything else from the model
            var compiledSources = plan.Sources.Where(s => s.RequiresRebuild && s.HasModelChanges).Select(s => s.SourceId).ToHashSet();
            foreach (var (id, t) in probed) dm.NodeTypes[id] = t;
            foreach (var (id, r) in probedRelations) dm.Relations[id] = r;
            foreach (var d in draft.NodeTypes.Values.Where(t => compiledSources.Contains(t.DatamodelSourceId) && !probed.ContainsKey(t.Id))) dm.NodeTypes.Remove(d.Id);
            foreach (var d in draft.Relations.Values.Where(r => compiledSources.Contains(r.DatamodelSourceId) && !probedRelations.ContainsKey(r.Id))) dm.Relations.Remove(d.Id);
            // what came back from the files against what the draft says
            foreach (var d in draft.NodeTypes.Values.Where(t => t.Id != NodeConstants.BaseNodeTypeId)) {
                if (!dm.NodeTypes.TryGetValue(d.Id, out var loaded)) {
                    issues.Add(DatamodelIssue.Warning("round-trip-missing", "After writing, the type " + d.FullName + " does not come back from its source. Check the source's namespace filter and file path.", nodeType: d.Id));
                    continue;
                }
                var a = DatamodelSourceWriter.Fingerprint(d);
                var b = DatamodelSourceWriter.Fingerprint(loaded);
                if (a != b) issues.Add(DatamodelIssue.Warning("round-trip-differs", "After writing, the type " + d.FullName + " comes back different from the draft: " + describeDifference(a, b) + ". The generated code cannot express this setting exactly; the written version is what the database will use.", nodeType: d.Id));
            }
            foreach (var d in draft.Relations.Values) {
                if (!dm.Relations.TryGetValue(d.Id, out var loaded)) {
                    issues.Add(DatamodelIssue.Warning("round-trip-missing", "After writing, the relation " + d.FullName() + " does not come back from its source.", relation: d.Id));
                    continue;
                }
                var a = DatamodelSourceWriter.Fingerprint(d);
                var b = DatamodelSourceWriter.Fingerprint(loaded);
                if (a != b) issues.Add(DatamodelIssue.Warning("round-trip-differs", "After writing, the relation " + d.FullName() + " comes back different from the draft: " + describeDifference(a, b) + ".", relation: d.Id));
            }
            foreach (var loaded in dm.NodeTypes.Values.Where(t => t.Id != NodeConstants.BaseNodeTypeId && !draft.NodeTypes.ContainsKey(t.Id))) {
                issues.Add(DatamodelIssue.Warning("round-trip-extra", "After writing, the source of " + loaded.FullName + " still defines it although the draft does not have it. It is in a file the plan does not touch.", nodeType: loaded.Id));
            }
        } finally {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }
    /// <summary>
    /// A copy of the source's model files as they would be after the plan is applied, in the scratch
    /// folder, and a source definition reading from there. Only files that hold model types are
    /// copied: a compiled source's folder is a whole project, which could not be compiled on its own.
    /// </summary>
    static DatamodelSource mirrorSource(DatamodelSource source, List<PlannedFile> files, Datamodel draft, string tempRoot, string rootFolder) {
        var folder = Path.Combine(tempRoot, source.Id.ToString("N"));
        Directory.CreateDirectory(folder);
        var isJson = source.IsJsonFiles;
        if (isJson && source.FileIO != null) {
            var file = Path.Combine(folder, "model.json");
            File.WriteAllText(file, files[0].Content ?? "{}");
            return new DatamodelSource { Id = source.Id, Name = source.Name, Type = DatamodelSourceType.TextFiles, FileFormat = DatamodelSourceFileFormat.Json, Filepath = file, Enabled = true };
        }
        // the files the plan leaves alone but that hold model types come along unchanged
        var written = files.Select(f => f.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string baseFolder;
        if (source.Type == DatamodelSourceType.TypeReference) {
            baseFolder = DatamodelSourceLoader.ResolveSourceCodeFolder(source, rootFolder)!;
        } else {
            var target = DatamodelSourceLoader.ResolveFilePath(source, rootFolder, DatamodelSourceLoader.DefaultFolder(source));
            baseFolder = File.Exists(target) || !Directory.Exists(target) && Path.HasExtension(target) ? Path.GetDirectoryName(target)! : target;
        }
        // a generated folder is described by the plan alone: the files the types were stamped with are
        // the ones being deleted, and copying them would declare every type twice
        var untouched = DatamodelSourceWriter.IsGeneratedFolder(source) ? [] : draft.NodeTypes.Values.Where(t => t.DatamodelSourceId == source.Id).Select(t => t.DatamodelSourceFilename)
            .Concat(draft.Relations.Values.Where(r => r.DatamodelSourceId == source.Id).Select(r => r.DatamodelSourceFilename))
            .Where(f => !string.IsNullOrEmpty(f) && !written.Contains(f!)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in untouched) {
            var from = Path.Combine(baseFolder, relative!);
            if (!File.Exists(from)) continue;
            var to = Path.Combine(folder, relative!);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, true);
        }
        foreach (var f in files) {
            var to = Path.Combine(folder, f.RelativePath);
            if (f.Action == PlannedFileAction.Delete) {
                if (File.Exists(to)) File.Delete(to);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.WriteAllText(to, f.Content);
        }
        var ns = source.Namespace;
        return new DatamodelSource {
            Id = source.Id, Name = source.Name, Enabled = true,
            Type = DatamodelSourceType.TextFiles,
            FileFormat = isJson ? DatamodelSourceFileFormat.Json : DatamodelSourceFileFormat.CSharpCode,
            Filepath = folder,
            Namespace = isJson ? null : ns,
        };
    }
    /// <summary>Which top level fields (and which properties) differ between two fingerprints, for a message.</summary>
    static string describeDifference(string a, string b) {
        try {
            using var da = JsonDocument.Parse(a);
            using var db = JsonDocument.Parse(b);
            var names = da.RootElement.EnumerateObject().Select(p => p.Name).Union(db.RootElement.EnumerateObject().Select(p => p.Name)).ToList();
            var differences = new List<string>();
            foreach (var name in names) {
                var hasA = da.RootElement.TryGetProperty(name, out var va);
                var hasB = db.RootElement.TryGetProperty(name, out var vb);
                if (hasA && hasB && va.GetRawText() == vb.GetRawText()) continue;
                if (name == "Properties" && hasA && hasB && va.ValueKind == JsonValueKind.Object && vb.ValueKind == JsonValueKind.Object) {
                    foreach (var pa in va.EnumerateObject()) {
                        if (!vb.TryGetProperty(pa.Name, out var pb)) { differences.Add("property " + codeName(pa.Value) + " missing"); continue; }
                        if (pa.Value.GetRawText() == pb.GetRawText()) continue;
                        var fields = pa.Value.EnumerateObject().Select(f => f.Name).Union(pb.EnumerateObject().Select(f => f.Name))
                            .Where(f => !(pa.Value.TryGetProperty(f, out var x) && pb.TryGetProperty(f, out var y) && x.GetRawText() == y.GetRawText()));
                        differences.Add("property " + codeName(pa.Value) + " (" + string.Join(", ", fields) + ")");
                    }
                    foreach (var pb in vb.EnumerateObject()) if (!va.TryGetProperty(pb.Name, out _)) differences.Add("property " + codeName(pb.Value) + " added");
                    continue;
                }
                differences.Add(name + (hasA && hasB ? "" : hasA ? " (missing after)" : " (added after)"));
            }
            return differences.Count == 0 ? "no visible difference" : string.Join(", ", differences.Take(12)) + (differences.Count > 12 ? ", ..." : "");
        } catch {
            return "different settings";
        }
    }
    static string codeName(JsonElement property) => property.TryGetProperty("CodeName", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "?" : "?";
}
