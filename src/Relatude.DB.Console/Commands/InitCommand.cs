using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Settings;

namespace Relatude.DB.Cli.Commands;

/// <summary>
/// Writes a relatude.db.json. The default the server writes when it finds no file points at the bundled
/// demo model, which is almost never what is wanted; this one points at the caller's own model instead.
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

        var io = new IOSettings {
            Id = Guid.NewGuid(),
            Name = "Local disk",
            Path = dataPath,
            IOType = IOTypes.LocalDisk,
        };
        var container = new NodeStoreContainerSettings {
            Id = Guid.NewGuid(),
            Name = args.Get("name") ?? "MyDatabase",
            AutoOpen = true,
            WaitUntilOpen = false,
            LocalSettings = new SettingsLocal(),
            IOSettings = [io],
            IoDatabase = io.Id,
            IoBackup = io.Id,
            IoLog = io.Id,
            FileStoreSettings = [new FileStoreSettings {
                Id = Guid.NewGuid(),
                IoProviderId = io.Id,
                StoreType = FileStoreEngine.MultiFile,
                MultiFileFolderDepth = 2,
            }],
            DatamodelSources = sources(modelNamespace, assemblyName),
        };
        var settings = new RelatudeDBServerSettings {
            Name = "Relatude.DB Server",
            Id = Guid.NewGuid(),
            ContainerSettings = [container],
            DefaultStoreId = container.Id,
            MasterUserName = args.Get("user"),
            MasterPassword = args.Get("password"),
            TokenEncryptionSecret = Guid.NewGuid().ToString(),
        };
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

    static DatamodelSource[] sources(string? modelNamespace, string? assemblyName) {
        // the engine's own model is always needed: it backs users, groups, collections and cultures
        var list = new List<DatamodelSource> {
            new() {
                Id = Guid.NewGuid(),
                Name = "Native",
                Type = DatamodelSourceType.AssemblyNameReference,
                Namespace = ModelSource.NativeNamespace,
                Reference = "Relatude.DB.NodeStore",
            },
        };
        if (modelNamespace != null) {
            list.Insert(0, new DatamodelSource {
                Id = Guid.NewGuid(),
                Name = "Model",
                Type = DatamodelSourceType.AssemblyNameReference,
                Namespace = modelNamespace,
                Reference = assemblyName,
            });
        }
        return [.. list];
    }
}
