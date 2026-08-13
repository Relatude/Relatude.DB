using Relatude.DB.CodeGeneration;
using Relatude.DB.Datamodels;

namespace Relatude.DB.Cli.Commands;

/// <summary>
/// Writes the datamodel back out as C# model code. Every id is spelled out in the attributes, which is
/// the point of it: ids that are otherwise derived from type and member names become explicit, and the
/// model survives renaming.
/// </summary>
public static class CodeGenCommand {
    public static Task<int> RunAsync(CommandArgs args) {
        args.Accept([.. Target.Options, .. ModelSource.Options, "type", "out", "out-dir", "no-attributes", "include-native", "force"]);
        var target = Target.Resolve(args);
        var dm = SchemaCommand.LoadModel(args, target);
        var filter = new SchemaFilter {
            IncludeNative = args.Flag("include-native"),
            Types = args.GetAll("type"),
        };
        var nodeTypes = SchemaView.NodeTypes(dm, filter).ToList();
        var relations = SchemaView.Relations(dm, filter).ToList();
        if (nodeTypes.Count == 0 && relations.Count == 0) {
            throw new CliException(filter.Types.Length > 0
                ? "No node type or relation named " + string.Join(", ", filter.Types) + "."
                : "The datamodel holds no types to generate.");
        }
        var attributes = !args.Flag("no-attributes");
        var outDir = args.Get("out-dir");
        var outFile = args.Get("out");
        if (outDir != null && outFile != null) throw new UsageException("Use either --out or --out-dir, not both.");

        if (outDir != null) return Task.FromResult(writeOneFilePerType(dm, nodeTypes, relations, attributes, outDir, args.Flag("force")));
        var ids = nodeTypes.Select(t => t.Id).ToHashSet();
        var relationIds = relations.Select(r => r.Id).ToHashSet();
        var code = ModelGen.GenerateCSharpModelCode(dm, attributes, t => ids.Contains(t.Id), r => relationIds.Contains(r.Id));
        if (outFile == null) {
            Con.Write(code);
            return Task.FromResult(0);
        }
        var path = Path.GetFullPath(outFile, Directory.GetCurrentDirectory());
        write(path, code, args.Flag("force"));
        Con.Info($"Wrote {nodeTypes.Count} node type(s) and {relations.Count} relation(s) to {path}");
        return Task.FromResult(0);
    }

    static int writeOneFilePerType(Datamodel dm, List<NodeTypeModel> nodeTypes, List<RelationModel> relations,
            bool attributes, string outDir, bool force) {
        var folder = Path.GetFullPath(outDir, Directory.GetCurrentDirectory());
        Directory.CreateDirectory(folder);
        var written = 0;
        foreach (var t in nodeTypes) {
            var code = ModelGen.GenerateCSharpModelCode(dm, attributes, n => n.Id == t.Id, _ => false);
            write(Path.Combine(folder, t.CodeName + ".cs"), code, force);
            written++;
        }
        foreach (var r in relations) {
            var code = ModelGen.GenerateCSharpModelCode(dm, attributes, _ => false, x => x.Id == r.Id);
            write(Path.Combine(folder, r.CodeName + ".cs"), code, force);
            written++;
        }
        Con.Info($"Wrote {written} file(s) to {folder}");
        return 0;
    }
    static void write(string path, string code, bool force) {
        if (File.Exists(path) && !force) {
            throw new CliException("The file already exists, pass --force to overwrite it: " + path);
        }
        var folder = Path.GetDirectoryName(path);
        if (folder != null && folder.Length > 0) Directory.CreateDirectory(folder);
        File.WriteAllText(path, code);
    }
}
