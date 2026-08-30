using Microsoft.AspNetCore.Mvc;
using Relatude.DB.CodeGeneration;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores;
using Relatude.DB.Demo;
using Relatude.DB.IO;
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
    }

    // PRIVATE API, requires authentication (controlled by path in middleware):
    void mapStatus(WebApplication app, Func<string, string> path) {
        app.MapGet(path("connect"), server.EventHub.Connect);
        app.MapPost(path("subscribe"), server.EventHub.Subscribe);
        app.MapPost(path("unsubscribe"), server.EventHub.Unsubscribe);
        app.MapPost(path("uptime-in-ms"), () => server.UpTime.TotalMilliseconds);
    }
    void mapSettings(WebApplication app, Func<string, string> path) {
        app.MapPost(path("get-settings"), (Guid storeId, bool prettyJson) => {
            return Results.Json(container(storeId).Settings, prettyJson ? LocalSettingsLoaderFile.PrettyJsonOptions : null);
        });
        app.MapPost(path("set-settings"), async (Guid storeId, HttpRequest request) => {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            var settings = JsonSerializer.Deserialize<NodeStoreContainerSettings>(body, LocalSettingsLoaderFile.PrettyJsonOptions);
            if (settings == null) throw new Exception("Invalid settings data. ");
            container(storeId).ApplyNewSettings(settings, true);
            server.UpdateWAFServerSettingsFile();
        });
        app.MapPost(path("re-save-settings"), (Guid storeId) => {
            server.UpdateWAFServerSettingsFile();
        });
    }
    void mapMaintenance(WebApplication app, Func<string, string> path) {
        app.MapPost(path("open"), (Guid storeId) => container(storeId).Open());
        app.MapPost(path("close"), (Guid storeId) => {
            container(storeId).CloseIfOpen();
            if (!server.GetContainers().Any(c => c.IsOpenOrOpening())) server.ResetIOProviders();
        });
        app.MapPost(path("cancel-opening"), (Guid storeId) => {
            try {
                container(storeId).CloseIfOpen();
                if (!server.GetContainers().Any(c => c.IsOpenOrOpening())) server.ResetIOProviders();
            } catch { }
        });
        app.MapPost(path("close-all-open-streams"), (Guid storeId, Guid ioId) => server.GetIO(ioId).CloseAllOpenStreams());
        app.MapPost(path("get-store-files"), (Guid storeId, Guid ioId) => server.GetIO(ioId).GetFiles());
        app.MapPost(path("can-have-folders"), (Guid storeId, Guid ioId) => new { CanHave = true });
        app.MapPost(path("get-folders"), (Guid storeId, Guid ioId) => server.GetIO(ioId).GetFoldersAsync([], true, true)); // kept for older clients
        // one level of the given folder ("" = the storage root): its files and subfolder stubs, no sizes computed
        app.MapPost(path("get-folder"), (Guid storeId, Guid ioId, string? folderPath) => server.GetIO(ioId).GetFolderAsync(splitFolderPath(folderPath), false, true));
        // the full tree of the given folder, used by the client side folder download to plan the files to fetch
        app.MapPost(path("get-folder-recursive"), (Guid storeId, Guid ioId, string? folderPath) => server.GetIO(ioId).GetFolderAsync(splitFolderPath(folderPath), true, true));
        // recursive size and counts of the given folder, on demand: walking a big tree can take a
        // while, so the client asks per folder instead of the listing computing it eagerly
        app.MapPost(path("get-folder-size"), async (Guid storeId, Guid ioId, string? folderPath) => {
            var folder = await server.GetIO(ioId).GetFolderAsync(splitFolderPath(folderPath), true, true);
            long size = 0; long fileCount = 0; long folderCount = 0;
            void sum(FolderMeta f) {
                foreach (var file in f.Files) { size += file.Size; fileCount++; }
                foreach (var sub in f.SubFolders) { folderCount++; sum(sub); }
            }
            sum(folder);
            return new { Size = size, FileCount = fileCount, FolderCount = folderCount };
        });
        app.MapPost(path("delete-folder"), (Guid storeId, Guid ioId, string folderName) => server.GetIO(ioId).DeleteFolderIfItExists(splitFolderPath(folderName)));
        app.MapPost(path("file-exist"), (Guid storeId, Guid ioId, string fileName) => !server.GetIO(ioId).DoesNotExistOrIsEmpty(fileName.SplitKey()));
        app.MapPost(path("backup-now"), (Guid storeId, Guid ioId, bool truncate, bool keepForever) => {
            if (ioId == Guid.Empty) {
                var settings = container(storeId).Settings;
                if (settings.IoBackup.HasValue && settings.IoBackup != Guid.Empty) ioId = settings.IoBackup.Value;
                else ioId = settings.IoDatabase ?? throw new Exception("No IoBackup or IoDatabase defined in settings. ");
            }
            db(storeId).Datastore.BackUpNow(truncate, keepForever, server.GetIO(ioId));
        });
        app.MapPost(path("cancel-rewrite-if-any"), (Guid storeId) => new { FileKey = db(storeId).Datastore.CancelRunningRewriteIfAny() });

        app.MapPost(path("is-file-key-legal"), (string fileKey) => new { IsLegal = FileKeyUtility.IsFileKeyValid(fileKey) });
        app.MapPost(path("is-file-prefix-legal"), (string filePrefix) => new { IsLegal = FileKeyUtility.IsFilePrefixValid(filePrefix, out _) });
        app.MapPost(path("get-file-key-of-db"), (Guid storeId, Guid ioId) => {
            var settings = container(storeId).Settings;
            if (settings.IoDatabase != ioId) return string.Empty;
            return FileKeyUtility.WAL_GetLatestFileKey(server.GetIO(ioId)).AsKeyString();
        });
        app.MapPost(path("get-file-key-of-db-next"), (Guid storeId, Guid ioId) => {
            var settings = container(storeId).Settings;
            if (settings.IoDatabase != ioId) return string.Empty;
            return FileKeyUtility.WAL_NextFileKey(server.GetIO(ioId)).AsKeyString();
        });
        app.MapPost(path("delete-file"), (HttpContext ctx, Guid storeId, Guid ioId, string fileName) => {
            server.GetIO(ioId).DeleteFileIfItExists(fileName.SplitKey());
        });
        app.MapPost(path("can-rename-file"), (Guid storeId, Guid ioId) => new { CanRename = server.GetIO(ioId).CanRenameFile });
        app.MapPost(path("rename-file"), (Guid storeId, Guid ioId, string fileName, string newFileName) => {
            server.GetIO(ioId).RenameFile(fileName.SplitKey(), newFileName.SplitKey());
        });
        app.MapPost(path("initiate-upload"), () => { return new { Value = Guid.NewGuid().ToString() }; });
        app.MapPost(path("upload-part"), async (HttpContext ctx, Guid uploadId) => {
            using var ioStream = server.TempIO.OpenAppend([uploadId.ToString()]);
            using var writeStream = new WriteStreamWrapper(ioStream);
            await ctx.Request.Body.CopyToAsync(writeStream);
        });
        app.MapPost(path("cancel-upload"), (HttpContext ctx, Guid uploadId) => server.TempIO.DeleteFileIfItExists([uploadId.ToString()]));
        app.MapPost(path("complete-upload"), (HttpContext ctx, Guid storeId, Guid ioId, Guid uploadId, string fileName, bool overwrite) => {
            string[] uploadKey = [uploadId.ToString()];
            if (server.TempIO.DoesNotExistsOrIsEmpty(uploadKey)) throw new Exception("Upload not found");
            var destIo = server.GetIO(ioId);
            var fileKey = fileName.SplitKey();
            if (FileKeyUtility.State_IsStateFileKey(fileKey)) {
                server.TempIO.DeleteFileIfItExists(uploadKey);
                throw new Exception("Uploading state file is not allowed. ");
            }
            FileKeyUtility.State_DeleteAll(destIo); // delete the state files to avoid old statefile and newer log file!
            if (destIo is IOProviderDisk diskIO && server.TempIO is IOProviderDisk tempDiskIO) {
                diskIO.MoveFile(tempDiskIO, uploadKey, fileKey, overwrite);
                return;
            }
            using var ioSourceStream = server.TempIO.OpenRead(uploadKey, 0);
            if (!destIo.DoesNotExistsOrIsEmpty(fileKey) && !overwrite) throw new Exception("File already exists");
            destIo.DeleteFileIfItExists(fileKey);
            using var ioDestStream = destIo.OpenAppend(fileKey);
            using var readStream = ReadStreamWrapper.Wrap(ioSourceStream);
            using var writeStream = new WriteStreamWrapper(ioDestStream);
            readStream.CopyTo(writeStream);
            ioSourceStream.Dispose();
            server.TempIO.DeleteFileIfItExists(uploadKey);
        });
        app.MapPost(path("truncate-log"), (HttpContext ctx, Guid storeId, bool deleteOld) => {
            var options = MaintenanceAction.TruncateLog;
            if (deleteOld) options |= MaintenanceAction.DeleteOldLogs;
            db(storeId).MaintenanceAsync(options);
        });
        app.MapPost(path("save-index-states"), (HttpContext ctx, Guid storeId, bool forceRefresh, bool nodeSegmentsOnly) => db(storeId).Datastore.SaveIndexStates(forceRefresh, nodeSegmentsOnly));
        app.MapPost(path("update-persisted-caches"), (HttpContext ctx, Guid storeId) => db(storeId).Datastore.MaintenanceAsync(MaintenanceAction.UpdatePersistedCaches));
        app.MapPost(path("reset-secondary-log-file"), (HttpContext ctx, Guid storeId) => db(storeId).MaintenanceAsync(MaintenanceAction.ResetSecondaryLogFile));
        app.MapPost(path("reset-state-and-indexes"), (HttpContext ctx, Guid storeId) => db(storeId).MaintenanceAsync(MaintenanceAction.ResetStateAndIndexes));
        app.MapPost(path("delete-state-and-indexes"), (HttpContext ctx, Guid storeId) => container(storeId).DeleteAllStateAndIndexFiles());
        app.MapPost(path("clear-cache"), (HttpContext ctx, Guid storeId) => db(storeId).Datastore.MaintenanceAsync(MaintenanceAction.ClearCache | MaintenanceAction.GarbageCollect));
        app.MapPost(path("info"), async (HttpContext ctx, Guid storeId) => {
            var store = container(storeId).Store;
            if (store == null) return null;
            return await store.Datastore.GetInfoAsync();
        });
        app.MapPost(path("clean-temp-files"), () => server.TempIO.GetFiles().ForEach(file => { try { server.TempIO.DeleteFileIfItExists(file.KeyOf()); } catch { } }));
        app.MapPost(path("get-size-temp-files"), () => new { TotalSize = server.TempIO.GetFiles().Sum(file => file.Size) });
        app.MapPost(path("copy-file"), (Guid storeId, Guid fromIoId, string fromFileName, Guid toIoId, string toIoFileName) => {
            var io = server.GetIO(toIoId);
            io.CopyFile(fromFileName.SplitKey(), toIoFileName.SplitKey());
        });

        // scanning the file stores can take a while on big databases, so these run as background
        // jobs the client polls for progress and can cancel
        app.MapPost(path("delete-unreferenced-files-start"), (Guid storeId, bool countOnly) => {
            if (db(storeId).Datastore is not DataStoreLocal local) throw new Exception("Only supported for local data stores. ");
            var job = FileScanJobs.Start(storeId, "unreferenced files", async j =>
                (object)await local.DeleteUnreferencedFilesAsync(countOnly, j.SetProgress, j.Cancellation.Token));
            return new { JobId = job.Id };
        });
        app.MapPost(path("delete-unreferenced-files-progress"), (Guid storeId, Guid jobId) => {
            var job = FileScanJobs.Get(jobId);
            var result = job.Result as DataStores.Files.DeleteUnReferenceResult;
            return new {
                job.State,
                job.Description,
                job.Percent,
                job.Error,
                TotalBytesDeleted = result?.TotalBytesDeleted ?? 0,
                TotalFilesDeleted = result?.TotalFilesDeleted ?? 0,
                TotalFoldersDeleted = result?.TotalFoldersDeleted ?? 0,
            };
        });
        app.MapPost(path("delete-unreferenced-files-cancel"), (Guid storeId, Guid jobId) => FileScanJobs.Get(jobId).Cancellation.Cancel());

        // the other direction: every file value in the database checked against the store it points at
        app.MapPost(path("find-missing-files-start"), (Guid storeId) => {
            if (db(storeId).Datastore is not DataStoreLocal local) throw new Exception("Only supported for local data stores. ");
            var job = FileScanJobs.Start(storeId, "missing files", async j =>
                (object)await local.FindMissingFilesAsync(j.SetProgress, j.Cancellation.Token));
            return new { JobId = job.Id };
        });
        app.MapPost(path("find-missing-files-progress"), (Guid storeId, Guid jobId) => {
            var job = FileScanJobs.Get(jobId);
            var result = job.Result as DataStores.Files.MissingFilesResult;
            return new {
                job.State,
                job.Description,
                job.Percent,
                job.Error,
                NodesScanned = result?.NodesScanned ?? 0,
                FilesChecked = result?.FilesChecked ?? 0,
                MissingCount = result?.MissingCount ?? 0,
                MissingBytes = result?.MissingBytes ?? 0,
                ListTruncated = result?.ListTruncated ?? false,
                // the result, and with it the list, is only set once the job is done, so the entries
                // are sent once instead of being repeated on every poll
                Missing = result?.Missing ?? Array.Empty<DataStores.Files.MissingFileInfo>(),
            };
        });
        app.MapPost(path("find-missing-files-cancel"), (Guid storeId, Guid jobId) => FileScanJobs.Get(jobId).Cancellation.Cancel());
    }
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
    void mapServer(WebApplication app, Func<string, string> path) {
        app.MapPost(path("get-store-containers"), () => {
            return server.GetContainers().Select(c => new {
                c.Settings.Id,
                c.Settings.Name,
                c.Settings.Description,
                Status = c.GetStatusAndActivity(),
                c.Settings.IoDatabase,
            });
        });
        app.MapPost(path("get-default-store-id"), () => server.Settings.DefaultStoreId.ToString());
        app.MapPost(path("set-default-store-id"), (Guid storeId) => {
            server.Settings.DefaultStoreId = storeId;
            server.UpdateWAFServerSettingsFile();
        });
        //app.MapPost(path("set-master-credentials"), ([FromBody] dynamic settings) => {
        //    server.Settings.MasterUserName = settings.MasterUserName;
        //    server.Settings.MasterPassword = settings.MasterPassword;
        //    server.UpdateWAFServerSettingsFile();
        //});
        //app.MapPost(path("set-name-and-description"), ([FromBody] dynamic settings) => {
        //    server.Settings.Name = settings.Name;
        //    server.Settings.Description = settings.Description;
        //    server.UpdateWAFServerSettingsFile();
        //});
        app.MapPost(path("create-store"), () => {
            var id = Guid.NewGuid();
            var containerSettings = new NodeStoreContainerSettings() { Id = id, Name = "New Store" };
            var container = new NodeStoreContainer(containerSettings, server);
            lock (server.Containers) server.Containers.Add(id, container);
            server.UpdateWAFServerSettingsFile();
            return containerSettings;
        });
        app.MapPost(path("remove-store"), (Guid storeId) => {
            container(storeId).Dispose();
            lock (server.Containers) server.Containers.Remove(storeId);
            server.UpdateWAFServerSettingsFile();
        });
        app.MapPost(path("get-server-log"), () => server.GetStartUpLog().Select(e => { return new { Timestamp = e.Item1, Description = e.Item2 }; }).ToArray());
        app.MapPost(path("clear-server-log"), server.ClearStartUpLog);
        app.MapPost(path("get-restart-info"), () => server.GetRestartCapabilities());
        app.MapPost(path("soft-restart"), () => {
            if (!server.GetRestartCapabilities().CanSoftRestart) throw new Exception("Soft restart is not allowed on this server. ");
            if (server.IsRestarting) return new { Started = false, Message = "A restart is already running." };
            // closing and rebuilding every database takes far longer than a request should, so this only
            // starts it: the admin UI watches RestartCount to see it land
            _ = Task.Run(async () => {
                try { await server.SoftRestartAsync(); } catch { } // SoftRestartAsync has already logged it
            });
            return new { Started = true, Message = "Soft restart started." };
        });
        app.MapPost(path("stop-host"), () => {
            if (!server.GetRestartCapabilities().CanStopHost) throw new Exception("Stopping the host is not allowed on this server. ");
            var started = server.StopHost();
            return new { Started = started, Message = started ? "The host is stopping." : "This host cannot be stopped from here." };
        });
    }
    void mapData(WebApplication app, Func<string, string> path) {
        app.MapPost(path("queue-re-index-all"), (Guid storeId) => {
            var allIds = db(storeId).Query<object>().SelectId().Execute();
            var transaction = db(storeId).CreateTransaction();
            foreach (var id in allIds) transaction.ReIndex(id);
            ThreadPool.QueueUserWorkItem(_ => { transaction.Execute(); });
            return allIds.Count;
        });
        // a context in the request body reads as that user, culture and visibility; without one the
        // query reads with the context of the store, as everywhere else
        app.MapPost(path("query"), (Guid storeId, QueryModel query) => server.GetStore(storeId)
            .EvaluateForJsonAsync(query.Query, [.. query.Parameters.Select(ParameterModel.Convert)], QueryContextModel.Convert(query.Context)));
        app.MapPost(path("execute"), (Guid storeId, ActionModel[] actions, bool flushToDisk) => server.GetStore(storeId).ExecuteAsync(actions, flushToDisk));
        app.MapPost(path("shift-all-dates"), async (Guid storeId, int seconds) => {
            var store = container(storeId).Store;
            if (store == null) throw new Exception("Store not initialized. ");
            var transaction = store.CreateTransaction();
            var dm = store.Datastore.Datamodel;
            var propsByNodeType = dm.Properties.Values
                .Where(p => p.PropertyType == PropertyType.DateTime)
                .GroupBy(p => p.NodeType)
                .Select(g => new { NodeTypeId = g.Key, Properties = g });
            var shift = TimeSpan.FromSeconds(seconds);
            foreach (var g in propsByNodeType) {
                var nodeIds = store.QueryType(g.NodeTypeId).SelectId().Execute();
                foreach (var property in g.Properties) {
                    foreach (var id in nodeIds) {
                        transaction.AddToProperty(id, property.Id, shift);
                    }
                }
            }
            await transaction.ExecuteAsync();
            return transaction.Count;
        });
    }
    void mapDatamodel(WebApplication app, Func<string, string> path) {
        app.MapPost(path("get-code"), (Guid storeId, bool addAttributes) => ModelGen.GenerateCSharpModelCode(db(storeId).Datastore.Datamodel, addAttributes));
        //app.MapPost(path("get-model"), (Guid storeId, Guid datamodelId) => db(storeId).Datastore.Datamodel);
        app.MapPost(path("get-model"), (Guid storeId) => db(storeId).Datastore.Datamodel);
        app.MapPost(path("server"), (Guid storeId, Guid datamodelId) => db(storeId).Datastore.Datamodel);
    }
    void mapLog(WebApplication app, Func<string, string> path) {
        var logger = (Guid storeId) => container(storeId).GetLogger();
        app.MapPost(path("has-startup-exception"), (Guid storeId) => container(storeId).StartUpException != null);
        app.MapPost(path("get-startup-exception"), (Guid storeId) => {
            var c = container(storeId);
            var e = c.StartUpException;
            if (e == null) return null;
            return new { When = c.StartUpExceptionDateTimeUTC, e.Message, e.StackTrace, };
        });
        app.MapPost(path("get-log-infos"), (Guid storeId) => {
            var loggerInstance = logger(storeId);
            var keysAndNames = loggerInstance.GetLogKeysAndNames();
            return keysAndNames.Select(k => new {
                k.Key,
                Name = k.Value,
                EnabledLog = loggerInstance.IsLogEnabled(k.Key),
                EnabledStatistics = loggerInstance.IsStatisticsEnabled(k.Key),
                FirstRecord = loggerInstance.LogStore.GetTimestampOfFirstRecord(k.Key),
                LastRecord = loggerInstance.LogStore.GetTimestampOfLastRecord(k.Key),
                TotalFileSize = loggerInstance.LogStore.GetFileSize(k.Key),
                LogFileSize = loggerInstance.LogStore.GetLogFileSize(k.Key),
                StatisticsFileSize = loggerInstance.LogStore.GetStatisticsFileSize(k.Key),
            });
        });
        app.MapPost(path("get-system-trace"), (Guid storeId, int skip, int take) => db(storeId).Datastore.GetSystemTrace(skip, take));
        app.MapPost(path("enable-log"), (Guid storeId, string logKey, bool enable) => logger(storeId).EnableLog(logKey, enable));
        app.MapPost(path("is-log-enabled"), (Guid storeId, string logKey) => logger(storeId).IsLogEnabled(logKey));
        app.MapPost(path("enable-statistics"), (Guid storeId, string logKey, bool enable) => logger(storeId).EnableStatistics(logKey, enable));
        app.MapPost(path("is-statistics-enabled"), (Guid storeId, string logKey) => logger(storeId).IsStatisticsEnabled(logKey));
        app.MapPost(path("clear-log"), (Guid storeId, string logKey) => logger(storeId).ClearLog(logKey));
        app.MapPost(path("clear-statistics"), (Guid storeId, string logKey) => logger(storeId).ClearStatistics(logKey));
        app.MapPost(path("extract-log"), (Guid storeId, string logKey, DateTime from, DateTime to, int skip, int take, bool orderByDescendingDates) => logger(storeId).ExtractLog(logKey, from, to, skip, take, orderByDescendingDates, out var total));

        app.MapPost(path("set-property-hits-recording-status"), (Guid storeId, bool enabled) => logger(storeId).RecordingPropertyHits = enabled);
        app.MapPost(path("is-recording-property-hits"), (Guid storeId) => logger(storeId).RecordingPropertyHits);
        app.MapPost(path("analyze-property-hits"), (Guid storeId) => logger(storeId).AnalyzePropertyHits().Select(kv => new { PropertyName = kv.Key, HitCount = kv.Value }));

        app.MapPost(path("analyze-system-log-count"), (Guid storeId, IntervalType intervalType, DateTime from, DateTime to) => logger(storeId).AnalyseSystemLogCount(intervalType, from, to));
        app.MapPost(path("analyze-system-log-count-by-type"), (Guid storeId, IntervalType intervalType, DateTime from, DateTime to) => logger(storeId).AnalyseSystemLogCountByType(intervalType, from, to));
        app.MapPost(path("analyze-query-count"), (Guid storeId, IntervalType intervalType, DateTime from, DateTime to) => logger(storeId).AnalyseQueryCount(intervalType, from, to));
        app.MapPost(path("analyze-query-duration"), (Guid storeId, IntervalType intervalType, DateTime from, DateTime to) => logger(storeId).AnalyseQueryDuration(intervalType, from, to));
        app.MapPost(path("analyze-transaction-count"), (Guid storeId, IntervalType intervalType, DateTime from, DateTime to) => logger(storeId).AnalyseTransactionCount(intervalType, from, to));
        app.MapPost(path("analyze-transaction-duration"), (Guid storeId, IntervalType intervalType, DateTime from, DateTime to) => logger(storeId).AnalyseTransactionDuration(intervalType, from, to));
        app.MapPost(path("analyze-transaction-action"), (Guid storeId, IntervalType intervalType, DateTime from, DateTime to) => logger(storeId).AnalyseTransactionAction(intervalType, from, to));
        app.MapPost(path("analyze-action-count"), (Guid storeId, IntervalType intervalType, DateTime from, DateTime to) => logger(storeId).AnalyseActionCount(intervalType, from, to));
        app.MapPost(path("analyze-action-operations"), (Guid storeId, IntervalType intervalType, DateTime from, DateTime to) => logger(storeId).AnalyseActionOperations(intervalType, from, to));

    }
    void mapTasks(WebApplication app, Func<string, string> path) {
        app.MapPost(path("get-batch-count-queued"), (Guid storeId) => db(storeId).Datastore.TaskQueue.CountBatch(Tasks.BatchState.Pending));
        app.MapPost(path("get-batch-count-per-state"), (Guid storeId) => db(storeId).Datastore.TaskQueue.BatchCountsPerState());
        app.MapPost(path("get-batch-info"), (Guid storeId, Tasks.BatchState[] states, string[] typeIds, string[] jobIds, int page, int pageSize) => {
            return db(storeId).Datastore.TaskQueue.GetBatchMeta(states, typeIds, jobIds, page, pageSize, out var totalCount);
        });
        app.MapPost(path("set-batch-state"), (Guid storeId, Guid[] batchIds, Tasks.BatchState state) => {
            db(storeId).Datastore.TaskQueue.SetState(batchIds, state);
        });
        app.MapPost(path("delete-batch-by-id"), (Guid storeId, Guid[] batchIds) => {
            db(storeId).Datastore.TaskQueue.DeleteById(batchIds);
        });
    }
    void mapDemo(WebApplication app, Func<string, string> path) {
        app.MapPost(path("populate"), (Guid storeId, int count, bool wikipediaData) => {
            var store = db(storeId);
            var sw = new Stopwatch();
            var chunkSize = 1000;
            var created = 0;
            var path = "C:\\WAF_Sources\\wikipedia\\wiki-articles.json"; // temporary hardcoded path to wikipedia data file...
            // if linux:
            if (Environment.OSVersion.Platform == PlatformID.Unix || Environment.OSVersion.Platform == PlatformID.MacOSX) {
                // convert to WSL mapped path:
                path = "/mnt/c/WAF_Sources/wikipedia/wiki-articles.json";
            }
            var seed = 0; // same every time for reproducible results
            using IArticleGenerator generator = wikipediaData ? new WikipediaArticleGenerator(path) : new RandomArticleGenerator(seed);
            // continue from existing count:
            var existingCount = store.Query<Demo.Models.DemoArticle>().Count();
            generator.Move(existingCount);
            while (true) {
                var create = Math.Min(chunkSize, count - created);
                if (create <= 0) break;
                var articles = generator.Many(create);
                sw.Start();
                store.Insert(articles);
                sw.Stop();
                created += create;
            }
            return new {
                CountCreated = count,
                ElapsedMs = sw.Elapsed.TotalMilliseconds
            };
        });
    }
}
