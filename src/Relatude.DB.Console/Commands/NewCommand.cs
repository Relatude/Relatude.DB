using System.Reflection;
using System.Text;
using Relatude.DB.NodeServer;

namespace Relatude.DB.Cli.Commands;

/// <summary>
/// Creates a new web application from the template embedded in this tool (Templates/WebApp in the
/// source): a Backend folder with an ASP.NET Core minimal API on Relatude.DB and a Client folder with a
/// React + TypeScript + Vite app. Source only, nothing built: the caller runs dotnet and npm afterwards.
/// </summary>
public static class NewCommand {
    const string _templatePrefix = "Templates/WebApp/";
    const string _backendFolder = "Backend";
    const string _backendAssembly = "Backend";

    public static Task<int> RunAsync(CommandArgs args) {
        args.Accept("out", "user", "password", "package-version", "force");
        var name = args.SinglePositional("project name")
            ?? throw new UsageException("A project name is required, for example: relatude new MyApp");
        if (name.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' ')) || !name.Any(char.IsLetter)) {
            throw new UsageException("The project name needs a letter and may only hold letters, digits, '-', '_', '.' and spaces: \"" + name + "\".");
        }
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

        var written = new List<string>();
        var assembly = typeof(NewCommand).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Select(n => (Resource: n, Path: n.Replace('\\', '/')))
            .Where(r => r.Path.StartsWith(_templatePrefix, StringComparison.Ordinal))
            .OrderBy(r => r.Path, StringComparer.Ordinal)
            .ToArray();
        if (resources.Length == 0) throw new CliException("This build of the tool holds no project template.");
        foreach (var (resource, path) in resources) {
            var relative = path[_templatePrefix.Length..];
            var target = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd()
                .Replace("__NAME__", name)
                .Replace("__NAMESPACE__", ns)
                .Replace("__PACKAGE_NAME__", packageName)
                .Replace("__PACKAGE_VERSION__", packageVersion);
            File.WriteAllText(target, text, new UTF8Encoding(false));
            written.Add(relative);
        }

        // the settings file is generated, not copied: fresh ids and secret, the model namespace of this project
        var settings = SettingsTemplate.Create(
            databaseName: name,
            dataPath: Defaults.DataFolderPath,
            modelNamespace: ns + ".Models",
            assemblyName: _backendAssembly,
            user: args.Get("user"),
            password: args.Get("password"),
            waitUntilOpen: true);
        var settingsRelative = _backendFolder + "/" + Defaults.SettingsFileName;
        File.WriteAllText(Path.Combine(root, _backendFolder, Defaults.SettingsFileName), SettingsReader.Serialize(settings));
        written.Add(settingsRelative);

        var relativeRoot = Path.GetRelativePath(cwd, root);
        if (relativeRoot.StartsWith("..")) relativeRoot = root; // far away: the absolute path reads better
        if (args.Flag("json")) {
            Output.Json(new {
                Folder = root,
                Name = name,
                Namespace = ns,
                ModelNamespace = ns + ".Models",
                PackageVersion = packageVersion,
                AdminUser = args.Get("user"),
                Files = written,
            });
            return Task.FromResult(0);
        }
        Output.WriteLine("Created " + name + " in " + root);
        Output.Table([
            (_backendFolder + "/", "ASP.NET Core minimal API + Relatude.DB " + packageVersion + " (net10.0)"),
            ("Client/", "React + TypeScript + Vite"),
            ("model namespace", ns + ".Models, in " + _backendFolder + "/Models"),
            ("admin login", args.Get("user") ?? "not set (not needed on localhost)"),
        ]);
        Output.WriteLine();
        Output.WriteLine("Next, in two terminals:");
        Output.WriteLine("  cd " + quote(relativeRoot));
        Output.WriteLine("  dotnet run --project Backend --launch-profile https           # API on https://localhost:7238");
        Output.WriteLine("  npm install --prefix Client && npm run dev --prefix Client     # client on http://localhost:5173");
        Output.WriteLine("Then open http://localhost:5173. The admin UI is at /relatude.db on either port.");
        Output.WriteLine("README.md explains where models, endpoints and client code go.");
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

    /// <summary>"my-app" or "my app" becomes MyApp: a C# identifier for the root namespace.</summary>
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
