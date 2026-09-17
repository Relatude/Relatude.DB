using System.Reflection;
using System.Text;
using Relatude.DB.NodeServer;

namespace Relatude.DB.Cli.Commands;

/// <summary>
/// Creates a new application from one of the templates embedded in this tool (Templates/&lt;type&gt; in
/// the source, described by <see cref="ProjectTypes"/>). Source only, nothing built: the caller runs
/// dotnet (and npm, for the types that have a client) afterwards.
/// </summary>
public static class NewCommand {
    const string _templatesRoot = "Templates/";

    public static Task<int> RunAsync(CommandArgs args) {
        args.Accept("out", "user", "password", "package-version", "force", ProjectTypes.Option, "project-type", "list-types");
        if (args.Flag("list-types")) {
            ProjectTypes.WriteList(args.Flag("json"));
            return Task.FromResult(0);
        }
        var name = args.SinglePositional("project name")
            ?? throw new UsageException("A project name is required, for example: relatude new MyApp");
        if (name.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' ')) || !name.Any(char.IsLetter)) {
            throw new UsageException("The project name needs a letter and may only hold letters, digits, '-', '_', '.' and spaces: \"" + name + "\".");
        }
        var typeName = args.Get(ProjectTypes.Option) ?? args.Get("project-type");
        var type = ProjectTypes.Resolve(typeName);
        var ns = toIdentifier(name);
        var packageName = toPackageName(name) + "-client";
        var packageVersion = args.Get("package-version") ?? DefaultPackageVersion;
        var cwd = Directory.GetCurrentDirectory();
        var root = Path.GetFullPath(args.Get("out") ?? name, cwd);
        if (File.Exists(root)) throw new CliException("The target is a file, not a folder: " + root);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any() && !args.Flag("force")) {
            throw new CliException("The folder is not empty: " + root + Environment.NewLine
                + "Pass --force to write into it anyway; files with the same names are overwritten, others are kept.");
        }

        string substitute(string text) => text
            .Replace("__NAME__", name)
            .Replace("__NAMESPACE__", ns)
            .Replace("__PACKAGE_NAME__", packageName)
            .Replace("__PACKAGE_VERSION__", packageVersion);

        var prefix = _templatesRoot + type.Name + "/";
        var written = new List<string>();
        var assembly = typeof(NewCommand).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Select(n => (Resource: n, Path: n.Replace('\\', '/')))
            .Where(r => r.Path.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(r => r.Path, StringComparer.Ordinal)
            .ToArray();
        if (resources.Length == 0) throw new CliException("This build of the tool holds no template for the project type " + type.Name + ".");
        foreach (var (resource, path) in resources) {
            var relative = substitute(path[prefix.Length..]); // file names carry placeholders too: __NAMESPACE__.csproj
            var target = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            File.WriteAllText(target, substitute(reader.ReadToEnd()), new UTF8Encoding(false));
            written.Add(relative);
        }

        // the settings file is generated, not copied: fresh ids and secret, the model namespace of this project
        var assemblyName = type.AssemblyName ?? ns;
        var modelNamespace = ns + ".Models";
        var settings = SettingsTemplate.Create(
            databaseName: name,
            dataPath: Defaults.DataFolderPath,
            modelNamespace: modelNamespace,
            assemblyName: assemblyName,
            user: args.Get("user"),
            password: args.Get("password"),
            waitUntilOpen: true);
        var projectFolder = Path.Combine(root, type.ProjectFolder);
        Directory.CreateDirectory(projectFolder);
        File.WriteAllText(Path.Combine(projectFolder, Defaults.SettingsFileName), SettingsReader.Serialize(settings));
        written.Add(joinRelative(type.ProjectFolder, Defaults.SettingsFileName));

        var relativeRoot = Path.GetRelativePath(cwd, root);
        if (relativeRoot.StartsWith("..")) relativeRoot = root; // far away: the absolute path reads better
        if (args.Flag("json")) {
            Output.Json(new {
                Folder = root,
                Name = name,
                ProjectType = type.Name,
                Namespace = ns,
                ModelNamespace = modelNamespace,
                ModelFolder = joinRelative(type.ProjectFolder, "Models"),
                ProjectFolder = type.ProjectFolder,
                PackageVersion = packageVersion,
                AdminUser = args.Get("user"),
                NextSteps = type.NextSteps.Select(s => type.Substitute(s, name, ns, packageVersion, root).Trim()).Where(s => s.Length > 0),
                Files = written,
            });
            return Task.FromResult(0);
        }
        Output.WriteLine("Created " + name + " in " + root);
        Output.Table([
            ("project type", type.Name + " - " + type.Title),
            .. type.Summary.Select(r => (type.Substitute(r.Key, name, ns, packageVersion, root), type.Substitute(r.Value, name, ns, packageVersion, root))),
            ("model namespace", modelNamespace + ", in " + joinRelative(type.ProjectFolder, "Models")),
            ("admin login", args.Get("user") ?? "not set (not needed on localhost)"),
        ]);
        Output.WriteLine();
        foreach (var line in type.NextSteps) Output.WriteLine(type.Substitute(line, name, ns, packageVersion, quote(relativeRoot)));
        Output.WriteLine("README.md explains where models, pages and endpoints go.");
        if (typeName == null) {
            Output.Info("Project type " + type.Name + " is the default; \"relatude new --list-types\" describes the others.");
        }
        return Task.FromResult(0);
    }

    /// <summary>
    /// The Relatude.DB package version written into the generated csproj: the version this tool was
    /// published as, so the two always match. --package-version overrides it.
    /// </summary>
    public static string DefaultPackageVersion {
        get {
            var assembly = typeof(NewCommand).Assembly;
            var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(info)) return assembly.GetName().Version?.ToString() ?? "*-*";
            var plus = info.IndexOf('+'); // the source revision is not part of the package version
            return plus < 0 ? info : info[..plus];
        }
    }

    static string joinRelative(string folder, string file) => folder.Length == 0 ? file : folder + "/" + file;

    /// <summary>"my-app" or "my app" becomes MyApp: a C# identifier for the root namespace and the assembly.</summary>
    static string toIdentifier(string name) {
        var sb = new StringBuilder();
        var startOfWord = true;
        foreach (var c in name) {
            if (!char.IsLetterOrDigit(c)) { startOfWord = true; continue; }
            sb.Append(startOfWord ? char.ToUpperInvariant(c) : c);
            startOfWord = false;
        }
        if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, "App");
        return sb.ToString();
    }

    /// <summary>"MyApp" becomes myapp, "My App 2" becomes my-app-2: a valid npm package name.</summary>
    static string toPackageName(string name) {
        var sb = new StringBuilder();
        foreach (var c in name) {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().TrimEnd('-');
    }

    static string quote(string path) => path.Contains(' ') ? "\"" + path + "\"" : path;
}
