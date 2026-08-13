using System.Reflection;
using Relatude.DB.CodeGeneration;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Nodes;

namespace Relatude.DB.Cli.Commands;

/// <summary>
/// Builds the datamodel from model code and reports what would go wrong at startup, plus the things that
/// are legal but worth knowing about - above all ids that are derived from names, because renaming such a
/// type or member makes the database look empty for it.
/// </summary>
public static class ValidateCommand {
    public static Task<int> RunAsync(CommandArgs args) {
        args.Accept([.. Target.Options, .. ModelSource.Options, "include-native"]);
        var target = Target.Resolve(args);
        var problems = new List<string>();
        var warnings = new List<string>();

        Datamodel dm;
        try {
            dm = ModelSource.IsExplicit(args, target)
                ? ModelSource.Build(args, target)
                : SettingsReader.BuildDatamodelFromSettings(args, target);
        } catch (Exception err) {
            return Task.FromResult(report(args, ["The model could not be built: " + err.Message], [], null, target));
        }
        try {
            dm.EnsureInitalization();
        } catch (Exception err) {
            return Task.FromResult(report(args, ["The model builder rejected the model: " + err.Message], [], dm, target));
        }

        var filter = new SchemaFilter { IncludeNative = args.Flag("include-native") };
        var own = SchemaView.NodeTypes(dm, filter).ToList();

        foreach (var group in own.GroupBy(t => t.CodeName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)) {
            warnings.Add("The name \"" + group.Key + "\" is used by " + group.Count() + " types ("
                + string.Join(", ", group.Select(t => t.FullName)) + "). A query that names it is ambiguous, "
                + "use the full name there.");
        }
        foreach (var t in own.Where(t => t.Properties.Count == 0 && t.AllProperties.Count <= 1)) {
            warnings.Add(t.FullName + " has no properties of its own.");
        }
        foreach (var t in own) {
            foreach (var p in t.Properties.Values.OfType<StringPropertyModel>()) {
                if (p.IndexedByWords && t.TextIndex != true) {
                    warnings.Add(t.CodeName + "." + p.CodeName + " asks for a word index while " + t.CodeName
                        + " has no text index, so free text search will not find it.");
                }
                if (p.IndexedBySemantic && t.SemanticIndex != true) {
                    warnings.Add(t.CodeName + "." + p.CodeName + " asks for a semantic index while " + t.CodeName
                        + " has no semantic index.");
                }
            }
            foreach (var p in t.Properties.Values.OfType<RelationPropertyModel>()) {
                if (!dm.Relations.ContainsKey(p.RelationId)) {
                    problems.Add(t.CodeName + "." + p.CodeName + " points at a relation that is not in the model.");
                }
            }
        }
        foreach (var r in SchemaView.Relations(dm, filter)) {
            if (r.SourceTypes.Count == 0 || r.TargetTypes.Count == 0) {
                problems.Add("Relation " + r.FullName() + " has no " + (r.SourceTypes.Count == 0 ? "source" : "target") + " type.");
            }
        }
        warnings.AddRange(implicitIdWarnings(dm, own));

        // last check: the model has to be expressible as code again, which is what the mapper generation needs
        try {
            var code = ModelGen.GenerateCSharpModelCode(dm, true);
            foreach (var error in ModelSource.CompileErrors(code, target).Take(10)) {
                problems.Add("The generated model code does not compile: " + error);
            }
        } catch (Exception err) {
            problems.Add("Model code could not be generated from this model: " + err.Message);
        }
        return Task.FromResult(report(args, problems, warnings, dm, target));
    }

    /// <summary>
    /// Ids that were derived from names. The datamodel does not remember where an id came from, so the
    /// attributes on the model types are read again.
    /// </summary>
    static IEnumerable<string> implicitIdWarnings(Datamodel dm, List<NodeTypeModel> nodeTypes) {
        var typesWithoutId = new List<string>();
        var membersWithoutId = 0;
        var found = 0;
        foreach (var t in nodeTypes) {
            var clr = dm.Assemblies.Select(a => a.GetType(t.FullName, false, false)).FirstOrDefault(x => x != null);
            if (clr == null) continue;
            found++;
            if (clr.GetCustomAttribute<NodeAttribute>()?.Id == null) typesWithoutId.Add(t.CodeName);
            foreach (var p in t.Properties.Values.Where(p => !p.Internal)) {
                var member = clr.GetMember(p.CodeName).FirstOrDefault();
                if (member == null) continue;
                if (member.GetCustomAttributes().OfType<PropertyAttribute>().FirstOrDefault()?.Id == null) membersWithoutId++;
            }
        }
        if (found == 0 || (typesWithoutId.Count == 0 && membersWithoutId == 0)) yield break;
        yield return typesWithoutId.Count + " node type(s) and " + membersWithoutId + " member(s) have ids derived from "
            + "their names, so renaming or moving one hides the data already stored under the old name"
            + (typesWithoutId.Count > 0 ? ": " + string.Join(", ", typesWithoutId.Take(10)) + (typesWithoutId.Count > 10 ? ", ..." : string.Empty) : string.Empty)
            + ". Pin them with [Node(Id = \"...\")] and Id on the property attributes - \"relatude codegen\" writes the model out with the current ids.";
    }

    static int report(CommandArgs args, List<string> problems, List<string> warnings, Datamodel? dm, Target target) {
        if (args.Flag("json")) {
            Con.Json(new {
                Valid = problems.Count == 0,
                Problems = problems,
                Warnings = warnings,
                NodeTypes = dm == null ? 0 : dm.NodeTypes.Count - 1,
                Relations = dm?.Relations.Count ?? 0,
            });
            return problems.Count == 0 ? 0 : 1;
        }
        if (dm != null) {
            var filter = new SchemaFilter { IncludeNative = args.Flag("include-native") };
            Con.WriteLine($"{SchemaView.NodeTypes(dm, filter).Count()} node type(s), "
                + $"{SchemaView.Relations(dm, filter).Count()} relation(s), {dm.Properties.Count} propert(ies)");
        }
        foreach (var p in problems) Con.WriteLine("  problem  " + p);
        foreach (var w in warnings) Con.WriteLine("  warning  " + w);
        Con.WriteLine();
        if (problems.Count == 0) {
            Con.WriteLine(warnings.Count == 0
                ? "The model is valid."
                : "The model is valid, with " + warnings.Count + " warning(s).");
            return 0;
        }
        Con.WriteLine(problems.Count + " problem(s) found. The database will not open with this model.");
        if (dm == null) Con.Info(target.Describe()); // nothing was read: say where it looked
        return 1;
    }
}
