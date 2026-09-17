namespace Relatude.DB.Cli;

/// <summary>
/// A project template "relatude new" can write. Each one is a folder under Templates/&lt;Name&gt; in the
/// source, embedded as text. Names follow language_platform_flavour (csharp_web_mvc) so templates for
/// other stacks can be added next to these.
/// </summary>
public sealed class ProjectType {
    public required string Name { get; init; }
    /// <summary>One line: what the generated project is.</summary>
    public required string Title { get; init; }
    /// <summary>One or two sentences: how the generated files are laid out.</summary>
    public required string Layout { get; init; }
    /// <summary>One or two sentences: when to choose this type over the others.</summary>
    public required string SuitedFor { get; init; }
    public required string Prerequisites { get; init; }
    /// <summary>Folder inside the project that holds the C# project and relatude.db.json, "" for the root.</summary>
    public required string ProjectFolder { get; init; }
    /// <summary>Assembly name of the C# project, or null when it is the project's identifier (the name as PascalCase).</summary>
    public string? AssemblyName { get; init; }
    /// <summary>Rows for the summary printed after generating. {name}, {ns} and {version} are substituted.</summary>
    public required (string Key, string Value)[] Summary { get; init; }
    /// <summary>What to run next, printed verbatim after generating. {root} is the folder the project was written to.</summary>
    public required string[] NextSteps { get; init; }
    public bool IsDefault { get; init; }

    public string Substitute(string text, string name, string ns, string version, string root)
        => text.Replace("{name}", name).Replace("{ns}", ns).Replace("{version}", version).Replace("{root}", root);
}

public static class ProjectTypes {
    public const string Option = "projecttype";

    public static readonly ProjectType[] All = [
        new() {
            Name = "csharp_web_react_ts",
            IsDefault = true,
            Title = "C# ASP.NET Core API + React/TypeScript client (single page application)",
            Layout = "Two folders: Backend/ is an ASP.NET Core minimal API on Relatude.DB, Client/ is a React + TypeScript + Vite "
                + "app that calls it under /api and is built into Backend/wwwroot for production.",
            SuitedFor = "Interactive web applications: rich, app-like user interfaces where a browser client talks to an API.",
            Prerequisites = ".NET 10 SDK and Node.js 20 or newer",
            ProjectFolder = "Backend",
            AssemblyName = "Backend",
            Summary = [
                ("Backend/", "ASP.NET Core minimal API + Relatude.DB {version} (net10.0)"),
                ("Client/", "React + TypeScript + Vite"),
            ],
            NextSteps = [
                "Next, in two terminals:",
                "  cd {root}",
                "  dotnet run --project Backend --launch-profile https           # API on https://localhost:7238",
                "  npm install --prefix Client && npm run dev --prefix Client     # client on http://localhost:5173",
                "Then open http://localhost:5173. The admin UI is at /relatude.db on either port.",
            ],
        },
        new() {
            Name = "csharp_web_mvc",
            Title = "C# ASP.NET Core MVC website, one project",
            Layout = "One project: controllers, Razor views and static files, with the HTML rendered on the server. "
                + "No JavaScript build step.",
            SuitedFor = "Content websites with traditional page navigation and SEO: every page is a crawlable, "
                + "server-rendered URL with its own title and description.",
            Prerequisites = ".NET 10 SDK",
            ProjectFolder = "",
            AssemblyName = null,
            Summary = [
                ("{ns}.csproj", "ASP.NET Core MVC + Relatude.DB {version} (net10.0)"),
                ("Controllers/, Views/", "server-rendered pages; HomeController renders the start page"),
            ],
            NextSteps = [
                "Next:",
                "  cd {root}",
                "  dotnet run --launch-profile https                              # site on https://localhost:7238",
                "Then open https://localhost:7238. The admin UI is at https://localhost:7238/relatude.db.",
            ],
        },
    ];

    public static ProjectType Default => All.Single(t => t.IsDefault);

    /// <summary>The type named on the command line, the default when none was; a usage error for an unknown name.</summary>
    public static ProjectType Resolve(string? name) {
        if (name == null) return Default;
        var type = All.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? All.FirstOrDefault(t => t.Name.Replace('_', '-').Equals(name.Replace('_', '-'), StringComparison.OrdinalIgnoreCase));
        return type ?? throw new UsageException("Unknown project type \"" + name + "\". Known types: "
            + string.Join(", ", All.Select(t => t.Name)) + ". \"relatude new --list-types\" describes them.");
    }

    /// <summary>The list an agent or a person picks from: every type with what it is for.</summary>
    public static void WriteList(bool json) {
        if (json) {
            Output.Json(All.Select(t => new {
                t.Name, t.Title, t.Layout, t.SuitedFor, t.Prerequisites, t.IsDefault,
            }));
            return;
        }
        Output.WriteLine("Project types for \"relatude new <name> --" + Option + " <type>\":");
        foreach (var t in All) {
            Output.WriteLine();
            Output.WriteLine("  " + t.Name + (t.IsDefault ? "   (default)" : ""));
            Output.WriteLine("      " + t.Title);
            Output.Table([
                ("Layout", t.Layout),
                ("Suited for", t.SuitedFor),
                ("Needs", t.Prerequisites),
            ], indent: "      ");
        }
        Output.WriteLine();
        Output.WriteLine("Pick by what the site is for: an interactive application wants the SPA type, a content site");
        Output.WriteLine("with pages, navigation and search engines wants the MVC type.");
    }
}
