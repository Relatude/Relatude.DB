using Microsoft.AspNetCore.Mvc;
using Relatude.DB.CodeGeneration;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores;
using Relatude.DB.Demo;
using Relatude.DB.IO;
using Relatude.DB.Logging;
using Relatude.DB.Logging.Statistics;
using Relatude.DB.Nodes;
using Relatude.DB.NodeServer.Models;
using Relatude.DB.NodeServer.Settings;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime;
using System.Text.Json;
namespace Relatude.DB.NodeServer.API;

/// <summary>
/// The REST admin API: one route per action, grouped in sections under {ApiUrlRoot}.
/// <para>Only three sections are mapped now, because the admin UI (src/Relatude.DB.UI) does its
/// work over the two routes of <see cref="UI.UIServer"/> instead: the public authentication
/// endpoints under {ApiUrlRoot}/auth/, the public startup status route, and the file and database
/// downloads, which a browser has to fetch as urls rather than as commands.</para>
/// <para>The other sections are what the previous admin UI (src/Relatude.DB.ServerUI, no longer
/// built or served) called. Their mapping is commented out in <see cref="MapSimpleAPI"/> so none
/// of it is reachable, but the code is kept as the starting point for the UI sections that are
/// still to be built.</para>
/// </summary>
public partial class ServerAPIMapper(RelatudeDBServer server) {
    string ApiUrlPublic => server.ApiUrlPublic;
    string ApiUrlRoot => server.ApiUrlRoot;
    static string[] splitFolderPath(string? folderPath) => folderPath?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
    NodeStoreContainer container(Guid storeId) {
        if (server.Containers.TryGetValue(storeId, out var container)) return container;
        throw new Exception("Container not found.");
    }
    NodeStore db(Guid storeId) {
        return container(storeId).Store ?? throw new Exception("Store not initialized. ");
    }
    public void MapSimpleAPI(WebApplication app) {

        // Public API, NOT requiring authentication:
        mapPublicStatus(app);                                 // startup progress, polled by the startup page
        mapAuth(app, action => ApiUrlPublic + action + "/");  // authentication, login, ping, version, logout, etc.

        // The admin UI itself is mapped by UIServer: the page on ApiUrlRoot, its files under
        // ApiUrlPublic, and the two routes it talks over under ApiUrlRoot + "/ui/".

        // Private API, requiring authentication:
        var path = (string section) => ApiUrlRoot + "/" + section + "/";
        mapDownloads(app, action => path("maintenance") + action);

        // The sections below are the retired admin UI's API and are deliberately NOT mapped: no
        // client calls them any more (the current UI works over UIServer's command channel), so
        // leaving them reachable would be authenticated surface no one is using. The methods are
        // kept, not deleted - they are the fastest starting point for the UI sections that are
        // still to be built, and re-enabling one is a matter of uncommenting its line.
        //mapStatus(app, action => path("status") + action);         // SSE hub of the retired UI
        //mapSettings(app, action => path("settings") + action);
        //mapMaintenance(app, action => path("maintenance") + action); // minus mapDownloads above
        //mapServer(app, action => path("server") + action);
        //mapData(app, action => path("data") + action);
        //mapTasks(app, action => path("tasks") + action);
        //mapDatamodel(app, action => path("datamodel") + action);
        //mapLog(app, action => path("log") + action);
        //mapDemo(app, action => path("demo") + action);

    }

