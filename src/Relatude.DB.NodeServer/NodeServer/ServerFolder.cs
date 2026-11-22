using Relatude.DB.IO;
namespace Relatude.DB.NodeServer;

/// <summary>A folder of the machine the admin UI browses next to the database storage, under a fixed id.</summary>
public sealed record ServerFolder(Guid Id, string Name, string Note, IOProviderDisk IO);

static class ServerFolderFinder {
    static readonly Guid homeId = new("c4a7e2d9-3b18-4f6a-a05e-8d2b71f9c3e6");
    static readonly Guid azureLogFilesId = new("1e9b5c73-d4a2-4b08-96f1-2c7e8a3d5b94");
    static readonly Guid tempId = new("7d3f1a86-52c9-4e1b-b8a4-6f0e9c2d7a51");

    /// <summary>The app root first, then the system folders that exist on this machine.</summary>
    internal static ServerFolder[] Find(string contentRoot) {
        var azure = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME"));
        var folders = new List<ServerFolder> {
            new(RelatudeDBServer.ProjectRootIOId, "[App root]",
                "the folder the application runs from (its content root). These are the application's own files, not database storage: " +
                "what is changed or deleted here changes the running site, and the database's data folder may be below here too.",
                new IOProviderDisk(contentRoot, plainFolder: true)),
        };
        var home = Environment.GetEnvironmentVariable("HOME");
        add(folders, homeId, "[Home]", home, azure
            ? "%HOME%, the App Service's persistent storage. It survives restarts and deployments and is shared by every instance of the app; " +
              "the app root is usually below it, in site/wwwroot."
            : "%HOME%, the home folder of the account the server runs as.");
        if (azure && !string.IsNullOrWhiteSpace(home)) {
            add(folders, azureLogFilesId, "[Log files]", Path.Combine(home, "LogFiles"),
                "where App Service writes its own logs: web server logs, failed request traces, the application logs it collects " +
                "and the stdout log of an app that fails to start, for whichever of these are switched on.");
        }
        add(folders, tempId, "[Temp]", Path.GetTempPath(), azure
            ? "the system's temporary folder. On App Service it is on this instance's own disk: not shared with other instances, " +
              "and not kept when the app restarts or moves."
            : "the system's temporary folder, shared with every other program on the machine.");
        return [.. folders];
    }

    static void add(List<ServerFolder> folders, Guid id, string name, string? path, string note) {
        if (string.IsNullOrWhiteSpace(path)) return;
        try {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            // the provider would create a missing folder on its first write
            if (!Directory.Exists(full)) return;
            if (folders.Any(f => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(f.IO.BaseFolder)), full, StringComparison.OrdinalIgnoreCase))) return;
            folders.Add(new(id, name, note, new IOProviderDisk(full, plainFolder: true)));
        } catch {
            // a variable pointing somewhere unreadable leaves the folder out
        }
    }
}
