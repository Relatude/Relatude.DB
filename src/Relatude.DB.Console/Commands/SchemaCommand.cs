using Relatude.DB.Datamodels;

namespace Relatude.DB.Cli.Commands;

/// <summary>
/// Prints the datamodel. The model is built from code only, so this works before the database exists and
/// while the application is running.
/// </summary>
public static class SchemaCommand {
    public static Task<int> RunAsync(CommandArgs args) {
        args.Accept([.. Target.Options, .. ModelSource.Options, "type", "format", "ids", "include-native", "properties-only"]);
        var target = Target.Resolve(args);
        var dm = LoadModel(args, target);
        var format = (args.Get("format") ?? (args.Flag("json") ? "json" : "text")).ToLowerInvariant();
        var filter = new SchemaFilter {
            IncludeNative = args.Flag("include-native"),
            ShowIds = args.Flag("ids") || format == "json",
            PropertiesOnly = args.Flag("properties-only"),
            Types = args.GetAll("type"),
        };
        if (filter.Types.Length > 0 && !SchemaView.NodeTypes(dm, filter).Any() && !SchemaView.Relations(dm, filter).Any()) {
            throw new CliException("No node type or relation named " + string.Join(", ", filter.Types)
                + ". Run \"relatude schema\" to see what the model holds.");
        }
        switch (format) {
            case "text": SchemaView.WriteText(dm, filter); break;
            case "md" or "markdown": SchemaView.WriteMarkdown(dm, filter); break;
            case "json": Con.Json(SchemaView.BuildJson(dm, filter)); break;
            default: throw new UsageException("--format takes text, md or json, not \"" + format + "\".");
        }
        return Task.FromResult(0);
    }

    /// <summary>
    /// The datamodel of the application. Model code named on the command line is used as is; otherwise the
    /// datamodel sources of relatude.db.json are followed, which is what the application itself does.
    /// </summary>
    public static Datamodel LoadModel(CommandArgs args, Target target) {
        if (ModelSource.IsExplicit(args, target)) {
            var dm = ModelSource.Build(args, target);
            dm.EnsureInitalization();
            return dm;
        }
        if (!target.SettingsExists) {
            throw new CliException("Nothing to read the datamodel from." + Environment.NewLine + target.Describe()
                + Environment.NewLine + "The datamodel is not stored in the database files, it lives in your model code:"
                + Environment.NewLine + "point at it with --project, --assembly or --source.");
        }
        var fromSettings = SettingsReader.BuildDatamodelFromSettings(args, target);
        fromSettings.EnsureInitalization();
        return fromSettings;
    }
}