    public static string GetResource(string name) {
        var assembly = Assembly.GetExecutingAssembly();
        var prefix = assembly.GetName().Name + ".";
        using var stream = assembly.GetManifestResourceStream(prefix + name);
        if (stream == null) throw new Exception("Resource not found: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    /// <summary>
    /// The embedded resources whose name begins with the given prefix, without it - so
    /// ResourceNames("ClientUI.") gives "index.js", "earth.js" and the rest of the admin UI's files.
    /// </summary>
    public static IEnumerable<string> ResourceNames(string prefix) {
        var assembly = Assembly.GetExecutingAssembly();
        var full = assembly.GetName().Name + "." + prefix;
        return assembly.GetManifestResourceNames().Where(n => n.StartsWith(full, StringComparison.Ordinal)).Select(n => n[full.Length..]);
    }
    /// <summary>An embedded binary resource, or null when this build does not carry it. Used for
    /// optional assets of the admin UI, which is built separately (see UIServer.mapStaticUI).</summary>
    public static byte[]? GetBinaryResourceOrNull(string name) {
        var assembly = Assembly.GetExecutingAssembly();
        var prefix = assembly.GetName().Name + ".";
        using var stream = assembly.GetManifestResourceStream(prefix + name);
        if (stream == null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
    public static string GlobalPublicStatusUrl = "relatude.db-public-status";

    // PUBLIC API and with no authentication (controlled by urlpath in middleware):
    // The startup page (ClientStart/start.html, served by the middleware while databases are still
    // opening) polls this from outside the admin url, so it sits on its own global path.
    void mapPublicStatus(WebApplication app) {
        app.MapPost(GlobalPublicStatusUrl, () => {
            return StatusResponse(server);
        });
    }
    static DateTime _startUp = DateTime.UtcNow;
    public static SimpleStatus StatusResponse(RelatudeDBServer server) {
        var timeSinceStart = DateTime.UtcNow - _startUp;
        int nextUpdate;
        if (timeSinceStart.TotalSeconds < 5) nextUpdate = 200;
        else if (timeSinceStart.TotalSeconds < 30) nextUpdate = 1000;
        else if (timeSinceStart.TotalMinutes < 2) nextUpdate = 5000;
        else nextUpdate = 15000;
        var dbStatus = server.GetOpeningStatus();
        return new SimpleStatus {
            Starting = server.AnyRemaingToAutoOpenIncludingFailed,
            ProgressPercentage = dbStatus.ProgressPercentage,
            TimeRemainingMs = dbStatus.TimeRemainingMs,
            TimeElapsedMs = dbStatus.TimeElapsedMs,
            NextUpdate = nextUpdate,
            ResponseCheck = "Valid",
        };
    }
    public class SimpleStatus {
        public bool Starting { get; set; } = false;
        public int ProgressPercentage { get; set; } = 0;
        public int TimeRemainingMs { get; set; } = 0;
        public int TimeElapsedMs { get; set; } = 0;
        public string ResponseCheck { get; set; } = "";
        public int NextUpdate { get; set; } = 100;
    }
    class Credentials {
        public string UserName { get; set; } = "";
        public string Password { get; set; } = "";
        public bool Remember { get; set; } = false;
    }
    void mapAuth(WebApplication app, Func<string, string> path) {
        app.MapGet(path("ping"), () => "pong");
        app.MapPost(path("ping"), () => "pong");
        app.MapPost(path("login"), async (HttpContext context, Credentials c) => {
            var requestIP = context.Connection.RemoteIpAddress + "";
            var isLocal = LocalRequest.IsLocalhost(context);
            var valid = await server.Authentication.AreCredentialsValid(c.UserName, c.Password, requestIP, isLocal);
            if (valid) {
                server.Authentication.LogIn(context, c.Remember);
                return new { Success = true };
            }
            return new { Success = false };
        });
        app.MapPost(path("have-users"), (HttpContext context) => {
            return !string.IsNullOrEmpty(server.Settings.MasterUserName) && !string.IsNullOrEmpty(server.Settings.MasterPassword);
        });
        app.MapPost(path("is-logged-in"), (HttpContext context) => server.Authentication.IsLoggedIn(context));
        app.MapPost(path("version"), () => { return new { Version = "1.0.0" }; });
        app.MapPost(path("logout"), (HttpContext context) => server.Authentication.LogOut(context));
        // Sign in with Relatude.License, see LicenseLogin: whether the login page should offer it, the
        // redirect that starts it, and where the license server sends the browser back to.
        app.MapPost(path("license-login-options"), () => server.LicenseLogin.DescribeOptions());
        app.MapGet(path("license-login/start"), (HttpContext context) => server.LicenseLogin.StartAsync(context));
        app.MapGet(path("license-login/callback"), (HttpContext context, string? code, string? state) => server.LicenseLogin.CallbackAsync(context, code, state));
    }

    // PRIVATE API, requires authentication (controlled by path in middleware):
    /// <summary>The part of the maintenance section that is still mapped: file and database
    /// downloads. They stay because a browser has to fetch a download as a url, which the admin
    /// UI's command channel cannot express. The urls are unchanged, so they are still
    /// {ApiUrlRoot}/maintenance/download-... </summary>
    void mapDownloads(WebApplication app, Func<string, string> path) {
        app.MapGet(path("download-file"), (HttpContext ctx, Guid storeId, Guid ioId, string fileName) => {
            var fileKey = fileName.SplitKey();
            var io = server.GetIO(ioId);
            var contentType = MediaTypeHeaderValue.Parse("application/octet-stream").ToString();
            // Disk backed files are opened directly: engine owned files (e.g. the index stores) can be
            // OS locked without the provider knowing, and the provider's OpenRead retries such files
            // for minutes while holding the provider lock, stalling every other file operation. A
            // locked file fails fast as 423 instead, so a folder download can warn and keep going.
            if (io.TryGetLocalFilePath(fileKey, out var localFilePath)) {
                try {
                    var fileStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return Results.File(fileStream, contentType, fileKey.FileName(), null, null, true);
                } catch (FileNotFoundException) {
                    return Results.NotFound();
                } catch (DirectoryNotFoundException) {
                    return Results.NotFound();
                } catch (IOException) {
                    return Results.StatusCode(StatusCodes.Status423Locked);
                }
            }
            var ioStream = io.OpenRead(fileKey, 0);
            var stream = ReadStreamWrapper.Wrap(ioStream);
            return Results.File(stream, contentType, fileKey.FileName(), null, null, true);
        });
        app.MapGet(path("download-truncated-db"), (Guid storeId, string namePrefix) => {
            namePrefix = string.Concat(namePrefix.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ' ' || c == '.'));
            if (namePrefix.Length > 100) namePrefix = namePrefix.Substring(0, 100);
            if (namePrefix.Length > 0 && !namePrefix.EndsWith(" ")) namePrefix += " ";
            string[] fileKey = [Guid.NewGuid().ToString()];
            db(storeId).Datastore.RewriteStore(false, fileKey, server.TempIO);
            var ioStream = server.TempIO.OpenRead(fileKey, 0);
            var stream = ReadStreamWrapper.Wrap(ioStream);
            var name = container(storeId).Settings.Name;
            if (string.IsNullOrEmpty(name)) name = "Database";
            var fileName = name + " " + DateTime.UtcNow.ToString("yyyy-MM-dd HH-mm-ss") + ".bin";
            //var fileName = FileKeyUtility.Log_NextFileKey(datastore.IO);
            return Results.File(stream, MediaTypeHeaderValue.Parse("application/octet-stream").ToString(), namePrefix + fileName);
        });
        app.MapGet(path("download-full-db"), (Guid storeId, string namePrefix) => {
            namePrefix = string.Concat(namePrefix.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ' ' || c == '.'));
            if (namePrefix.Length > 100) namePrefix = namePrefix.Substring(0, 100);
            if (namePrefix.Length > 0 && !namePrefix.EndsWith(" ")) namePrefix += " ";
            string[] fileKey = [Guid.NewGuid().ToString()];
            var datastore = container(storeId).Store!.Datastore;
            datastore.CopyStore(fileKey, server.TempIO);
            var ioStream = server.TempIO.OpenRead(fileKey, 0);
            var stream = ReadStreamWrapper.Wrap(ioStream);
            var name = container(storeId).Settings.Name;
            if (string.IsNullOrEmpty(name)) name = "Database";
            var fileName = name + " " + DateTime.UtcNow.ToString("yyyy-MM-dd HH-mm-ss") + ".bin";
            //var fileName = FileKeyUtility.Log_NextFileKey(datastore.IO);
            return Results.File(stream, MediaTypeHeaderValue.Parse("application/octet-stream").ToString(), namePrefix + fileName);
        });
    }
}
