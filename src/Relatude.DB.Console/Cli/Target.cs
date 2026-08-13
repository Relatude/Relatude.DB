using System.Reflection;
using System.Runtime.Loader;

namespace Relatude.DB.Cli;

/// <summary>
/// Where the tool is pointed: the content root of the application (the folder that holds
/// relatude.db.json, exactly as the server resolves it), the settings file itself, and the folders the
/// application's own assemblies are loaded from. Everything is derived from the current folder unless
/// --project, --settings, --data, --bin or --assembly say otherwise.
/// </summary>
public sealed class Target {
    public const string SettingsFileOption = "settings";
    public static readonly string[] Options = ["project", SettingsFileOption, "data", "bin", "assembly", "store"];

    public required string Root { get; init; }
    public required string SettingsPath { get; init; }
    public string? ProjectFile { get; init; }
    public required string[] ProbeFolders { get; init; }
    public required string[] AssemblyFiles { get; init; }
    /// <summary>Name or id of the database container to work on, null for the default one.</summary>
    public string? Store { get; init; }
    public bool SettingsExists => File.Exists(SettingsPath);

    public static Target Resolve(CommandArgs args) {
        var cwd = Directory.GetCurrentDirectory();
        string? projectFile = null;
        string? root = null;
        var project = args.Get("project");
        if (project != null) {
            var full = Path.GetFullPath(project, cwd);
            if (File.Exists(full) && full.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) {
                projectFile = full;
                root = Path.GetDirectoryName(full)!;
            } else if (Directory.Exists(full)) {
                root = full;
            } else {
                throw new UsageException("--project is neither a folder nor a .csproj file: " + full);
            }
        }
        var settingsOption = args.Get(SettingsFileOption);
        string settingsPath;
        if (settingsOption != null) {
            settingsPath = Path.GetFullPath(settingsOption, cwd);
            if (Directory.Exists(settingsPath)) settingsPath = Path.Combine(settingsPath, Defaults.SettingsFileName);
            root ??= Path.GetDirectoryName(settingsPath)!;
        } else {
            root ??= findRootWithSettingsFile(cwd) ?? cwd;
            settingsPath = Path.Combine(root, Defaults.SettingsFileName);
        }
        var data = args.Get("data");
        if (data != null) root = Path.GetFullPath(data, cwd);
        projectFile ??= findProjectFile(root);

        var assemblies = args.GetAll("assembly").Select(p => Path.GetFullPath(p, cwd)).ToArray();
        foreach (var a in assemblies) if (!File.Exists(a)) throw new UsageException("--assembly file not found: " + a);
        var bins = args.GetAll("bin").Select(p => Path.GetFullPath(p, cwd)).ToArray();
        foreach (var b in bins) if (!Directory.Exists(b)) throw new UsageException("--bin folder not found: " + b);
        if (bins.Length == 0) bins = findOutputFolders(root);
        bins = [.. bins.Concat(assemblies.Select(a => Path.GetDirectoryName(a)!)).Distinct(StringComparer.OrdinalIgnoreCase)];

        return new Target {
            Root = root,
            SettingsPath = settingsPath,
            ProjectFile = projectFile,
            ProbeFolders = bins,
            AssemblyFiles = assemblies,
            Store = args.Get("store"),
        };
    }
    static string? findRootWithSettingsFile(string start) {
        var dir = new DirectoryInfo(start);
        for (var i = 0; i < 6 && dir != null; i++, dir = dir.Parent) {
            if (File.Exists(Path.Combine(dir.FullName, Defaults.SettingsFileName))) return dir.FullName;
        }
        return null;
    }
    static string? findProjectFile(string folder) {
        if (!Directory.Exists(folder)) return null;
        var projects = Directory.GetFiles(folder, "*.csproj");
        return projects.Length == 1 ? projects[0] : null;
    }
    /// <summary>The build output folders below <c>bin</c>, newest first, so a freshly built app wins.</summary>
    static string[] findOutputFolders(string root) {
        var bin = Path.Combine(root, "bin");
        if (!Directory.Exists(bin)) return [];
        var candidates = new List<(string Folder, DateTime Newest)>();
        foreach (var folder in Directory.EnumerateDirectories(bin, "*", SearchOption.AllDirectories)) {
            if (folder.Split(Path.DirectorySeparatorChar).Length - bin.Split(Path.DirectorySeparatorChar).Length > 3) continue;
            var dlls = Directory.GetFiles(folder, "*.dll");
            if (dlls.Length == 0) continue;
            candidates.Add((folder, dlls.Max(File.GetLastWriteTimeUtc)));
        }
        return [.. candidates.OrderByDescending(c => c.Newest).Select(c => c.Folder).Take(4)];
    }

    bool _probingRegistered;
    /// <summary>
    /// Makes the application's own assemblies loadable by name, which is what a
    /// <c>DatamodelSource</c> of type AssemblyNameReference needs. The Relatude.DB assemblies are
    /// deliberately not resolved from the application's output folder: the model types must bind to the
    /// ones this tool already has loaded, otherwise the attributes on them are different types.
    /// </summary>
    public void RegisterAssemblyProbing() {
        if (_probingRegistered) return;
        _probingRegistered = true;
        var folders = ProbeFolders;
        if (folders.Length > 0) Con.Detail("Probing for application assemblies in:" + string.Join("", folders.Select(f => Environment.NewLine + "  " + f)));
        AssemblyLoadContext.Default.Resolving += (context, name) => {
            if (name.Name == null) return null;
            foreach (var folder in folders) {
                var file = Path.Combine(folder, name.Name + ".dll");
                if (!File.Exists(file)) continue;
                Con.Detail("Loading " + file);
                return context.LoadFromAssemblyPath(file);
            }
            return null;
        };
    }
    /// <summary>Loads the assemblies given with --assembly, after enabling probing for their dependencies.</summary>
    public Assembly[] LoadExplicitAssemblies() {
        RegisterAssemblyProbing();
        var loaded = new List<Assembly>();
        foreach (var file in AssemblyFiles) {
            try {
                loaded.Add(AssemblyLoadContext.Default.LoadFromAssemblyPath(file));
            } catch (Exception err) {
                throw new CliException("Unable to load " + file + ": " + err.Message, err);
            }
        }
        return [.. loaded];
    }
    public string Describe() {
        var lines = new List<(string, string)> {
            ("content root", Root),
            ("settings", SettingsPath + (SettingsExists ? string.Empty : "  (does not exist)")),
        };
        if (ProjectFile != null) lines.Add(("project", ProjectFile));
        foreach (var f in ProbeFolders) lines.Add(("assemblies", f));
        return string.Join(Environment.NewLine, lines.Select(l => "  " + l.Item1.PadRight(13) + "  " + l.Item2));
    }
}
