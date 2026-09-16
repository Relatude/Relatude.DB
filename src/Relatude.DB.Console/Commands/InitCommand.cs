using Relatude.DB.NodeServer;

namespace Relatude.DB.Cli.Commands;

/// <summary>
/// Writes a relatude.db.json next to an existing application (see <see cref="SettingsTemplate"/> for
/// what goes in it). "relatude new" writes the same file into a freshly generated project.
/// </summary>
public static class InitCommand {
    public static Task<int> RunAsync(CommandArgs args) {
        args.Accept([.. Target.Options, "name", "namespace", "assembly-name", "path", "user", "password", "force"]);
        var target = Target.Resolve(args);
        var path = target.SettingsPath;
        if (File.Exists(path) && !args.Flag("force")) {
            throw new CliException("A settings file already exists: " + path + Environment.NewLine
                + "Pass --force to replace it, or look at it with: relatude settings");
        }
        var assemblyName = args.Get("assembly-name")
            ?? (target.ProjectFile == null ? null : Path.GetFileNameWithoutExtension(target.ProjectFile));
        var modelNamespace = args.Get("namespace");
        var dataPath = args.Get("path") ?? Defaults.DataFolderPath;

        var settings = SettingsTemplate.Create(
            databaseName: args.Get("name") ?? "MyDatabase",
            dataPath: dataPath,
            modelNamespace: modelNamespace,
            assemblyName: assemblyName,
            user: args.Get("user"),
            password: args.Get("password"),
            waitUntilOpen: false);
        var container = settings.ContainerSettings!.First();
        var folder = Path.GetDirectoryName(path);
        if (folder != null && folder.Length > 0) Directory.CreateDirectory(folder);
        File.WriteAllText(path, SettingsReader.Serialize(settings));

        if (args.Flag("json")) {
            Output.Json(new { File = path, Database = container.Name, container.Id, DataFolder = Path.Combine(target.Root, dataPath) });
            return Task.FromResult(0);
        }
        Output.WriteLine("Wrote " + path);
        Output.Table([
            ("database", container.Name ?? "-"),
            ("data folder", Path.Combine(target.Root, dataPath)),
            ("model", modelNamespace == null ? "not set" : modelNamespace + " in " + (assemblyName ?? "(entry assembly)")),
            ("admin user", args.Get("user") == null ? "not set" : args.Get("user")!),
        ]);
        Output.WriteLine();
        if (modelNamespace == null) {
            Output.WriteLine("Next: add your model namespace to DatamodelSources, or register it in code with");
            Output.WriteLine("options.OnDatamodelInit. Re-run with --namespace <ns> --force to have it written for you.");
        } else {
            Output.WriteLine("Next: check it with \"relatude validate\", then open it with \"relatude info\".");
        }
        if (args.Get("user") == null) {
            Output.WriteLine("MasterUserName and MasterPassword are empty: the admin UI cannot be logged into until they are set.");
        }
        return Task.FromResult(0);
    }
}
